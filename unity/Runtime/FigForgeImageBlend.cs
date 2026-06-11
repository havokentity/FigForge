// =============================================================================
// FigForge — live Figma blend modes for rasterized sprites. Plain UI.Image is
// foreign code, so this companion (attached next to the Image) makes baked PNG
// nodes full participants in the blend pipeline the same way LayeredRect/
// VectorGraphic/TextBlend are:
//
//   • bake     — the sprite is blitted (FigForge/ImageBake) into a cached
//                offscreen surface: textureRect sub-region, Image tint folded
//                in, output PREMULTIPLIED, inset by the AA pad ring.
//   • suppress — the live Image renderer is culled (re-asserted every canvas
//                update; Images don't fight cull the way TMP does, but the
//                pattern stays uniform across the companions).
//   • present  — a hidden child quad draws the result at the image's paint
//                position: Tier-2 modes hand the surface to the page compositor
//                and present the pre-blended texture (premult-over); Multiply/
//                Screen/PlusLighter present the surface with GPU blend state.
//
// Normal/PassThrough → the component idles and the Image renders untouched.
// The importer keeps Image.color white (node opacity is baked into the PNG),
// and whatever colour IS set gets folded into the surface by the bake — so the
// quad presents at opacity 1; CompositorOpacity stays at the serialized
// appearanceOpacity (1 from the importer) to avoid double-dimming.
//
// v1 covers Image.Type.Simple semantics: the bake stretches the sprite across
// the rect (the importer forces Simple for blend nodes — the baked PNG already
// contains the node's corners). No sprite → the component idles.
// =============================================================================

using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace FigForge
{
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("FigForge/Image Blend")]
    public class FigForgeImageBlend : MonoBehaviour, IFigForgeCompositorSource
    {
        [SerializeField] FigForgeBlendMode blendMode = FigForgeBlendMode.Normal;
        [SerializeField, Range(0f, 1f)] float appearanceOpacity = 1f;

        const float AaPadPx = 2f;
        const float MaxSurfaceScale = 16f;

        Image _image;
        RenderTexture _surface;
        Material _bakeMaterial;
        int _bakeHash;
        bool _bakeDirty = true;
        FigForgePageCompositor _pageCompositor;
        FigForgeImageBlendGraphic _present;
        readonly Vector3[] _worldCorners = new Vector3[4];
        // Cached surface scale keyed by the projection camera — recomputed only when the
        // camera actually moves, so an idle SceneView doesn't thrash the quantized scale
        // across a 0.25 boundary and re-bake the cached surface every canvas update.
        float _cachedScaleFactor = -1f;
        Matrix4x4 _scaleCamKey;

        public FigForgeBlendMode BlendMode
        {
            get => blendMode;
            set
            {
                if (blendMode == value) return;
                blendMode = value;
                Reconfigure();
            }
        }

        public void Configure(FigForgeBlendMode mode, float opacity = 1f)
        {
            blendMode = mode;
            appearanceOpacity = Mathf.Clamp01(opacity);
            Reconfigure();
        }

        void Reconfigure()
        {
            _bakeDirty = true;
            UpdatePageCompositorRegistration();
            MarkPageCompositorDirty();
        }

        bool ActiveBlend => blendMode != FigForgeBlendMode.Normal && blendMode != FigForgeBlendMode.PassThrough;

        // ---- IFigForgeCompositorSource ----------------------------------------
        public bool RequiresPageCompositor =>
            FigForgeLayeredRect.PageCompositorEnabled && FigForgeLayeredRect.BlendTier(blendMode) == 2;
        public FigForgeBlendMode CompositorBlendMode => blendMode;
        public float CompositorOpacity => appearanceOpacity;
        public float CompositorPad => AaPadPx; // sprites never overflow their rect — AA margin only
        public float CompositorBackdropBlur => 0f;          // background blur lives on panels (LayeredRect)
        public Vector4 CompositorShapeCorners => Vector4.zero;
        public RectTransform CompositorRectTransform => transform as RectTransform;
        public RenderTexture GetCompositorSurface() { EnsureSurface(); return _surface; }

        // ---- Lifecycle ---------------------------------------------------------
        void OnEnable()
        {
            _image = GetComponent<Image>();
            Canvas.willRenderCanvases -= HandleWillRenderCanvases;
            Canvas.willRenderCanvases += HandleWillRenderCanvases;
            _bakeDirty = true;
            UpdatePageCompositorRegistration();
            MarkPageCompositorDirty();
        }

        void OnDisable()
        {
            Canvas.willRenderCanvases -= HandleWillRenderCanvases;
            if (_pageCompositor != null)
            {
                _pageCompositor.Unregister(this);
                _pageCompositor = null;
            }
            SetSuppressed(false);
            DestroyPresent();
            ReleaseSurface();
            MarkPageCompositorDirty();
        }

        void OnDestroy()
        {
            if (_bakeMaterial != null)
            {
                if (Application.isPlaying) Destroy(_bakeMaterial);
                else DestroyImmediate(_bakeMaterial);
            }
            _bakeMaterial = null;
        }

        void OnRectTransformDimensionsChange()
        {
            _cachedScaleFactor = -1f; // rect size feeds the surface scale — re-evaluate it
            _bakeDirty = true;
            MarkPageCompositorDirty();
        }

        void OnTransformParentChanged()
        {
            UpdatePageCompositorRegistration();
            MarkPageCompositorDirty();
        }

        void OnCanvasHierarchyChanged()
        {
            UpdatePageCompositorRegistration();
            MarkPageCompositorDirty();
        }

#if UNITY_EDITOR
        void OnValidate()
        {
            appearanceOpacity = Mathf.Clamp01(appearanceOpacity);
            // Reconfigure can AddComponent (FindOrCreatePageCompositor), which Unity
            // disallows inside OnValidate ("SendMessage cannot be called during
            // OnValidate") — defer it to the next editor tick, guarding against the
            // component being destroyed or disabled in between.
            UnityEditor.EditorApplication.delayCall += () =>
            {
                if (this == null || !isActiveAndEnabled) return;
                Reconfigure();
            };
        }
#endif

        // ---- Per-canvas-update drive -------------------------------------------
        void HandleWillRenderCanvases()
        {
            if (_image == null) _image = GetComponent<Image>();
            // No sprite → nothing to bake or stand in for; idle without suppressing.
            bool active = ActiveBlend && _image != null && _image.sprite != null;
            SetSuppressed(active);
            if (!active)
            {
                if (_present != null) _present.enabled = false;
                return;
            }

            EnsurePresent();
            EnsureSurface();
            var blended = BlendedSurfaceOrNull();
            Texture tex = blended != null ? (Texture)blended : _surface;
            if (_present == null) return;
            if (tex == null) { _present.enabled = false; return; }
            _present.enabled = true;
            _present.Bind(tex, blended != null, blendMode,
                blended != null ? 1f : appearanceOpacity, CompositorPad);
            // Recover a rebuild the canvas consumed while the capture pass had the
            // quad culled — safe here (we're a willRenderCanvases subscriber, not
            // inside the registry's rebuild loop).
            if (_present.RebuildSkippedWhileCulled)
            {
                _present.ClearRebuildSkipped();
                _present.SetVerticesDirty();
                _present.SetMaterialDirty();
            }
        }

        // The live Image must not draw while the quad presents the sprite.
        // Re-asserted every canvas update for uniformity with the TMP companion
        // (Images don't rewrite their own cull state, but the pattern is cheap).
        void SetSuppressed(bool suppressed)
        {
            if (_image == null) return;
            var cr = _image.canvasRenderer;
            if (cr != null && cr.cull != suppressed) cr.cull = suppressed;
        }

        // ---- Offscreen surface ---------------------------------------------------
        void EnsureSurface()
        {
            if (_image == null || _image.sprite == null || _image.sprite.texture == null) return;
            var rectT = transform as RectTransform;
            if (rectT == null) return;
            var rect = rectT.rect;
            float scale = SurfaceScaleFactor();
            float pad = CompositorPad;
            int limit = MaxSurfaceTextureSize();
            int w = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(1f, rect.width + 2f * pad) * scale), 1, limit);
            int h = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(1f, rect.height + 2f * pad) * scale), 1, limit);
            int hash = ComputeBakeHash(w, h, pad, scale);
            // A lost hardware resource (device reset, aggressive editor reloads) keeps
            // the RT object but blanks its contents — treat as needing a rebake.
            if (_surface != null && !_surface.IsCreated()) _bakeDirty = true;
            if (_surface != null && _surface.width == w && _surface.height == h && hash == _bakeHash && !_bakeDirty)
                return;

            if (_surface == null || _surface.width != w || _surface.height != h)
            {
                ReleaseSurface();
                _surface = FigForgeCompositorCache.RentPageRT(w, h);
                if (_surface == null) return;
                _surface.name = "FigForgeImageSurface_" + w + "x" + h;
            }
            Bake(w, h, pad, scale, rect);
            _bakeHash = hash;
            // Unlike text, the source is always ready — a sprite asset has its pixels
            // the moment we can see it. No empty-bake retry needed.
            _bakeDirty = false;
            MarkPageCompositorDirty();
        }

        void Bake(int width, int height, float pad, float scale, Rect rect)
        {
            if (!EnsureBakeMaterial()) return;
            var sprite = _image.sprite;
            var tex = sprite.texture;
            // textureRect, NOT rect: tightly-packed sprites keep their pixels in a
            // sub-region of the (atlas) texture.
            var tr = sprite.textureRect;
            _bakeMaterial.SetColor("_Tint", _image.color);
            _bakeMaterial.SetVector("_UvRect", new Vector4(
                tr.x / tex.width, tr.y / tex.height,
                tr.width / tex.width, tr.height / tex.height));
            // Content placement matches the other bakes: the rect sits inset by the
            // pad ring in surface pixels (ceil slack lands at the top/right).
            _bakeMaterial.SetVector("_ContentRect", new Vector4(
                pad * scale / width, pad * scale / height,
                Mathf.Max(1f, rect.width * scale) / width, Mathf.Max(1f, rect.height * scale) / height));
            var previous = RenderTexture.active;
            Graphics.Blit(tex, _surface, _bakeMaterial);
            RenderTexture.active = previous;
        }

        bool EnsureBakeMaterial()
        {
            if (_bakeMaterial != null) return true;
            var shader = Shader.Find("FigForge/ImageBake");
            if (shader == null) return false;
            _bakeMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            return true;
        }

        void ReleaseSurface()
        {
            if (_surface != null) FigForgeCompositorCache.ReturnPageRT(_surface);
            _surface = null;
            _bakeHash = 0;
            _bakeDirty = true;
        }

        // There is no TEXT_CHANGED analogue for Images, so the hash IS the dirty
        // tracker: sprite swaps (FigForgeGraphicStateSprites assigns a different
        // instance), tint/opacity edits, and atlas repacks (textureRect) land here.
        int ComputeBakeHash(int width, int height, float pad, float scale)
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + width;
                h = h * 31 + height;
                h = h * 31 + Mathf.RoundToInt(pad * 100f);
                h = h * 31 + Mathf.RoundToInt(scale * 100f);
                if (_image != null)
                {
                    var sprite = _image.sprite;
                    h = h * 31 + (sprite != null ? sprite.GetInstanceID() : 0);
                    Color32 c = _image.color;
                    h = h * 31 + (c.r | (c.g << 8) | (c.b << 16) | (c.a << 24));
                    if (sprite != null)
                    {
                        var tr = sprite.textureRect;
                        h = h * 31 + Mathf.RoundToInt(tr.x * 100f);
                        h = h * 31 + Mathf.RoundToInt(tr.y * 100f);
                        h = h * 31 + Mathf.RoundToInt(tr.width * 100f);
                        h = h * 31 + Mathf.RoundToInt(tr.height * 100f);
                    }
                }
                return h;
            }
        }

#if UNITY_EDITOR
        // Dumps the whole chain to files next to the project (works even when console
        // capture is unavailable): FFImageState.txt + surface/blended PNGs with
        // covered-pixel counts — "is the bake empty / is the blend empty" at a glance.
        [ContextMenu("Save Debug Surfaces")]
        void SaveDebugSurfaces()
        {
            EnsureSurface();
            string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, ".."));
            var comp = _pageCompositor != null ? _pageCompositor : GetComponentInParent<FigForgePageCompositor>();
            var blended = BlendedSurfaceOrNull();
            var sb = new System.Text.StringBuilder();
            sb.Append("blend=").Append(blendMode)
              .Append(" sprite=").Append(_image != null && _image.sprite != null ? _image.sprite.name : "null")
              .Append(" spriteTexRect=").Append(_image != null && _image.sprite != null ? _image.sprite.textureRect.ToString() : "null")
              .Append(" imgColor=").Append(_image != null ? _image.color.ToString() : "null")
              .Append(" imgCulled=").Append(_image != null && _image.canvasRenderer != null ? _image.canvasRenderer.cull.ToString() : "?")
              .Append(" bakeDirty=").Append(_bakeDirty)
              .Append(" surface=").Append(_surface != null ? _surface.width + "x" + _surface.height + ":created=" + _surface.IsCreated() : "null")
              .Append(" surfaceCoveredPx=").Append(CoveredPixels(_surface, out string sp)).Append(sp)
              .Append(" compositor=").Append(comp != null ? comp.gameObject.name + ":owns=" + comp.ShouldRenderLayer(this) : "none")
              .Append(" blended=").Append(blended != null ? blended.width + "x" + blended.height : "null")
              .Append(" blendedCoveredPx=").Append(CoveredPixels(blended, out string bp)).Append(bp)
              .Append(" present=").Append(_present != null ? _present.enabled + ":" + (_present.mainTexture != null ? _present.mainTexture.name : "null") : "null");
            if (_surface != null) WriteRtPng(_surface, System.IO.Path.Combine(dir, "FFImageSurface.png"));
            if (blended != null) WriteRtPng(blended, System.IO.Path.Combine(dir, "FFImageBlended.png"));
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "FFImageState.txt"), sb.ToString());
            Debug.Log("[FigForge] " + sb, this);
        }

        static int CoveredPixels(RenderTexture rt, out string note)
        {
            note = "";
            if (rt == null) return -1;
            var prev = RenderTexture.active;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply(false);
            RenderTexture.active = prev;
            var px = tex.GetPixels32();
            int covered = 0;
            for (int i = 0; i < px.Length; i++) if (px[i].a > 8) covered++;
            DestroyImmediate(tex);
            note = "/" + px.Length;
            return covered;
        }

        static void WriteRtPng(RenderTexture rt, string path)
        {
            var prev = RenderTexture.active;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply(false);
            RenderTexture.active = prev;
            var px = tex.GetPixels32();
            for (int i = 0; i < px.Length; i++) px[i].a = 255;
            tex.SetPixels32(px);
            System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
            DestroyImmediate(tex);
        }

        [ContextMenu("Log Image Blend State")]
        void LogImageBlendState()
        {
            var comp = _pageCompositor != null ? _pageCompositor : GetComponentInParent<FigForgePageCompositor>();
            var blended = BlendedSurfaceOrNull();
            Debug.Log("[FigForge] ImageBlend '" + name + "': blend=" + blendMode
                + " active=" + ActiveBlend
                + " sprite=" + (_image != null && _image.sprite != null ? _image.sprite.name : "null")
                + " imgCulled=" + (_image != null && _image.canvasRenderer != null ? _image.canvasRenderer.cull.ToString() : "?")
                + " surface=" + (_surface != null ? _surface.width + "x" + _surface.height + " created=" + _surface.IsCreated() : "null")
                + " bakeDirty=" + _bakeDirty
                + " compositor=" + (comp != null ? comp.name + " owns=" + comp.ShouldRenderLayer(this) : "none")
                + " blendedRT=" + (blended != null ? blended.width + "x" + blended.height : "null")
                + " present=" + (_present != null ? "enabled=" + _present.enabled + " tex=" + (_present.mainTexture != null ? _present.mainTexture.name : "null") : "null"),
                this);
        }
#endif

        // ---- Present quad --------------------------------------------------------
        void EnsurePresent()
        {
            if (_present != null) return;
            var existing = transform.Find("__FigForgeImageBlend");
            var go = existing != null ? existing.gameObject : new GameObject("__FigForgeImageBlend", typeof(RectTransform));
            go.hideFlags = HideFlags.HideAndDontSave;
            if (go.transform.parent != transform)
                go.transform.SetParent(transform, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            rt.localScale = Vector3.one;
            rt.localRotation = Quaternion.identity;
            rt.SetAsLastSibling();
            _present = go.GetComponent<FigForgeImageBlendGraphic>();
            if (_present == null)
                _present = go.AddComponent<FigForgeImageBlendGraphic>();
            _present.raycastTarget = false;
        }

        void DestroyPresent()
        {
            if (_present == null) return;
            var go = _present.gameObject;
            _present = null;
            if (Application.isPlaying) Destroy(go);
            else DestroyImmediate(go);
        }

        // ---- Page-compositor plumbing (mirrors FigForgeTextBlend) ---------------
        RenderTexture BlendedSurfaceOrNull()
        {
            var comp = _pageCompositor != null ? _pageCompositor : GetComponentInParent<FigForgePageCompositor>();
            if (comp == null || !comp.ShouldRenderLayer(this)) return null;
            return comp.GetBlendedSurface(this);
        }

        // IFigForgeCompositorSource — the importer's post-build rebind (build-time
        // registration is suppressed so no stray canvas compositor gets minted).
        public void RebindPageCompositor() => UpdatePageCompositorRegistration();

        void UpdatePageCompositorRegistration()
        {
            if (_pageCompositor != null)
            {
                _pageCompositor.Unregister(this);
                _pageCompositor = null;
            }
            if (!RequiresPageCompositor || !isActiveAndEnabled) return;
            if (!FigForgePageCapture.CanCapture(GetComponentInParent<Canvas>())) return;
            _pageCompositor = FindOrCreatePageCompositor();
            if (_pageCompositor != null) _pageCompositor.Register(this);
        }

        FigForgePageCompositor FindOrCreatePageCompositor()
        {
            var comp = GetComponentInParent<FigForgePageCompositor>();
            if (comp != null) return comp;
            // Mid-import-build, the page root doesn't have its compositor yet —
            // creating one here would land on the canvas (stray). The importer
            // rebinds after ConfigurePageCompositor; see SuppressAutoCreate.
            if (FigForgePageCompositor.SuppressAutoCreate) return null;
            var screen = GetComponentInParent<FigForgeScreen>();
            if (screen != null) return screen.gameObject.AddComponent<FigForgePageCompositor>();
            var c = GetComponentInParent<Canvas>();
            if (c != null) return c.gameObject.AddComponent<FigForgePageCompositor>();
            return null;
        }

        void MarkPageCompositorDirty()
        {
            var comp = _pageCompositor != null ? _pageCompositor : GetComponentInParent<FigForgePageCompositor>();
            if (comp != null) comp.MarkDirty();
        }

        // ---- Surface scale (mirrors the other sources so positions line up) -----
        float SurfaceScaleFactor()
        {
            // The only per-frame-varying input is the camera projection (used by
            // RawSurfaceScaleFactor via WorldToScreenPoint). Reuse the cached quantized
            // scale unless the camera actually moved; rect-size changes invalidate it via
            // OnRectTransformDimensionsChange. This stops the every-canvas-update re-bake
            // loop where float jitter flipped the 0.25-quantized scale on an idle SceneView.
            Camera cam = ProjectionCamera(GetComponentInParent<Canvas>());
            Matrix4x4 camKey = cam != null ? cam.projectionMatrix * cam.worldToCameraMatrix : Matrix4x4.identity;
            if (_cachedScaleFactor > 0f && camKey == _scaleCamKey)
                return _cachedScaleFactor;
            _scaleCamKey = camKey;
            float scale = Mathf.Clamp(RawSurfaceScaleFactor(), 1f, MaxSurfaceScale);
            _cachedScaleFactor = Mathf.Ceil(scale * 4f) * 0.25f;
            return _cachedScaleFactor;
        }

        float RawSurfaceScaleFactor()
        {
            float scale = 1f;
            var c = GetComponentInParent<Canvas>();
            if (c != null) scale = Mathf.Max(scale, c.scaleFactor);

            var rt = transform as RectTransform;
            if (rt == null) return scale;
            var rect = rt.rect;
            if (rect.width > 0.001f && rect.height > 0.001f)
            {
                Camera cam = ProjectionCamera(c);
                rt.GetWorldCorners(_worldCorners);
                float projectedWidth = Vector2.Distance(
                    RectTransformUtility.WorldToScreenPoint(cam, _worldCorners[0]),
                    RectTransformUtility.WorldToScreenPoint(cam, _worldCorners[3]));
                float projectedHeight = Vector2.Distance(
                    RectTransformUtility.WorldToScreenPoint(cam, _worldCorners[0]),
                    RectTransformUtility.WorldToScreenPoint(cam, _worldCorners[1]));
                scale = Mathf.Max(scale, projectedWidth / rect.width, projectedHeight / rect.height);
            }
            return Mathf.Max(1f, scale);
        }

        Camera ProjectionCamera(Canvas c)
        {
            Camera cam = null;
#if UNITY_EDITOR
            if (!Application.isPlaying) cam = EditorSceneViewCamera();
#endif
            if (cam == null && c != null && c.renderMode != RenderMode.ScreenSpaceOverlay)
                cam = c.worldCamera;
            return cam != null ? cam : Camera.current;
        }

#if UNITY_EDITOR
        static Camera EditorSceneViewCamera()
        {
            var sceneViewType = System.Type.GetType("UnityEditor.SceneView,UnityEditor");
            if (sceneViewType == null) return null;
            object sceneView = null;
            var current = sceneViewType.GetProperty("currentDrawingSceneView", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (current != null) sceneView = current.GetValue(null, null);
            if (sceneView == null)
            {
                var last = sceneViewType.GetProperty("lastActiveSceneView", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (last != null) sceneView = last.GetValue(null, null);
            }
            if (sceneView == null) return null;
            var cameraProp = sceneViewType.GetProperty("camera", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            return cameraProp != null ? cameraProp.GetValue(sceneView, null) as Camera : null;
        }
#endif

        static int MaxSurfaceTextureSize()
        {
            int systemLimit = SystemInfo.maxTextureSize > 0 ? SystemInfo.maxTextureSize : 8192;
            return Mathf.Clamp(systemLimit, 4096, 16384);
        }
    }

    // The quad that stands in for the suppressed Image: padded to the surface
    // bounds, masked/clipped like any MaskableGraphic. Compositor-owned → the
    // pre-blended texture with plain premult-over; standalone Tier-1 → the baked
    // surface with fixed-function blend state (Multiply/Screen/PlusLighter).
    [RequireComponent(typeof(CanvasRenderer))]
    internal sealed class FigForgeImageBlendGraphic : MaskableGraphic
    {
        Material _material;
        Texture _texture;
        float _pad;
        bool _premultOver = true;
        FigForgeBlendMode _blend = FigForgeBlendMode.Normal;
        float _opacity = 1f;

        public override Texture mainTexture => _texture != null ? _texture : s_WhiteTexture;

#if UNITY_EDITOR
        // Diagnostic counters (read via inspector tooling): how the canvas treats us.
        public int DbgRebuildCalls { get; private set; }
        public int DbgGeometryUpdates { get; private set; }
        public int DbgMaterialUpdates { get; private set; }
        public string DbgLastSkip { get; private set; } = "";

        protected override void UpdateGeometry() { DbgGeometryUpdates++; base.UpdateGeometry(); }
        protected override void UpdateMaterial() { DbgMaterialUpdates++; base.UpdateMaterial(); }
#endif

        // uGUI CONSUMES a queued rebuild even when Graphic.Rebuild early-outs for a
        // culled renderer — and this quad is transiently culled by the page
        // compositor's capture pass. If that window swallows our only pending
        // rebuild, geometry/material never upload and the quad is permanently
        // invisible. Flag it; the owner re-dirties us from OUTSIDE the rebuild loop
        // (dirtying from inside would be rejected by the registry).
        public bool RebuildSkippedWhileCulled { get; private set; }
        public void ClearRebuildSkipped() { RebuildSkippedWhileCulled = false; }

        public override void Rebuild(CanvasUpdate update)
        {
            if (update == CanvasUpdate.PreRender)
            {
#if UNITY_EDITOR
                DbgRebuildCalls++;
#endif
                if (canvasRenderer != null && canvasRenderer.cull)
                {
                    RebuildSkippedWhileCulled = true;
#if UNITY_EDITOR
                    DbgLastSkip = "culled@" + Time.frameCount;
#endif
                }
            }
            base.Rebuild(update);
        }

        public void Bind(Texture texture, bool premultOver, FigForgeBlendMode blend, float opacity, float pad)
        {
            bool materialChanged = _texture != texture || _premultOver != premultOver
                || _blend != blend || !Mathf.Approximately(_opacity, opacity);
            bool padChanged = !Mathf.Approximately(_pad, pad);
            _texture = texture;
            _premultOver = premultOver;
            _blend = blend;
            _opacity = opacity;
            _pad = pad;
            if (materialChanged) SetMaterialDirty();
            if (padChanged) SetVerticesDirty();
        }

        public override Material material
        {
            get
            {
                if (_material == null)
                {
                    var shader = Shader.Find("FigForge/CachedQuad");
                    if (shader == null) return base.material;
                    _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                }
                Configure(_material);
                return _material;
            }
            set { }
        }

        public override Material materialForRendering
        {
            get
            {
                var rm = base.materialForRendering; // stencil-wrapped copy of `material`
                if (rm != null && rm != _material) Configure(rm);
                return rm;
            }
        }

        void Configure(Material m)
        {
            m.mainTexture = _texture != null ? _texture : Texture2D.whiteTexture;
            m.SetFloat("_AppearanceOpacity", Mathf.Clamp01(_opacity));
            m.SetFloat("_BlendMode", (float)_blend);

            // Surface is premultiplied → normal = One/OneMinusSrcAlpha; standalone
            // Tier-1 modes map to fixed-function state, mirroring the other sources.
            var src = BlendMode.One;
            var dst = BlendMode.OneMinusSrcAlpha;
            if (!_premultOver)
            {
                switch (_blend)
                {
                    case FigForgeBlendMode.Multiply:
                        src = BlendMode.DstColor; dst = BlendMode.OneMinusSrcAlpha; break;
                    case FigForgeBlendMode.Screen:
                        src = BlendMode.OneMinusDstColor; dst = BlendMode.One; break;
                    case FigForgeBlendMode.PlusLighter:
                        src = BlendMode.One; dst = BlendMode.One; break;
                }
            }
            m.SetInt("_SrcBlend", (int)src);
            m.SetInt("_DstBlend", (int)dst);
            m.SetInt("_SrcBlendA", (int)BlendMode.One);
            m.SetInt("_DstBlendA", (int)BlendMode.OneMinusSrcAlpha);
            m.SetInt("_BlendOp", (int)BlendOp.Add);
            m.SetInt("_BlendOpA", (int)BlendOp.Add);
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            color = Color.white;
            raycastTarget = false;
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            var r = GetPixelAdjustedRect();
            float x0 = r.xMin - _pad, y0 = r.yMin - _pad, x1 = r.xMax + _pad, y1 = r.yMax + _pad;
            vh.Clear();
            AddVert(vh, new Vector3(x0, y0), color, new Vector2(0f, 0f));
            AddVert(vh, new Vector3(x0, y1), color, new Vector2(0f, 1f));
            AddVert(vh, new Vector3(x1, y1), color, new Vector2(1f, 1f));
            AddVert(vh, new Vector3(x1, y0), color, new Vector2(1f, 0f));
            vh.AddTriangle(0, 1, 2);
            vh.AddTriangle(2, 3, 0);
        }

        protected override void OnDestroy()
        {
            if (_material != null)
            {
                if (Application.isPlaying) Destroy(_material);
                else DestroyImmediate(_material);
            }
            base.OnDestroy();
        }

        static void AddVert(VertexHelper vh, Vector3 pos, Color32 c, Vector2 uv)
        {
            var v = UIVertex.simpleVert;
            v.position = pos;
            v.color = c;
            v.uv0 = uv;
            vh.AddVert(v);
        }
    }
}
