// =============================================================================
// FigForge — live Figma blend modes for TMP text. TMP is foreign code, so this
// companion (attached next to the TextMeshProUGUI) makes text a full
// participant in the blend pipeline the same way LayeredRect/VectorGraphic are:
//
//   • bake     — the TMP mesh (+ TMP_SubMeshUI fallback/sprite meshes) is drawn
//                with its own font materials into a cached offscreen surface.
//                TMP's SDF shaders blend One/OneMinusSrcAlpha, so rendering onto
//                a transparent-clear RT yields a correct PREMULTIPLIED surface.
//   • suppress — the live TMP renderers are culled (re-asserted every canvas
//                update; TMP occasionally rewrites its own cull state).
//   • present  — a hidden child quad draws the result at the text's paint
//                position: Tier-2 modes hand the surface to the page compositor
//                and present the pre-blended texture (premult-over); Multiply/
//                Screen/PlusLighter present the surface with GPU blend state.
//
// Normal/PassThrough → the component idles and TMP renders untouched. Layer
// opacity is folded into the TMP vertex colour by the importer, so it is
// already baked into the surface; CompositorOpacity stays at the serialized
// appearanceOpacity (1 from the importer) to avoid double-dimming.
// =============================================================================

using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace FigForge
{
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("FigForge/Text Blend")]
    public class FigForgeTextBlend : MonoBehaviour, IFigForgeCompositorSource
    {
        [SerializeField] FigForgeBlendMode blendMode = FigForgeBlendMode.Normal;
        [SerializeField, Range(0f, 1f)] float appearanceOpacity = 1f;

        const float AaPadPx = 2f;
        const float MaxSurfaceScale = 16f;

        TMP_Text _text;
        RenderTexture _surface;
        int _bakeHash;
        bool _bakeDirty = true;
        FigForgePageCompositor _pageCompositor;
        FigForgeTextBlendGraphic _present;
        readonly List<TMP_SubMeshUI> _subMeshes = new List<TMP_SubMeshUI>();
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
        public float CompositorPad => Mathf.Ceil(CurrentPad());
        public float CompositorBackdropBlur => 0f;          // background blur lives on panels (LayeredRect)
        public Vector4 CompositorShapeCorners => Vector4.zero;
        public RectTransform CompositorRectTransform => transform as RectTransform;
        public RenderTexture GetCompositorSurface() { EnsureSurface(); return _surface; }

        // ---- Lifecycle ---------------------------------------------------------
        void OnEnable()
        {
            _text = GetComponent<TMP_Text>();
            Canvas.willRenderCanvases -= HandleWillRenderCanvases;
            Canvas.willRenderCanvases += HandleWillRenderCanvases;
            TMPro_EventManager.TEXT_CHANGED_EVENT.Remove(OnTextChanged);
            TMPro_EventManager.TEXT_CHANGED_EVENT.Add(OnTextChanged);
            _bakeDirty = true;
            UpdatePageCompositorRegistration();
            MarkPageCompositorDirty();
        }

        void OnDisable()
        {
            Canvas.willRenderCanvases -= HandleWillRenderCanvases;
            TMPro_EventManager.TEXT_CHANGED_EVENT.Remove(OnTextChanged);
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

        void OnTextChanged(Object obj)
        {
            if (_text == null || !ReferenceEquals(obj, _text)) return;
            _bakeDirty = true;
            MarkPageCompositorDirty();
        }

        // ---- Per-canvas-update drive -------------------------------------------
        void HandleWillRenderCanvases()
        {
            if (_text == null) _text = GetComponent<TMP_Text>();
            bool active = ActiveBlend && _text != null;
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

        // The live TMP renderers must not draw while the quad presents the text.
        // Re-asserted every canvas update: TMP's own clipping/culling code rewrites
        // canvasRenderer.cull on its own schedule.
        void SetSuppressed(bool suppressed)
        {
            if (_text == null) return;
            var cr = _text.canvasRenderer;
            if (cr != null && cr.cull != suppressed) cr.cull = suppressed;
            _subMeshes.Clear();
            GetComponentsInChildren(false, _subMeshes);
            for (int i = 0; i < _subMeshes.Count; i++)
            {
                var sub = _subMeshes[i] != null ? _subMeshes[i].canvasRenderer : null;
                if (sub != null && sub.cull != suppressed) sub.cull = suppressed;
            }
        }

        // ---- Offscreen surface ---------------------------------------------------
        void EnsureSurface()
        {
            if (_text == null) return;
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
                _surface.name = "FigForgeTextSurface_" + w + "x" + h;
            }
            Bake(w, h, pad, scale, rect);
            _bakeHash = hash;
            // Baking before TMP has generated its mesh (scene load, domain reload,
            // canvas-update ordering) yields an empty surface — the text would vanish
            // and STAY gone if the matching TEXT_CHANGED never reaches us. Keep the
            // bake dirty until a non-empty mesh arrives (unless the text really is
            // empty), so the next canvas update retries.
            bool bakedEmpty = (_text.mesh == null || _text.mesh.vertexCount == 0)
                && !string.IsNullOrEmpty(_text.text);
            _bakeDirty = bakedEmpty;
            MarkPageCompositorDirty();
        }

        void Bake(int width, int height, float pad, float scale, Rect rect)
        {
            var cmd = new CommandBuffer { name = "FigForgeTextBake" };
            cmd.SetRenderTarget(_surface);
            cmd.ClearRenderTarget(true, true, Color.clear);
            // Pixel-space ortho, unflipped — matches the vector bake convention so the
            // present quad (uv 0,0 = bottom-left) samples the surface upright.
            var proj = Matrix4x4.Ortho(0f, width, 0f, height, -1f, 100f);
            proj = GL.GetGPUProjectionMatrix(proj, false);
            cmd.SetViewProjectionMatrices(Matrix4x4.identity, proj);
            // TMP meshes are authored in the rect's local space (y-up, pivot origin):
            // surfacePx = (local - rect.min + pad) * scale.
            var m = Matrix4x4.TRS(
                new Vector3((pad - rect.xMin) * scale, (pad - rect.yMin) * scale, 0f),
                Quaternion.identity,
                new Vector3(scale, scale, 1f));

            var mainCr = _text.canvasRenderer;
            var mainMat = mainCr != null ? mainCr.GetMaterial() : null;
            if (mainMat == null) mainMat = _text.fontSharedMaterial;
            if (_text.mesh != null && _text.mesh.vertexCount > 0 && mainMat != null)
                cmd.DrawMesh(_text.mesh, m, mainMat, 0, 0);

            _subMeshes.Clear();
            GetComponentsInChildren(false, _subMeshes);
            for (int i = 0; i < _subMeshes.Count; i++)
            {
                var sub = _subMeshes[i];
                if (sub == null || sub.mesh == null || sub.mesh.vertexCount == 0) continue;
                var subCr = sub.canvasRenderer;
                var subMat = subCr != null ? subCr.GetMaterial() : null;
                if (subMat == null) subMat = sub.sharedMaterial;
                if (subMat != null)
                    cmd.DrawMesh(sub.mesh, m, subMat, 0, 0);
            }

            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();
        }

        void ReleaseSurface()
        {
            if (_surface != null) FigForgeCompositorCache.ReturnPageRT(_surface);
            _surface = null;
            _bakeHash = 0;
            _bakeDirty = true;
        }

        // TEXT_CHANGED drives most rebakes; the hash is the belt-and-braces layer for
        // changes that can slip past the event (same-length text swaps settle through
        // the event, but colour/material edits and missed events land here).
        int ComputeBakeHash(int width, int height, float pad, float scale)
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + width;
                h = h * 31 + height;
                h = h * 31 + Mathf.RoundToInt(pad * 100f);
                h = h * 31 + Mathf.RoundToInt(scale * 100f);
                if (_text != null)
                {
                    h = h * 31 + (_text.mesh != null ? _text.mesh.vertexCount : 0);
                    h = h * 31 + (_text.text != null ? _text.text.GetHashCode() : 0);
                    Color32 c = _text.color;
                    h = h * 31 + (c.r | (c.g << 8) | (c.b << 16) | (c.a << 24));
                    h = h * 31 + (_text.fontSharedMaterial != null ? _text.fontSharedMaterial.GetInstanceID() : 0);
                }
                return h;
            }
        }

#if UNITY_EDITOR
        // Dumps the whole chain to files next to the project (works even when console
        // capture is unavailable): FFTextState.txt + surface/blended PNGs with
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
              .Append(" tmpMeshVerts=").Append(_text != null && _text.mesh != null ? _text.mesh.vertexCount : -1)
              .Append(" tmpText='").Append(_text != null ? _text.text : "null")
              .Append("' tmpCulled=").Append(_text != null && _text.canvasRenderer != null ? _text.canvasRenderer.cull.ToString() : "?")
              .Append(" bakeDirty=").Append(_bakeDirty)
              .Append(" surface=").Append(_surface != null ? _surface.width + "x" + _surface.height + ":created=" + _surface.IsCreated() : "null")
              .Append(" surfaceCoveredPx=").Append(CoveredPixels(_surface, out string sp)).Append(sp)
              .Append(" compositor=").Append(comp != null ? comp.gameObject.name + ":owns=" + comp.ShouldRenderLayer(this) : "none")
              .Append(" blended=").Append(blended != null ? blended.width + "x" + blended.height : "null")
              .Append(" blendedCoveredPx=").Append(CoveredPixels(blended, out string bp)).Append(bp)
              .Append(" present=").Append(_present != null ? _present.enabled + ":" + (_present.mainTexture != null ? _present.mainTexture.name : "null") : "null");
            if (_surface != null) WriteRtPng(_surface, System.IO.Path.Combine(dir, "FFTextSurface.png"));
            if (blended != null) WriteRtPng(blended, System.IO.Path.Combine(dir, "FFTextBlended.png"));
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "FFTextState.txt"), sb.ToString());
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

        [ContextMenu("Log Text Blend State")]
        void LogTextBlendState()
        {
            var comp = _pageCompositor != null ? _pageCompositor : GetComponentInParent<FigForgePageCompositor>();
            var blended = BlendedSurfaceOrNull();
            Debug.Log("[FigForge] TextBlend '" + name + "': blend=" + blendMode
                + " active=" + ActiveBlend
                + " tmpMeshVerts=" + (_text != null && _text.mesh != null ? _text.mesh.vertexCount : -1)
                + " tmpCulled=" + (_text != null && _text.canvasRenderer != null ? _text.canvasRenderer.cull.ToString() : "?")
                + " surface=" + (_surface != null ? _surface.width + "x" + _surface.height + " created=" + _surface.IsCreated() : "null")
                + " bakeDirty=" + _bakeDirty
                + " compositor=" + (comp != null ? comp.name + " owns=" + comp.ShouldRenderLayer(this) : "none")
                + " blendedRT=" + (blended != null ? blended.width + "x" + blended.height : "null")
                + " present=" + (_present != null ? "enabled=" + _present.enabled + " tex=" + (_present.mainTexture != null ? _present.mainTexture.name : "null") : "null"),
                this);
        }
#endif

        // Pad covering glyph overflow beyond the rect (ascenders/descenders, TMP
        // material effects pushing mesh bounds out, auto-size overshoot) + AA margin.
        float CurrentPad()
        {
            float pad = AaPadPx;
            var rectT = transform as RectTransform;
            if (_text != null && rectT != null && _text.mesh != null && _text.mesh.vertexCount > 0)
            {
                var b = _text.mesh.bounds;
                var r = rectT.rect;
                float overflow = Mathf.Max(
                    Mathf.Max(r.xMin - b.min.x, b.max.x - r.xMax),
                    Mathf.Max(r.yMin - b.min.y, b.max.y - r.yMax));
                if (overflow > 0f) pad = Mathf.Max(pad, overflow + 2f);
            }
            return pad;
        }

        // ---- Present quad --------------------------------------------------------
        void EnsurePresent()
        {
            if (_present != null) return;
            var existing = transform.Find("__FigForgeTextBlend");
            var go = existing != null ? existing.gameObject : new GameObject("__FigForgeTextBlend", typeof(RectTransform));
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
            _present = go.GetComponent<FigForgeTextBlendGraphic>();
            if (_present == null)
                _present = go.AddComponent<FigForgeTextBlendGraphic>();
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

        // ---- Page-compositor plumbing (mirrors FigForgeVectorGraphic) -----------
        RenderTexture BlendedSurfaceOrNull()
        {
            var comp = _pageCompositor != null ? _pageCompositor : GetComponentInParent<FigForgePageCompositor>();
            if (comp == null || !comp.ShouldRenderLayer(this)) return null;
            return comp.GetBlendedSurface(this);
        }

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

    // The quad that stands in for the suppressed TMP: padded to the surface
    // bounds, masked/clipped like any MaskableGraphic. Compositor-owned → the
    // pre-blended texture with plain premult-over; standalone Tier-1 → the baked
    // surface with fixed-function blend state (Multiply/Screen/PlusLighter).
    [RequireComponent(typeof(CanvasRenderer))]
    internal sealed class FigForgeTextBlendGraphic : MaskableGraphic
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
