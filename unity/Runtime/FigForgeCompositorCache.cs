using System.Collections.Generic;
using UnityEngine;

namespace FigForge
{
    static class FigForgeCompositorCache
    {
        const long MaxBytes = 64L * 1024L * 1024L;
        static readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>();
        static readonly Dictionary<string, Stack<RenderTexture>> _pool = new Dictionary<string, Stack<RenderTexture>>();
        static long _bytes;
        static int _clock;

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
            {
                if (kv.Value.texture != null)
                    kv.Value.texture.Release();
            }
            _entries.Clear();

            foreach (var stack in _pool.Values)
            {
                while (stack.Count > 0)
                {
                    var rt = stack.Pop();
                    if (rt != null) rt.Release();
                }
            }
            _pool.Clear();
            _bytes = 0;
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

        static void ApplyLayerBlur(RenderTexture target, Material blur, Vector4 blurParams)
        {
            var temp = RenderTexture.GetTemporary(target.width, target.height, 0, RenderTextureFormat.ARGB32);
            temp.filterMode = FilterMode.Bilinear;
            temp.wrapMode = TextureWrapMode.Clamp;

            blur.SetVector("_BlurParams", blurParams);
            blur.SetVector("_Direction", new Vector4(1f, 0f, 0f, 0f));
            Graphics.Blit(target, temp, blur);

            blur.SetVector("_Direction", new Vector4(0f, 1f, 0f, 0f));
            Graphics.Blit(temp, target, blur);

            RenderTexture.ReleaseTemporary(temp);
        }

        static RenderTexture Rent(int width, int height)
        {
            string bucket = BucketKey(width, height);
            if (_pool.TryGetValue(bucket, out var stack))
            {
                while (stack.Count > 0)
                {
                    var rt = stack.Pop();
                    if (rt != null) return rt;
                }
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
            string bucket = BucketKey(rt.width, rt.height);
            if (!_pool.TryGetValue(bucket, out var stack))
            {
                stack = new Stack<RenderTexture>();
                _pool[bucket] = stack;
            }
            stack.Push(rt);
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

        static string BucketKey(int width, int height) => width + "x" + height;
        static long EstimateBytes(int width, int height) => (long)width * height * 4L;

        struct Entry
        {
            public RenderTexture texture;
            public long bytes;
            public int refCount;
            public int lastUsed;
        }
    }
}
