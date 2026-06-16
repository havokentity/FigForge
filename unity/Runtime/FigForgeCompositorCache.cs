using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace FigForge
{
    static class FigForgeCompositorCache
    {
        const long MaxBytes = 64L * 1024L * 1024L;
        // Idle pooled RTs get their own (smaller) budget: the pool only ever hits on
        // an EXACT size match (same-size re-bakes after eviction, enable/disable
        // cycles), but surface sizes track rect size x camera scale, so after a
        // resize the old-size buckets would otherwise pile up unreleased for the
        // whole session. Total GPU residency is bounded by MaxBytes + MaxPooledBytes.
        const long MaxPooledBytes = 16L * 1024L * 1024L;
        static readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>();
        static readonly Dictionary<string, PoolBucket> _pool = new Dictionary<string, PoolBucket>();
        static long _bytes;
        static long _pooledBytes;
        static int _clock;

#if UNITY_EDITOR
        // Statics reset on domain reload but the HideAndDontSave RTs survive it —
        // without this hook every reload strands the live cache AND the pool on the
        // GPU until the editor closes.
        static FigForgeCompositorCache()
        {
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += Clear;
        }
#endif

        public static RenderTexture Acquire(string key, int width, int height, Texture paintTexture, Material resolver,
                                            Material blur = null, Vector4 blurParams = default(Vector4))
        {
            if (string.IsNullOrEmpty(key) || resolver == null) return null;

            if (_entries.TryGetValue(key, out var entry) && entry.texture != null)
            {
                entry.refCount++;
                entry.lastUsed = ++_clock;
                _entries[key] = entry;
                return entry.texture;
            }

            var rt = Rent(width, height);
            Bake(rt, paintTexture != null ? paintTexture : Texture2D.whiteTexture, resolver);
            if (blur != null && blurParams.x > 0.5f && Mathf.Max(blurParams.z, blurParams.w) > 0.001f)
                ApplyLayerBlur(rt, blur, blurParams);
            entry = new Entry
            {
                texture = rt,
                bytes = EstimateBytes(width, height),
                refCount = 1,
                lastUsed = ++_clock,
            };
            _entries[key] = entry;
            _bytes += entry.bytes;
            Trim();
            return rt;
        }

        // Bake an arbitrary triangle MESH (verts in surface pixel space, bottom-left
        // origin) into a cached surface — the vector-graphic analogue of Acquire's
        // fullscreen blit. The mesh's per-vertex colours are flattened with
        // premultiplied-over blending so overlapping fill/stroke/AA composite once.
        public static RenderTexture AcquireMesh(string key, int width, int height, Mesh mesh, Material material)
        {
            if (string.IsNullOrEmpty(key) || material == null || mesh == null) return null;

            if (_entries.TryGetValue(key, out var entry) && entry.texture != null)
            {
                entry.refCount++;
                entry.lastUsed = ++_clock;
                _entries[key] = entry;
                return entry.texture;
            }

            var rt = Rent(width, height);
            BakeMesh(rt, mesh, material);
            entry = new Entry
            {
                texture = rt,
                bytes = EstimateBytes(width, height),
                refCount = 1,
                lastUsed = ++_clock,
            };
            _entries[key] = entry;
            _bytes += entry.bytes;
            Trim();
            return rt;
        }

        static void BakeMesh(RenderTexture target, Mesh mesh, Material material)
        {
            var cmd = new CommandBuffer { name = "FigForgeVectorBake" };
            cmd.SetRenderTarget(target);
            cmd.ClearRenderTarget(true, true, Color.clear);
            // Pixel-space ortho so mesh verts in [0,w]x[0,h] (bottom-left origin) map
            // 1:1 to the surface. The mesh is already authored in RT pixel space, so
            // keep the projection unflipped; otherwise node-local top content lands at
            // the bottom of the cached surface.
            var proj = Matrix4x4.Ortho(0f, target.width, 0f, target.height, -1f, 100f);
            proj = GL.GetGPUProjectionMatrix(proj, false);
            cmd.SetViewProjectionMatrices(Matrix4x4.identity, proj);
            cmd.DrawMesh(mesh, Matrix4x4.identity, material, 0, 0);
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();
        }

        public static void Release(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (!_entries.TryGetValue(key, out var entry)) return;
            entry.refCount = Mathf.Max(0, entry.refCount - 1);
            entry.lastUsed = ++_clock;
            _entries[key] = entry;
            Trim();
        }

        public static void Clear()
        {
            foreach (var kv in _entries)
                ReleaseRT(kv.Value.texture);
            _entries.Clear();

            foreach (var bucket in _pool.Values)
            {
                while (bucket.stack.Count > 0)
                    ReleaseRT(bucket.stack.Pop());
            }
            _pool.Clear();
            _bytes = 0;
            _pooledBytes = 0;
        }

        public static RenderTexture RentPageRT(int width, int height)
        {
            return Rent(Mathf.Max(1, width), Mathf.Max(1, height));
        }

        public static void ReturnPageRT(RenderTexture rt)
        {
            Return(rt);
        }

        static void Bake(RenderTexture target, Texture paintTexture, Material resolver)
        {
            var previous = RenderTexture.active;
            Graphics.SetRenderTarget(target);
            GL.Clear(true, true, Color.clear);
            Graphics.Blit(paintTexture, target, resolver);
            RenderTexture.active = previous;
        }

        // FigForge/LayerBlur spaces its 9 taps blur*0.29596 px apart (sigma = blur/2
        // with the kernel's intrinsic 1.689-tap sigma). Past ~2px spacing (blur 6.75)
        // the taps stop overlapping and the kernel degenerates into a comb that rings
        // as concentric bands, so larger radii run the kernel on a downsampled copy
        // and/or iterate bounded-step passes (per-pass sigmas add in quadrature).
        const float StepScalePerPass = 0.29596f;
        const float MaxBlurPerPass = 6.75f;
        // Uniform blurs downsample until the per-level blur fits two bounded-step
        // passes. Blurring deeper (where a single pass would do) makes the result's
        // width wobble +-5% with the edge's sub-texel phase on the coarse grid —
        // the chain's box prefilter is too weak an AA for sigma < ~2 level px — so
        // stay shallow enough for sigma >= ~2.4 and let the second pass both wash
        // out the grid phase and Gaussianize the kernel's +-4-tap truncation
        // (verified against erf edge profiles: +-0.5% across phases, +1..2% bias).
        const float DownsampleBlurBound = 9.5f;
        // Progressive gradients keep their sharp end at full resolution by iterating
        // instead of downsampling (resampling would soften the blur=0 end); up to
        // MaxBlurPasses passes cover 6.75*sqrt(32) ~= 38px of blur per level before
        // downsampling kicks in after all.
        const int MaxBlurPasses = 32;
        const float MaxProgressiveBlurPerLevel = 38f;

        public static void ApplyLayerBlur(RenderTexture target, Material blur, Vector4 blurParams)
        {
            float maxBlur = Mathf.Max(blurParams.z, blurParams.w);
            bool progressive = blurParams.y > 0.5f && Mathf.Abs(blurParams.z - blurParams.w) > 0.001f;

            // Uniform blurs run the kernel on a downsampled copy — the radius shrinks
            // with the surface, so the tap spacing stays bounded and the whole blit
            // chain is cheaper than one full-res pass. Progressive blurs iterate at
            // full res (pass sigmas add in quadrature) so the sharp end never gets
            // resampled, and only fall back to downsampling past ~38px.
            float levelBound = progressive ? MaxProgressiveBlurPerLevel : DownsampleBlurBound;
            int scale = 1;
            {
                // Halve only while the level dims divide exactly (the first halving
                // is exempt — phase jitter from an odd full-res dim is sub-pixel):
                // an inexact 2:1 step lets the sampling phase drift across the
                // image, which adds blur that grows with scale^2.
                int dw = target.width, dh = target.height;
                while (maxBlur / scale > levelBound && dw / 2 >= 4 && dh / 2 >= 4
                       && (scale == 1 || ((dw & 1) == 0 && (dh & 1) == 0)))
                {
                    dw /= 2; dh /= 2; scale *= 2;
                }
            }

            // The resample chain blurs on its own (each bilinear halving is a 2x2 box,
            // each doubling a tent): variance (scale^2 - 1)/3 per axis. Shrink the
            // kernel so the combined falloff still lands on Figma's sigma = blur/2.
            float extraVar = (scale * scale - 1) / 3f;
            float startBlur = CompensateResampleBlur(blurParams.z, extraVar);
            float endBlur = CompensateResampleBlur(blurParams.w, extraVar);

            float levelBlur = Mathf.Max(startBlur, endBlur) / scale;
            int passes = Mathf.Clamp(Mathf.CeilToInt(levelBlur * levelBlur / (MaxBlurPerPass * MaxBlurPerPass)),
                                     scale > 1 ? 2 : 1, MaxBlurPasses);
            float stepScale = StepScalePerPass / Mathf.Sqrt(passes);

            int w = target.width, h = target.height;
            RenderTexture small = null;
            for (int s = 1; s < scale; s *= 2)
            {
                w = Mathf.Max(1, w / 2);
                h = Mathf.Max(1, h / 2);
                var next = RentBlurTemp(w, h);
                Graphics.Blit(small != null ? (Texture)small : target, next);
                if (small != null) RenderTexture.ReleaseTemporary(small);
                small = next;
            }

            var surface = small != null ? small : target;
            var temp = RentBlurTemp(w, h);
            blur.SetVector("_BlurParams", new Vector4(blurParams.x, blurParams.y, startBlur / scale, endBlur / scale));
            for (int i = 0; i < passes; i++)
            {
                blur.SetVector("_Direction", new Vector4(1f, 0f, stepScale, 0f));
                Graphics.Blit(surface, temp, blur);
                blur.SetVector("_Direction", new Vector4(0f, 1f, stepScale, 0f));
                Graphics.Blit(temp, surface, blur);
            }
            RenderTexture.ReleaseTemporary(temp);

            // Progressive doubling keeps each upsample at 2x so the bilinear
            // magnification stays smooth even from deep levels.
            while (small != null)
            {
                w = Mathf.Min(target.width, w * 2);
                h = Mathf.Min(target.height, h * 2);
                bool last = w >= target.width && h >= target.height;
                var up = last ? target : RentBlurTemp(w, h);
                Graphics.Blit(small, up);
                RenderTexture.ReleaseTemporary(small);
                small = last ? null : up;
            }
        }

        static float CompensateResampleBlur(float blurPx, float extraVar)
        {
            float sigma = 0.5f * Mathf.Max(0f, blurPx);
            return 2f * Mathf.Sqrt(Mathf.Max(0f, sigma * sigma - extraVar));
        }

        static RenderTexture RentBlurTemp(int width, int height)
        {
            var temp = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
            temp.filterMode = FilterMode.Bilinear;
            temp.wrapMode = TextureWrapMode.Clamp;
            return temp;
        }

        static RenderTexture Rent(int width, int height)
        {
            string key = BucketKey(width, height);
            if (_pool.TryGetValue(key, out var bucket))
            {
                while (bucket.stack.Count > 0)
                {
                    var rt = bucket.stack.Pop();
                    _pooledBytes -= bucket.bytesPerItem;
                    if (rt != null) return rt;
                }
                _pool.Remove(key); // drained — don't accrue empty buckets per size
            }

            var created = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32)
            {
                name = "FigForgeSurface_" + width + "x" + height,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false,
                hideFlags = HideFlags.HideAndDontSave,
            };
            created.Create();
            return created;
        }

        static void Return(RenderTexture rt)
        {
            if (rt == null) return;
            string key = BucketKey(rt.width, rt.height);
            if (!_pool.TryGetValue(key, out var bucket))
            {
                bucket = new PoolBucket { bytesPerItem = EstimateBytes(rt.width, rt.height) };
                _pool[key] = bucket;
            }
            bucket.stack.Push(rt);
            bucket.lastReturned = ++_clock;
            _pooledBytes += bucket.bytesPerItem;
            TrimPool();
        }

        static void Trim()
        {
            while (_bytes > MaxBytes && _entries.Count > 0)
            {
                string oldestKey = null;
                Entry oldest = default;
                foreach (var kv in _entries)
                {
                    if (kv.Value.refCount > 0) continue;
                    if (oldestKey == null || kv.Value.lastUsed < oldest.lastUsed)
                    {
                        oldestKey = kv.Key;
                        oldest = kv.Value;
                    }
                }
                if (oldestKey == null) break;
                _entries.Remove(oldestKey);
                _bytes -= oldest.bytes;
                Return(oldest.texture);
            }
        }

        // Over budget, release whole buckets oldest-returned first: a stale-size
        // bucket is exactly the resize residue that never gets rented again, while
        // the bucket the caller just returned into is by definition the newest and
        // survives for same-size re-rents.
        static void TrimPool()
        {
            while (_pooledBytes > MaxPooledBytes)
            {
                string oldestKey = null;
                PoolBucket oldest = null;
                foreach (var kv in _pool)
                {
                    if (kv.Value.stack.Count == 0) continue;
                    if (oldest == null || kv.Value.lastReturned < oldest.lastReturned)
                    {
                        oldestKey = kv.Key;
                        oldest = kv.Value;
                    }
                }
                if (oldest == null) break;
                while (oldest.stack.Count > 0 && _pooledBytes > MaxPooledBytes)
                {
                    ReleaseRT(oldest.stack.Pop());
                    _pooledBytes -= oldest.bytesPerItem;
                }
                if (oldest.stack.Count == 0) _pool.Remove(oldestKey);
            }
        }

        // Release frees the GPU side; the managed shell is HideAndDontSave (never
        // scene-collected) so it needs an explicit Destroy too.
        static void ReleaseRT(RenderTexture rt)
        {
            if (rt == null) return;
            // Trims can fire mid-frame from inside a render dispatch (a layered rect
            // freeing its cached surface during the compositor's nested
            // ForceUpdateCanvases), where a prior blit/capture may have left this very
            // RT bound. Releasing the active target makes Unity warn every frame; clear
            // it first. Anything downstream rebinds its own target before drawing.
            if (RenderTexture.active == rt) RenderTexture.active = null;
            rt.Release();
            if (Application.isPlaying) Object.Destroy(rt);
            else Object.DestroyImmediate(rt);
        }

        static string BucketKey(int width, int height) => width + "x" + height;
        static long EstimateBytes(int width, int height) => (long)width * height * 4L;

        struct Entry
        {
            public RenderTexture texture;
            public long bytes;
            public int refCount;
            public int lastUsed;
        }

        sealed class PoolBucket
        {
            public readonly Stack<RenderTexture> stack = new Stack<RenderTexture>();
            public long bytesPerItem;
            public int lastReturned;
        }
    }
}
