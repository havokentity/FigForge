using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace FigForge
{
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("FigForge/Page Compositor")]
    public class FigForgePageCompositor : MonoBehaviour
    {
        readonly List<FigForgeLayeredRect> _advancedLayers = new List<FigForgeLayeredRect>();
        readonly List<FigForgeLayeredRect> _paintLayers = new List<FigForgeLayeredRect>();
        readonly Vector3[] _worldCorners = new Vector3[4];

        RectTransform _rectTransform;
        FigForgePageCompositeGraphic _presentGraphic;
        Material _compositeMaterial;
        RenderTexture _front;
        RenderTexture _back;
        bool _dirty = true;
        int _rtWidth;
        int _rtHeight;
        int _layoutHash;
        float _surfaceScale = 1f;

        public bool IsActive => isActiveAndEnabled && ActiveAdvancedCount() > 0;

        public void Register(FigForgeLayeredRect layer)
        {
            if (layer == null || _advancedLayers.Contains(layer)) return;
            _advancedLayers.Add(layer);
            MarkDirty();
        }

        public void Unregister(FigForgeLayeredRect layer)
        {
            if (layer == null) return;
            if (_advancedLayers.Remove(layer))
                MarkDirty();
        }

        public bool ShouldRenderLayer(FigForgeLayeredRect layer)
        {
            return IsActive
                && layer != null
                && layer.isActiveAndEnabled
                && layer.transform.IsChildOf(transform);
        }

        public void MarkDirty()
        {
            _dirty = true;
        }

        void OnEnable()
        {
            _rectTransform = transform as RectTransform;
            Canvas.willRenderCanvases -= HandleWillRenderCanvases;
            Canvas.willRenderCanvases += HandleWillRenderCanvases;
            MarkDirty();
        }

        void OnDisable()
        {
            Canvas.willRenderCanvases -= HandleWillRenderCanvases;
            ReleaseRenderTextures();
            if (_presentGraphic != null)
                _presentGraphic.SetTexture(null);
        }

        void OnDestroy()
        {
            Canvas.willRenderCanvases -= HandleWillRenderCanvases;
            ReleaseRenderTextures();
            DestroyRuntimeMaterial(_compositeMaterial);
        }

        void OnTransformChildrenChanged()
        {
            MarkDirty();
        }

        void OnRectTransformDimensionsChange()
        {
            MarkDirty();
        }

        void HandleWillRenderCanvases()
        {
            PruneAdvancedLayers();
            if (!IsActive)
            {
                if (_presentGraphic != null)
                    _presentGraphic.enabled = false;
                return;
            }

            EnsurePresentGraphic();
            if (_presentGraphic != null)
                _presentGraphic.enabled = true;

            if (UpdateTargetSize() || LayoutChanged())
                MarkDirty();

            if (!_dirty) return;
            Rebuild();
        }

        void Rebuild()
        {
            if (_rectTransform == null) _rectTransform = transform as RectTransform;
            if (_rectTransform == null) return;
            if (!EnsureRenderTextures() || !EnsureCompositeMaterial()) return;

            CollectPaintLayers();
            Clear(_front);

            for (int i = 0; i < _paintLayers.Count; i++)
            {
                var layer = _paintLayers[i];
                if (!ShouldRenderLayer(layer)) continue;

                var surface = layer.GetCompositorSurface();
                if (surface == null) continue;

                // MVP: all FigForge layers in this page subtree are replayed through
                // the same compositor shader. Foreign uGUI is intentionally not part
                // of the backdrop yet, which keeps Built-in pipeline support simple.
                _compositeMaterial.SetTexture("_Source", surface);
                _compositeMaterial.SetTexture("_Backdrop", _front);
                _compositeMaterial.SetFloat("_BlendMode", (float)layer.CompositorBlendMode);
                _compositeMaterial.SetFloat("_AppearanceOpacity", layer.CompositorOpacity);
                _compositeMaterial.SetVector("_SourceRect", LayerSourceRect(layer, surface));
                _compositeMaterial.SetVector("_PageSize", new Vector4(_rtWidth, _rtHeight, 0f, 0f));
                _compositeMaterial.SetVector("_ClipRect", new Vector4(-1000000000f, -1000000000f, 1000000000f, 1000000000f));

                Graphics.Blit(_front, _back, _compositeMaterial);
                Swap(ref _front, ref _back);
            }

            if (_presentGraphic != null)
                _presentGraphic.SetTexture(_front);
            _dirty = false;
        }

        bool EnsureCompositeMaterial()
        {
            if (_compositeMaterial != null) return true;
            var shader = Shader.Find("FigForge/Composite");
            if (shader == null) return false;
            _compositeMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            return true;
        }

        bool EnsureRenderTextures()
        {
            if (_rtWidth <= 0 || _rtHeight <= 0)
                UpdateTargetSize();
            if (_rtWidth <= 0 || _rtHeight <= 0) return false;
            if (_front != null && _back != null && _front.width == _rtWidth && _front.height == _rtHeight)
                return true;

            ReleaseRenderTextures();
            _front = FigForgeCompositorCache.RentPageRT(_rtWidth, _rtHeight);
            _back = FigForgeCompositorCache.RentPageRT(_rtWidth, _rtHeight);
            if (_front == null || _back == null)
            {
                ReleaseRenderTextures();
                return false;
            }
            _front.name = "FigForgePageFront_" + _rtWidth + "x" + _rtHeight;
            _back.name = "FigForgePageBack_" + _rtWidth + "x" + _rtHeight;
            return true;
        }

        void ReleaseRenderTextures()
        {
            if (_front != null) FigForgeCompositorCache.ReturnPageRT(_front);
            if (_back != null) FigForgeCompositorCache.ReturnPageRT(_back);
            _front = null;
            _back = null;
        }

        bool UpdateTargetSize()
        {
            if (_rectTransform == null) _rectTransform = transform as RectTransform;
            if (_rectTransform == null) return false;

            var rect = _rectTransform.rect;
            float scale = SurfaceScaleFactor();
            int limit = MaxSurfaceTextureSize();
            int width = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(1f, rect.width) * scale), 1, limit);
            int height = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(1f, rect.height) * scale), 1, limit);
            bool changed = width != _rtWidth || height != _rtHeight || !Mathf.Approximately(scale, _surfaceScale);
            _rtWidth = width;
            _rtHeight = height;
            _surfaceScale = scale;
            return changed;
        }

        float SurfaceScaleFactor()
        {
            float scale = 1f;
            var c = GetComponentInParent<Canvas>();
            if (c != null) scale = Mathf.Max(scale, c.scaleFactor);

            if (_rectTransform != null)
            {
                var rect = _rectTransform.rect;
                if (rect.width > 0.001f && rect.height > 0.001f)
                {
                    Camera cam = ProjectionCamera(c);
                    _rectTransform.GetWorldCorners(_worldCorners);
                    float projectedWidth = Vector2.Distance(
                        RectTransformUtility.WorldToScreenPoint(cam, _worldCorners[0]),
                        RectTransformUtility.WorldToScreenPoint(cam, _worldCorners[3]));
                    float projectedHeight = Vector2.Distance(
                        RectTransformUtility.WorldToScreenPoint(cam, _worldCorners[0]),
                        RectTransformUtility.WorldToScreenPoint(cam, _worldCorners[1]));
                    scale = Mathf.Max(scale, projectedWidth / rect.width, projectedHeight / rect.height);
                }
            }

            return Mathf.Ceil(Mathf.Clamp(scale, 1f, 16f) * 4f) * 0.25f;
        }

        Camera ProjectionCamera(Canvas c)
        {
            Camera cam = null;
#if UNITY_EDITOR
            if (!Application.isPlaying)
                cam = EditorSceneViewCamera();
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

        Vector4 LayerSourceRect(FigForgeLayeredRect layer, RenderTexture surface)
        {
            var pageRect = _rectTransform.rect;
            var bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(_rectTransform, layer.CompositorRectTransform);
            float pad = layer.CompositorPad;
            float x = (bounds.min.x - pad - pageRect.xMin) * _surfaceScale;
            float y = (bounds.min.y - pad - pageRect.yMin) * _surfaceScale;
            return new Vector4(x, y, surface.width, surface.height);
        }

        bool LayoutChanged()
        {
            int hash = ComputeLayoutHash();
            if (hash == _layoutHash) return false;
            _layoutHash = hash;
            return true;
        }

        int ComputeLayoutHash()
        {
            unchecked
            {
                int hash = 17;
                if (_rectTransform != null)
                {
                    var rect = _rectTransform.rect;
                    hash = hash * 31 + Quantized(rect.width);
                    hash = hash * 31 + Quantized(rect.height);
                }

                CollectPaintLayers();
                for (int i = 0; i < _paintLayers.Count; i++)
                {
                    var layer = _paintLayers[i];
                    if (layer == null) continue;
                    var bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(_rectTransform, layer.CompositorRectTransform);
                    hash = hash * 31 + Quantized(bounds.min.x);
                    hash = hash * 31 + Quantized(bounds.min.y);
                    hash = hash * 31 + Quantized(bounds.size.x);
                    hash = hash * 31 + Quantized(bounds.size.y);
                    hash = hash * 31 + layer.transform.GetSiblingIndex();
                    hash = hash * 31 + (int)layer.CompositorBlendMode;
                }
                hash = hash * 31 + Quantized(_surfaceScale);
                return hash;
            }
        }

        static int Quantized(float value)
        {
            return Mathf.RoundToInt(value * 100f);
        }

        void CollectPaintLayers()
        {
            _paintLayers.Clear();
            if (!IsActive) return;
            GetComponentsInChildren(false, _paintLayers);
            for (int i = _paintLayers.Count - 1; i >= 0; i--)
            {
                var layer = _paintLayers[i];
                if (layer == null || !layer.isActiveAndEnabled || !layer.transform.IsChildOf(transform))
                    _paintLayers.RemoveAt(i);
            }
            _paintLayers.Sort(CompareHierarchy);
        }

        static int CompareHierarchy(FigForgeLayeredRect a, FigForgeLayeredRect b)
        {
            if (a == b) return 0;
            if (a == null) return -1;
            if (b == null) return 1;
            return CompareTransforms(a.transform, b.transform);
        }

        static int CompareTransforms(Transform a, Transform b)
        {
            if (a == b) return 0;
            var pathA = HierarchyPath(a);
            var pathB = HierarchyPath(b);
            int count = Mathf.Min(pathA.Count, pathB.Count);
            for (int i = 0; i < count; i++)
            {
                int diff = pathA[i] - pathB[i];
                if (diff != 0) return diff;
            }
            return pathA.Count - pathB.Count;
        }

        static List<int> HierarchyPath(Transform t)
        {
            var path = new List<int>();
            while (t != null)
            {
                path.Add(t.GetSiblingIndex());
                t = t.parent;
            }
            path.Reverse();
            return path;
        }

        int ActiveAdvancedCount()
        {
            PruneAdvancedLayers();
            return _advancedLayers.Count;
        }

        void PruneAdvancedLayers()
        {
            for (int i = _advancedLayers.Count - 1; i >= 0; i--)
            {
                var layer = _advancedLayers[i];
                if (layer == null || !layer.isActiveAndEnabled || !layer.RequiresPageCompositor || !layer.transform.IsChildOf(transform))
                    _advancedLayers.RemoveAt(i);
            }
        }

        static void Clear(RenderTexture target)
        {
            var previous = RenderTexture.active;
            Graphics.SetRenderTarget(target);
            GL.Clear(true, true, Color.clear);
            RenderTexture.active = previous;
        }

        static void Swap(ref RenderTexture a, ref RenderTexture b)
        {
            var temp = a;
            a = b;
            b = temp;
        }

        static int MaxSurfaceTextureSize()
        {
            int systemLimit = SystemInfo.maxTextureSize > 0 ? SystemInfo.maxTextureSize : 8192;
            return Mathf.Clamp(systemLimit, 4096, 16384);
        }

        void EnsurePresentGraphic()
        {
            if (_presentGraphic != null) return;

            var existing = transform.Find("__FigForgePageComposite");
            var go = existing != null ? existing.gameObject : new GameObject("__FigForgePageComposite", typeof(RectTransform));
            go.hideFlags = HideFlags.HideAndDontSave;
            if (go.transform.parent != transform)
                go.transform.SetParent(transform, false);
            go.transform.SetAsFirstSibling();

            var rt = go.transform as RectTransform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            rt.localScale = Vector3.one;
            rt.localRotation = Quaternion.identity;

            _presentGraphic = go.GetComponent<FigForgePageCompositeGraphic>();
            if (_presentGraphic == null)
                _presentGraphic = go.AddComponent<FigForgePageCompositeGraphic>();
            _presentGraphic.raycastTarget = false;
            _presentGraphic.color = Color.white;
        }

        static void DestroyRuntimeMaterial(Material material)
        {
            if (material == null) return;
            if (Application.isPlaying) Destroy(material);
            else DestroyImmediate(material);
        }
    }

    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class FigForgePageCompositeGraphic : MaskableGraphic
    {
        Material _material;
        Texture _texture;

        public override Texture mainTexture => _texture != null ? _texture : s_WhiteTexture;

        public void SetTexture(Texture texture)
        {
            if (_texture == texture) return;
            _texture = texture;
            SetMaterialDirty();
            SetVerticesDirty();
        }

        public override Material material
        {
            get
            {
                if (_material == null)
                {
                    var shader = Shader.Find("FigForge/CachedQuad");
                    if (shader != null)
                    {
                        _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                        _material.SetFloat("_AppearanceOpacity", 1f);
                        _material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
                        _material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                        _material.SetInt("_SrcBlendA", (int)UnityEngine.Rendering.BlendMode.One);
                        _material.SetInt("_DstBlendA", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                        _material.SetInt("_BlendOp", (int)BlendOp.Add);
                        _material.SetInt("_BlendOpA", (int)BlendOp.Add);
                    }
                }
                return _material != null ? _material : base.material;
            }
            set { }
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
            vh.Clear();
            AddVert(vh, new Vector3(r.xMin, r.yMin), color, new Vector2(0f, 0f));
            AddVert(vh, new Vector3(r.xMin, r.yMax), color, new Vector2(0f, 1f));
            AddVert(vh, new Vector3(r.xMax, r.yMax), color, new Vector2(1f, 1f));
            AddVert(vh, new Vector3(r.xMax, r.yMin), color, new Vector2(1f, 0f));
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

        static void AddVert(VertexHelper vh, Vector3 pos, Color32 color, Vector2 uv)
        {
            var v = UIVertex.simpleVert;
            v.position = pos;
            v.color = color;
            v.uv0 = uv;
            vh.AddVert(v);
        }
    }
}
