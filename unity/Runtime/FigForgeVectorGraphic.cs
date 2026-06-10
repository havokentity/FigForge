// =============================================================================
// FigForge — procedural vector renderer. Streams the flattened triangle mesh
// (fills + strokes, anti-aliased) produced by the exporter's tessellator into a
// CanvasRenderer. Crisp at any scale, tintable by Graphic.color (so button/
// toggle state transitions work), mask-friendly, and — like FigForgeLayeredRect
// — a full participant in the Figma blend-mode pipeline:
//
//   • Normal / PassThrough  → draw the mesh straight to the canvas (cheapest).
//   • Multiply/Screen/Plus  → flatten the mesh to an offscreen surface, then
//                             present it with fixed-function GPU blend state.
//   • Advanced (Overlay, …) → flatten to a surface and hand it to the
//                             FigForgePageCompositor, which blends it against the
//                             rendered backdrop (destination-reading modes).
//
// Verts arrive in node-local px (top-left origin, +y down) and map into the
// RectTransform on every rebuild, so the drawing follows the rect's size.
// =============================================================================

using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace FigForge
{
    [RequireComponent(typeof(CanvasRenderer))]
    [AddComponentMenu("FigForge/FigForge Vector Graphic")]
    public class FigForgeVectorGraphic : MaskableGraphic, IFigForgeCompositorSource
    {
        [SerializeField] Vector2 _bounds = Vector2.one;          // node px (w,h) the verts live in
        [SerializeField] float[] _verts;                         // x0,y0,x1,y1,… node-local px, top-left, +y down
        [SerializeField] int[] _tris;                            // triangle indices into _verts
        [SerializeField] Color32[] _colors;                      // per-vertex base colour (region colour × AA alpha)
        [SerializeField] FigForgeBlendMode _blendMode = FigForgeBlendMode.Normal;
        [SerializeField, Range(0f, 1f)] float _appearanceOpacity = 1f;
        [SerializeField] int _contentHash;

        const float MaxCachedSurfaceScale = 16f;
        const float AaPadPx = 2f; // px margin baked around the rect so the AA fringe isn't clipped
        const int SurfaceShaderRevision = 2;

        Material _bakeMaterial;
        Material _presentMaterial;
        Mesh _bakeMesh;
        RenderTexture _cachedSurface;
        string _cachedSurfaceKey;
        int _cachedSurfaceWidth;
        int _cachedSurfaceHeight;
        FigForgePageCompositor _pageCompositor;
        readonly Vector3[] _worldCorners = new Vector3[4];

        // ---- IFigForgeCompositorSource ----------------------------------------
        // Tier-2 blends ride the page compositor (real-backdrop blending) alongside
        // FigForgeLayeredRect; registration is gated on a capturable canvas there.
        public bool RequiresPageCompositor =>
            FigForgeLayeredRect.PageCompositorEnabled && FigForgeLayeredRect.BlendTier(_blendMode) == 2;
        public FigForgeBlendMode CompositorBlendMode => _blendMode;
        public float CompositorOpacity => _appearanceOpacity;
        public float CompositorPad => AaPadPx;
        public float CompositorBackdropBlur => 0f;          // background blur lives on panels (LayeredRect)
        public Vector4 CompositorShapeCorners => Vector4.zero;
        public RectTransform CompositorRectTransform => rectTransform;
        public RenderTexture GetCompositorSurface() { EnsureSurface(); return _cachedSurface; }

        public bool HasMesh =>
            _verts != null && _verts.Length >= 6 && _tris != null && _tris.Length >= 3;

        bool NeedsFlatten => _blendMode != FigForgeBlendMode.Normal && _blendMode != FigForgeBlendMode.PassThrough;

        bool IsRenderedByPageCompositor()
        {
            var comp = _pageCompositor != null ? _pageCompositor : GetComponentInParent<FigForgePageCompositor>();
            return comp != null && comp.ShouldRenderLayer(this);
        }

        // The compositor's pre-blended premultiplied texture for this layer, or null
        // when this graphic isn't compositor-owned (or hasn't been composited yet).
        RenderTexture BlendedSurfaceOrNull()
        {
            var comp = _pageCompositor != null ? _pageCompositor : GetComponentInParent<FigForgePageCompositor>();
            if (comp == null || !comp.ShouldRenderLayer(this)) return null;
            return comp.GetBlendedSurface(this);
        }

        // Standalone = needs a flattened surface but no active page compositor owns
        // it (e.g. a lone Multiply glyph) → present the surface ourselves with GPU
        // blend state.
        bool PresentingStandalone() => NeedsFlatten && _cachedSurface != null && !IsRenderedByPageCompositor();

        // Presenting a quad at all — standalone flatten OR compositor-owned (where
        // the quad shows the compositor's pre-blended texture).
        bool PresentingQuad() => PresentingStandalone() || IsRenderedByPageCompositor();

        public override Texture mainTexture
        {
            get
            {
                var blended = BlendedSurfaceOrNull();
                if (blended != null) return blended;
                // Compositor-owned but not yet composited → cached surface (normal
                // alpha) rather than a white flash from the default texture.
                if (PresentingQuad() && _cachedSurface != null) return _cachedSurface;
                return Texture2D.whiteTexture;
            }
        }

        // Bake a drawing onto this graphic. `colors` is per-vertex, region colour
        // pre-multiplied by AA alpha (NOT by opacity — opacity is applied at present).
        public void Configure(Vector2 bounds, float[] verts, int[] tris, Color32[] colors,
                              bool raycast, FigForgeBlendMode blendMode, float opacity)
        {
            _bounds = new Vector2(bounds.x > 0f ? bounds.x : 1f, bounds.y > 0f ? bounds.y : 1f);
            _verts = verts;
            _tris = tris;
            _colors = colors;
            _blendMode = blendMode;
            _appearanceOpacity = Mathf.Clamp01(opacity);
            _contentHash = ComputeContentHash();
            raycastTarget = raycast;
            ReleaseSurface();
            UpdatePageCompositorRegistration();
            MarkPageCompositorDirty();
            SetVerticesDirty();
            SetMaterialDirty();
        }

        // ---- Mesh population --------------------------------------------------
        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            if (!HasMesh) return;

            // Compositor-owned: same padded present quad, textured with the
            // compositor's pre-blended result instead of the raw surface.
            if (IsRenderedByPageCompositor())
            {
                EnsureSurface();
                BuildPresentQuad(vh);
                return;
            }
            // Non-normal blend with no compositor → flatten + present with GPU blend.
            if (NeedsFlatten)
            {
                EnsureSurface();
                if (_cachedSurface != null) { BuildPresentQuad(vh); return; }
                // surface unavailable → fall through and draw directly (best effort)
            }
            BuildMeshDirect(vh);
        }

        void BuildMeshDirect(VertexHelper vh)
        {
            var r = GetPixelAdjustedRect();
            float w = _bounds.x, h = _bounds.y;
            int n = _verts.Length / 2;
            int colN = _colors != null ? _colors.Length : 0;
            Color tint = color;
            tint.a *= _appearanceOpacity; // opacity is live on the direct path

            var ui = UIVertex.simpleVert;
            for (int i = 0; i < n; i++)
            {
                float u = _verts[i * 2] / w;
                float v = _verts[i * 2 + 1] / h;
                ui.position = new Vector3(r.x + u * r.width, r.yMax - v * r.height, 0f);
                Color baseCol = i < colN ? (Color)_colors[i] : Color.white;
                ui.color = baseCol * tint;
                vh.AddVert(ui);
            }
            for (int t = 0; t + 2 < _tris.Length; t += 3)
            {
                int a = _tris[t], b = _tris[t + 1], c = _tris[t + 2];
                if (a < n && b < n && c < n) vh.AddTriangle(a, b, c);
            }
        }

        // A padded-rect quad textured with the cached surface; the present material's
        // GPU blend state does the compositing against the canvas.
        void BuildPresentQuad(VertexHelper vh)
        {
            var r = GetPixelAdjustedRect();
            float pad = CompositorPad;
            float x0 = r.xMin - pad, y0 = r.yMin - pad, x1 = r.xMax + pad, y1 = r.yMax + pad;
            Color32 c = color;
            AddQuadVert(vh, new Vector3(x0, y0), c, new Vector2(0f, 0f));
            AddQuadVert(vh, new Vector3(x0, y1), c, new Vector2(0f, 1f));
            AddQuadVert(vh, new Vector3(x1, y1), c, new Vector2(1f, 1f));
            AddQuadVert(vh, new Vector3(x1, y0), c, new Vector2(1f, 0f));
            vh.AddTriangle(0, 1, 2);
            vh.AddTriangle(2, 3, 0);
        }

        static void AddQuadVert(VertexHelper vh, Vector3 pos, Color32 c, Vector2 uv)
        {
            var v = UIVertex.simpleVert;
            v.position = pos;
            v.color = c;
            v.uv0 = uv;
            vh.AddVert(v);
        }

        // ---- Materials --------------------------------------------------------
        public override Material material
        {
            get
            {
                if (PresentingQuad())
                {
                    var m = EnsurePresentMaterial();
                    ConfigurePresentMaterial(m);
                    return m;
                }
                return base.material;
            }
            set { base.material = value; }
        }

        public override Material materialForRendering
        {
            get
            {
                if (!PresentingQuad()) return base.materialForRendering;
                EnsurePresentMaterial();
                var rm = base.materialForRendering; // stencil-wrapped copy of `material`
                if (rm != null) ConfigurePresentMaterial(rm);
                return rm;
            }
        }

        Material EnsurePresentMaterial()
        {
            if (_presentMaterial != null) return _presentMaterial;
            var shader = Shader.Find("FigForge/CachedQuad");
            if (shader != null)
                _presentMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            return _presentMaterial;
        }

        Material EnsureBakeMaterial()
        {
            if (_bakeMaterial != null) return _bakeMaterial;
            var shader = Shader.Find("FigForge/VectorBake");
            if (shader != null)
                _bakeMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            return _bakeMaterial;
        }

        void ConfigurePresentMaterial(Material m)
        {
            if (m == null) return;
            // Compositor-owned: present the pre-blended texture; opacity is already
            // baked into its coverage at composite time.
            var blended = BlendedSurfaceOrNull();
            m.mainTexture = blended != null ? blended
                : (_cachedSurface != null ? (Texture)_cachedSurface : Texture2D.whiteTexture);
            m.SetFloat("_AppearanceOpacity", blended != null ? 1f : Mathf.Clamp01(_appearanceOpacity));
            m.SetFloat("_BlendMode", (float)_blendMode);

            // Surface is premultiplied → normal = One/OneMinusSrcAlpha. Tier-1 modes
            // map to fixed-function blend; advanced modes degrade to normal here
            // (they should be owned by a page compositor instead).
            var src = BlendMode.One;
            var dst = BlendMode.OneMinusSrcAlpha;
            switch (_blendMode)
            {
                case FigForgeBlendMode.Multiply:
                    src = BlendMode.DstColor; dst = BlendMode.OneMinusSrcAlpha; break;
                case FigForgeBlendMode.Screen:
                    src = BlendMode.OneMinusDstColor; dst = BlendMode.One; break;
                case FigForgeBlendMode.PlusLighter:
                    src = BlendMode.One; dst = BlendMode.One; break;
            }
            m.SetInt("_SrcBlend", (int)src);
            m.SetInt("_DstBlend", (int)dst);
            m.SetInt("_SrcBlendA", (int)BlendMode.One);
            m.SetInt("_DstBlendA", (int)BlendMode.OneMinusSrcAlpha);
            m.SetInt("_BlendOp", (int)BlendOp.Add);
            m.SetInt("_BlendOpA", (int)BlendOp.Add);
        }

        // ---- Offscreen surface ------------------------------------------------
        void EnsureSurface()
        {
            var bake = EnsureBakeMaterial();
            if (bake == null || !HasMesh) return;

            if (!CurrentSurfaceDescriptor(out int width, out int height, out float pad, out float scale, out string key))
                return;

            if (_cachedSurface != null && _cachedSurfaceKey == key
                && _cachedSurfaceWidth == width && _cachedSurfaceHeight == height)
                return;

            ReleaseSurface();
            BuildBakeMesh(pad, scale, rectTransform.rect);
            _cachedSurface = FigForgeCompositorCache.AcquireMesh(key, width, height, _bakeMesh, bake);
            if (_cachedSurface == null) return;
            _cachedSurfaceKey = key;
            _cachedSurfaceWidth = width;
            _cachedSurfaceHeight = height;
        }

        bool CurrentSurfaceDescriptor(out int width, out int height, out float pad, out float scale, out string key)
        {
            scale = SurfaceScaleFactor();
            var rect = rectTransform.rect;
            pad = CompositorPad;
            int limit = MaxSurfaceTextureSize();
            width = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(1f, rect.width + 2f * pad) * scale), 1, limit);
            height = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(1f, rect.height + 2f * pad) * scale), 1, limit);
            key = BuildSurfaceCacheKey(width, height, pad, scale, rect);
            return !string.IsNullOrEmpty(key);
        }

        string BuildSurfaceCacheKey(int width, int height, float pad, float scale, Rect rect)
        {
            unchecked
            {
                uint hash = 2166136261u;
                Hash(ref hash, SurfaceShaderRevision);
                Hash(ref hash, _contentHash);
                Hash(ref hash, width);
                Hash(ref hash, height);
                Hash(ref hash, pad);
                Hash(ref hash, scale);
                Hash(ref hash, rect.width);
                Hash(ref hash, rect.height);
                return "vec:" + hash.ToString("X8");
            }
        }

        void CheckCachedSurfaceValidity()
        {
            if (_cachedSurface == null || string.IsNullOrEmpty(_cachedSurfaceKey))
                return;

            if (!CurrentSurfaceDescriptor(out int width, out int height, out _, out _, out string key))
                return;

            if (_cachedSurfaceKey == key && _cachedSurfaceWidth == width && _cachedSurfaceHeight == height)
                return;

            ReleaseSurface();
            MarkPageCompositorDirty();
            SetVerticesDirty();
            SetMaterialDirty();
        }

        void HandleWillRenderCanvases()
        {
            CheckCachedSurfaceValidity();
        }

        // Mesh in surface PIXEL space. CachedQuad/PageCompositor sample with uv.y=1 at
        // the displayed top edge, so node-local top (v=0) must bake into high pixel-y.
        void BuildBakeMesh(float pad, float scale, Rect rect)
        {
            if (_bakeMesh == null)
                _bakeMesh = new Mesh { name = "FigForgeVectorBake", hideFlags = HideFlags.HideAndDontSave };
            _bakeMesh.Clear();

            float w = _bounds.x > 0f ? _bounds.x : 1f;
            float h = _bounds.y > 0f ? _bounds.y : 1f;
            int n = _verts.Length / 2;
            int colN = _colors != null ? _colors.Length : 0;
            // In a linear project the (sRGB) surface RT encodes on write, so the bake
            // must output LINEAR colour — matching how LayeredRect bakes its surfaces,
            // so the two composite together consistently. (Direct-draw uses UGUI's own
            // colour handling, so only the surface bake needs this.)
            bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
            var positions = new Vector3[n];
            var cols = new Color32[n];
            for (int i = 0; i < n; i++)
            {
                float u = _verts[i * 2] / w;
                float v = _verts[i * 2 + 1] / h;
                float sx = (pad + u * rect.width) * scale;
                float sy = (pad + (1f - v) * rect.height) * scale;
                positions[i] = new Vector3(sx, sy, 0f);
                Color32 c = i < colN ? _colors[i] : (Color32)Color.white;
                if (linear) { Color lc = ((Color)c).linear; lc.a = c.a / 255f; c = lc; }
                cols[i] = c;
            }
            _bakeMesh.vertices = positions;
            _bakeMesh.colors32 = cols;
            _bakeMesh.triangles = _tris;
            _bakeMesh.RecalculateBounds();
        }

        void ReleaseSurface()
        {
            if (!string.IsNullOrEmpty(_cachedSurfaceKey))
                FigForgeCompositorCache.Release(_cachedSurfaceKey);
            _cachedSurface = null;
            _cachedSurfaceKey = null;
            _cachedSurfaceWidth = 0;
            _cachedSurfaceHeight = 0;
        }

        // ---- Surface scale (mirrors FigForgeLayeredRect so positions line up) --
        float SurfaceScaleFactor()
        {
            float scale = Mathf.Clamp(RawSurfaceScaleFactor(), 1f, MaxCachedSurfaceScale);
            return Mathf.Ceil(scale * 4f) * 0.25f;
        }

        float RawSurfaceScaleFactor()
        {
            float scale = 1f;
            var c = canvas;
            if (c != null) scale = Mathf.Max(scale, c.scaleFactor);

            var rt = rectTransform;
            var rect = rt.rect;
            if (rect.width > 0.001f && rect.height > 0.001f)
            {
                Camera cam = ProjectionCamera();
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

        Camera ProjectionCamera()
        {
            Camera cam = null;
#if UNITY_EDITOR
            if (!Application.isPlaying) cam = EditorSceneViewCamera();
#endif
            var c = canvas;
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

        static void Hash(ref uint h, int v)
        {
            unchecked { h = (h ^ (uint)v) * 16777619u; }
        }

        static void Hash(ref uint h, float v)
        {
            Hash(ref h, Mathf.RoundToInt(v * 1000f));
        }

        // ---- Page-compositor registration (advanced modes only) ---------------
        void UpdatePageCompositorRegistration()
        {
            if (_pageCompositor != null)
            {
                _pageCompositor.Unregister(this);
                _pageCompositor = null;
            }
            if (!RequiresPageCompositor || !isActiveAndEnabled) return;
            // No capturable canvas (Screen Space - Overlay etc.) → stay on the
            // per-graphic fallback path.
            if (!FigForgePageCapture.CanCapture(canvas)) return;
            _pageCompositor = FindOrCreatePageCompositor();
            if (_pageCompositor != null) _pageCompositor.Register(this);
        }

        FigForgePageCompositor FindOrCreatePageCompositor()
        {
            var comp = GetComponentInParent<FigForgePageCompositor>();
            if (comp != null) return comp;
            var screen = GetComponentInParent<FigForgeScreen>();
            if (screen != null) return screen.gameObject.AddComponent<FigForgePageCompositor>();
            var c = canvas;
            if (c != null) return c.gameObject.AddComponent<FigForgePageCompositor>();
            return null;
        }

        void MarkPageCompositorDirty()
        {
            var comp = _pageCompositor != null ? _pageCompositor : GetComponentInParent<FigForgePageCompositor>();
            if (comp != null) comp.MarkDirty();
        }

        int ComputeContentHash()
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + _bounds.x.GetHashCode();
                h = h * 31 + _bounds.y.GetHashCode();
                if (_verts != null) for (int i = 0; i < _verts.Length; i++) h = h * 31 + _verts[i].GetHashCode();
                if (_tris != null) for (int i = 0; i < _tris.Length; i++) h = h * 31 + _tris[i];
                if (_colors != null) for (int i = 0; i < _colors.Length; i++)
                    { var c = _colors[i]; h = h * 31 + (c.r | (c.g << 8) | (c.b << 16) | (c.a << 24)); }
                return h;
            }
        }

        // ---- Lifecycle --------------------------------------------------------
        protected override void OnEnable()
        {
            Canvas.willRenderCanvases -= HandleWillRenderCanvases;
            Canvas.willRenderCanvases += HandleWillRenderCanvases;
            base.OnEnable();
            UpdatePageCompositorRegistration();
            SetVerticesDirty();
            SetMaterialDirty();
        }

        protected override void OnRectTransformDimensionsChange()
        {
            base.OnRectTransformDimensionsChange();
            ReleaseSurface();
            MarkPageCompositorDirty();
            SetVerticesDirty();
            SetMaterialDirty();
        }

        protected override void OnCanvasHierarchyChanged()
        {
            base.OnCanvasHierarchyChanged();
            ReleaseSurface();
            UpdatePageCompositorRegistration();
            MarkPageCompositorDirty();
            SetMaterialDirty();
        }

        protected override void OnDisable()
        {
            Canvas.willRenderCanvases -= HandleWillRenderCanvases;
            if (_pageCompositor != null)
            {
                _pageCompositor.Unregister(this);
                _pageCompositor = null;
            }
            ReleaseSurface();
            base.OnDisable();
        }

        protected override void OnDestroy()
        {
            Canvas.willRenderCanvases -= HandleWillRenderCanvases;
            ReleaseSurface();
            DestroyRuntimeMaterial(_bakeMaterial);
            DestroyRuntimeMaterial(_presentMaterial);
            if (_bakeMesh != null)
            {
                if (Application.isPlaying) Destroy(_bakeMesh); else DestroyImmediate(_bakeMesh);
            }
            base.OnDestroy();
        }

        static void DestroyRuntimeMaterial(Material material)
        {
            if (material == null) return;
            if (Application.isPlaying) Destroy(material); else DestroyImmediate(material);
        }
    }
}
