// =============================================================================
// FigForge — page compositor. Gives Tier-2 (destination-reading) Figma blend
// modes — Overlay, Soft Light, Difference, Darken, Color Dodge/Burn, Hue/Sat/
// Color/Luminosity — a REAL backdrop on both pipelines, GrabPass-free:
//
//   1. Capture: render the page through its own camera (FigForgePageCapture)
//      with every graphic at-or-above the layer in paint order culled, so the
//      backdrop contains exactly what Figma blends against — foreign uGUI
//      (TMP text, plain Images) included, content above the layer excluded.
//   2. Blend:   one blit per layer (FigForge/Composite) sampling the layer's
//      cached premultiplied surface against the capture at its screen rect,
//      producing a premultiplied blended texture (coverage in alpha).
//   3. Present: the layer's OWN graphic draws that texture as a normal
//      premult-over quad at its hierarchy position — so masking (_ClipRect /
//      stencil), raycast targets, and z-order against foreign content all
//      behave like any other graphic. There is no full-page present.
//
// Layers chain: a lower Tier-2 layer's blended quad stays visible in a higher
// layer's capture, so stacked blend modes composite through each other.
//
// Performance: captures and blits run only when dirty (source content/layout
// changes, layout-hash drift, camera resize). When clean the quads are static
// textured quads — zero per-frame compositor work. Changes to FOREIGN graphics
// beneath a blend layer are not auto-detected; call MarkDirty() if you animate
// or reorder content under a Tier-2 layer at runtime. (In the editor, hierarchy
// edits mark the compositor dirty automatically.)
// =============================================================================

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace FigForge
{
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("FigForge/Page Compositor")]
    public class FigForgePageCompositor : MonoBehaviour
    {
        readonly List<IFigForgeCompositorSource> _advancedLayers = new List<IFigForgeCompositorSource>();
        readonly List<IFigForgeCompositorSource> _orderedLayers = new List<IFigForgeCompositorSource>();
        readonly Dictionary<IFigForgeCompositorSource, RenderTexture> _blended = new Dictionary<IFigForgeCompositorSource, RenderTexture>();
        readonly List<IFigForgeCompositorSource> _orphanScratch = new List<IFigForgeCompositorSource>();
        readonly List<Graphic> _canvasGraphics = new List<Graphic>();
        readonly List<CanvasRenderer> _culled = new List<CanvasRenderer>();

        Material _compositeMaterial;
        bool _dirty = true;
        bool _inRebuild;
        int _layoutHash;

        public bool IsActive => isActiveAndEnabled && ActiveAdvancedCount() > 0;

        // Whether this page can be composited at all: needs a Screen Space - Camera
        // canvas to capture through. Sources check this BEFORE registering and stay
        // on their per-graphic fallback path (BIRP GrabPass / URP alpha) otherwise.
        public bool CanComposite => FigForgePageCapture.CanCapture(GetComponentInParent<Canvas>());

        public void Register(IFigForgeCompositorSource layer)
        {
            if (layer == null || _advancedLayers.Contains(layer)) return;
            _advancedLayers.Add(layer);
            MarkDirty();
        }

        public void Unregister(IFigForgeCompositorSource layer)
        {
            if (layer == null) return;
            if (_advancedLayers.Remove(layer))
            {
                ReleaseBlendedTarget(layer);
                MarkDirty();
            }
        }

        public bool ShouldRenderLayer(IFigForgeCompositorSource layer)
        {
            return IsActive
                && layer != null
                && layer.isActiveAndEnabled
                && _advancedLayers.Contains(layer)
                && layer.transform.IsChildOf(transform);
        }

        // The premultiplied blended texture the layer's graphic presents. Null until
        // the first rebuild after registration (the graphic falls back to its cached
        // surface for that frame).
        public RenderTexture GetBlendedSurface(IFigForgeCompositorSource layer)
        {
            return layer != null && _blended.TryGetValue(layer, out var rt) ? rt : null;
        }

        public void MarkDirty()
        {
            _dirty = true;
        }

        void OnEnable()
        {
            Canvas.willRenderCanvases -= HandleWillRenderCanvases;
            Canvas.willRenderCanvases += HandleWillRenderCanvases;
#if UNITY_EDITOR
            // The compositor only gets OnTransformChildrenChanged for its own
            // GameObject, so deep hierarchy edits (reordering text above/below a
            // blend layer) would otherwise leave a stale backdrop while authoring.
            // Runtime reorders still need an explicit MarkDirty().
            UnityEditor.EditorApplication.hierarchyChanged -= HandleHierarchyChanged;
            UnityEditor.EditorApplication.hierarchyChanged += HandleHierarchyChanged;
#endif
            MarkDirty();
        }

        void OnDisable()
        {
            Canvas.willRenderCanvases -= HandleWillRenderCanvases;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.hierarchyChanged -= HandleHierarchyChanged;
#endif
            ReleaseAllBlendedTargets();
        }

        void OnDestroy()
        {
            Canvas.willRenderCanvases -= HandleWillRenderCanvases;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.hierarchyChanged -= HandleHierarchyChanged;
#endif
            ReleaseAllBlendedTargets();
            DestroyRuntimeMaterial(_compositeMaterial);
        }

#if UNITY_EDITOR
        void HandleHierarchyChanged()
        {
            MarkDirty();
        }
#endif

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
            if (_inRebuild) return;
            PruneAdvancedLayers();
            if (!IsActive) return;

            var cam = FigForgePageCapture.ResolveCamera(GetComponentInParent<Canvas>());
            if (cam == null) return;

            SortOrderedLayers();
            if (LayoutChanged(cam)) MarkDirty();
            if (!_dirty) return;

            _inRebuild = true;
            try { Rebuild(cam); }
            finally { _inRebuild = false; }
        }

        void Rebuild(Camera cam)
        {
            if (!EnsureCompositeMaterial()) return;

            // Allocate/refresh the per-layer targets FIRST, so the present quads bind
            // the final RT instances during the forced canvas update below. Later
            // blits write into the same instances — no further rebinding needed, and
            // lower layers' quads show current content in higher layers' captures.
            for (int i = 0; i < _orderedLayers.Count; i++)
                EnsureBlendedTarget(_orderedLayers[i]);

            // Make quad geometry/material bindings current regardless of where this
            // handler sits in the willRenderCanvases subscription order (re-entrancy
            // guarded by _inRebuild).
            Canvas.ForceUpdateCanvases();

            CollectCanvasGraphics();
            bool complete = true;
            for (int i = 0; i < _orderedLayers.Count; i++)
            {
                var layer = _orderedLayers[i];
                var surface = layer.GetCompositorSurface();
                var target = GetBlendedSurface(layer);
                if (surface == null || target == null) continue;

                CullAtOrAbove(layer);
                var capture = FigForgePageCapture.CaptureTemporary(cam);
                RestoreCulled();
                if (capture == null) { complete = false; break; }

                _compositeMaterial.SetTexture("_Backdrop", capture);
                _compositeMaterial.SetVector("_BackdropRect", BackdropRect(layer, cam));
                _compositeMaterial.SetVector("_BackdropSize", new Vector4(capture.width, capture.height, 0f, 0f));
                _compositeMaterial.SetFloat("_BlendMode", (float)layer.CompositorBlendMode);
                _compositeMaterial.SetFloat("_AppearanceOpacity", layer.CompositorOpacity);
                Graphics.Blit(surface, target, _compositeMaterial);
                RenderTexture.ReleaseTemporary(capture);
            }

            // Blit leaves the last blended target active; don't let anything
            // downstream inherit it as a render target.
            RenderTexture.active = null;
            if (complete) _dirty = false;
        }

        bool EnsureCompositeMaterial()
        {
            if (_compositeMaterial != null) return true;
            var shader = Shader.Find("FigForge/Composite");
            if (shader == null) return false;
            _compositeMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            return true;
        }

        // ---- Per-layer blended targets -----------------------------------------

        void EnsureBlendedTarget(IFigForgeCompositorSource layer)
        {
            var surface = layer.GetCompositorSurface();
            if (surface == null) return;
            _blended.TryGetValue(layer, out var rt);
            if (rt != null && rt.width == surface.width && rt.height == surface.height) return;

            if (rt != null) FigForgeCompositorCache.ReturnPageRT(rt);
            rt = FigForgeCompositorCache.RentPageRT(surface.width, surface.height);
            if (rt == null) { _blended.Remove(layer); return; }
            rt.name = "FigForgeBlended_" + surface.width + "x" + surface.height;
            _blended[layer] = rt;
            SetGraphicDirty(layer); // rebind mainTexture/material to the new instance
        }

        void ReleaseBlendedTarget(IFigForgeCompositorSource layer)
        {
            if (layer == null || !_blended.TryGetValue(layer, out var rt)) return;
            if (rt != null) FigForgeCompositorCache.ReturnPageRT(rt);
            _blended.Remove(layer);
            SetGraphicDirty(layer);
        }

        void ReleaseAllBlendedTargets()
        {
            foreach (var kv in _blended)
            {
                if (kv.Value != null) FigForgeCompositorCache.ReturnPageRT(kv.Value);
                SetGraphicDirty(kv.Key);
            }
            _blended.Clear();
        }

        static void SetGraphicDirty(IFigForgeCompositorSource layer)
        {
            // Component cast first: interface-typed refs bypass Unity's overloaded
            // null check, and touching a destroyed component's members throws.
            var component = layer as Component;
            if (component == null) return;
            var graphic = component.GetComponent<Graphic>();
            if (graphic == null) return;
            graphic.SetMaterialDirty();
            graphic.SetVerticesDirty();
        }

        // ---- Backdrop rect mapping ---------------------------------------------

        // The layer's padded surface rect in capture pixels. WorldToScreenPoint keeps
        // this robust against CanvasScaler/camera setup; min/max over all four corners
        // tolerates rotated ancestors (the surface itself is axis-aligned, as before).
        Vector4 BackdropRect(IFigForgeCompositorSource layer, Camera cam)
        {
            var rt = layer.CompositorRectTransform;
            var r = rt.rect;
            float pad = layer.CompositorPad;
            Vector2 min = new Vector2(float.MaxValue, float.MaxValue);
            Vector2 max = new Vector2(float.MinValue, float.MinValue);
            for (int i = 0; i < 4; i++)
            {
                var local = new Vector3(
                    (i == 0 || i == 1) ? r.xMin - pad : r.xMax + pad,
                    (i == 0 || i == 3) ? r.yMin - pad : r.yMax + pad,
                    0f);
                Vector2 screen = RectTransformUtility.WorldToScreenPoint(cam, rt.TransformPoint(local));
                min = Vector2.Min(min, screen);
                max = Vector2.Max(max, screen);
            }
            return new Vector4(min.x, min.y, Mathf.Max(1f, max.x - min.x), Mathf.Max(1f, max.y - min.y));
        }

        // ---- Capture visibility ------------------------------------------------

        void CollectCanvasGraphics()
        {
            _canvasGraphics.Clear();
            var canvas = GetComponentInParent<Canvas>();
            var root = canvas != null ? (canvas.rootCanvas != null ? canvas.rootCanvas : canvas) : null;
            if (root == null) return;
            root.GetComponentsInChildren(false, _canvasGraphics);
        }

        // Cull every graphic at-or-above the layer in paint order: the backdrop must
        // contain only what's BELOW it (Figma semantics). Lower Tier-2 layers' quads
        // stay visible so stacked blends chain. Renderers already culled (RectMask2D
        // fast-cull) are left alone and excluded from restore.
        void CullAtOrAbove(IFigForgeCompositorSource layer)
        {
            _culled.Clear();
            var pivot = layer.transform;
            for (int i = 0; i < _canvasGraphics.Count; i++)
            {
                var g = _canvasGraphics[i];
                if (g == null) continue;
                if (CompareTransforms(g.transform, pivot) < 0) continue;
                var cr = g.canvasRenderer;
                if (cr == null || cr.cull) continue;
                cr.cull = true;
                _culled.Add(cr);
            }
        }

        void RestoreCulled()
        {
            for (int i = 0; i < _culled.Count; i++)
                _culled[i].cull = false;
            _culled.Clear();
        }

        // ---- Dirty tracking ----------------------------------------------------

        bool LayoutChanged(Camera cam)
        {
            int hash = ComputeLayoutHash(cam);
            if (hash == _layoutHash) return false;
            _layoutHash = hash;
            return true;
        }

        int ComputeLayoutHash(Camera cam)
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + cam.pixelWidth;
                hash = hash * 31 + cam.pixelHeight;
                for (int i = 0; i < _orderedLayers.Count; i++)
                {
                    var layer = _orderedLayers[i];
                    var rect = BackdropRect(layer, cam);
                    hash = hash * 31 + Quantized(rect.x);
                    hash = hash * 31 + Quantized(rect.y);
                    hash = hash * 31 + Quantized(rect.z);
                    hash = hash * 31 + Quantized(rect.w);
                    hash = hash * 31 + (int)layer.CompositorBlendMode;
                    hash = hash * 31 + Quantized(layer.CompositorOpacity);
                    // Pure z-reorders move a layer in paint order without moving it on
                    // screen — the backdrop set changes while the rect doesn't.
                    for (var t = layer.transform; t != null; t = t.parent)
                        hash = hash * 31 + t.GetSiblingIndex();
                }
                return hash;
            }
        }

        static int Quantized(float value)
        {
            return Mathf.RoundToInt(value * 100f);
        }

        // ---- Layer bookkeeping -------------------------------------------------

        void SortOrderedLayers()
        {
            _orderedLayers.Clear();
            for (int i = 0; i < _advancedLayers.Count; i++)
            {
                var layer = _advancedLayers[i];
                if (ShouldRenderLayer(layer))
                    _orderedLayers.Add(layer);
            }
            _orderedLayers.Sort(CompareHierarchy);
        }

        int ActiveAdvancedCount()
        {
            PruneAdvancedLayers();
            return _advancedLayers.Count;
        }

        void PruneAdvancedLayers()
        {
            bool removed = false;
            for (int i = _advancedLayers.Count - 1; i >= 0; i--)
            {
                var layer = _advancedLayers[i];
                if (layer == null || !layer.isActiveAndEnabled || !layer.RequiresPageCompositor || !layer.transform.IsChildOf(transform))
                {
                    _advancedLayers.RemoveAt(i);
                    removed = true;
                }
            }
            if (removed) ReleaseOrphanedTargets();
        }

        // Targets whose layer was pruned (or destroyed — the dictionary key can be a
        // dead component reference) leak pooled RTs unless swept here.
        void ReleaseOrphanedTargets()
        {
            _orphanScratch.Clear();
            foreach (var kv in _blended)
            {
                var layer = kv.Key;
                if (layer == null || (layer as Object) == null || !_advancedLayers.Contains(layer))
                    _orphanScratch.Add(layer);
            }
            for (int i = 0; i < _orphanScratch.Count; i++)
            {
                if (_blended.TryGetValue(_orphanScratch[i], out var rt) && rt != null)
                    FigForgeCompositorCache.ReturnPageRT(rt);
                _blended.Remove(_orphanScratch[i]);
            }
            _orphanScratch.Clear();
        }

        static int CompareHierarchy(IFigForgeCompositorSource a, IFigForgeCompositorSource b)
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

        static void DestroyRuntimeMaterial(Material material)
        {
            if (material == null) return;
            if (Application.isPlaying) Destroy(material);
            else DestroyImmediate(material);
        }

#if UNITY_EDITOR
        // Verifies the capture pass in isolation: saves the page as the compositor
        // will see it as a backdrop — Tier-2 (destination-reading) sources hidden,
        // everything else (foreign uGUI included) rendered through the page camera.
        // Selects sources by blend tier directly, NOT RequiresPageCompositor, so the
        // pass is testable regardless of registration state. Alpha is forced opaque
        // in the PNG: uGUI leaves non-1 destination alpha along AA edges, which the
        // compositor ignores but PNG viewers show as a pale fringe.
        [ContextMenu("Save Debug Page Capture (Tier-2 Hidden)")]
        void SaveDebugPageCapture()
        {
            var canvas = GetComponentInParent<Canvas>();
            var cam = FigForgePageCapture.ResolveCamera(canvas);
            if (cam == null)
            {
                Debug.LogWarning("[FigForge] Page capture needs a Screen Space - Camera canvas with a camera assigned.", this);
                return;
            }

            var sources = new List<IFigForgeCompositorSource>();
            GetComponentsInChildren(false, sources);
            var culled = new List<CanvasRenderer>();
            for (int i = 0; i < sources.Count; i++)
            {
                var source = sources[i];
                if (source == null || FigForgeLayeredRect.BlendTier(source.CompositorBlendMode) != 2) continue;
                var cr = source.transform.GetComponent<CanvasRenderer>();
                if (cr == null || cr.cull) continue;
                cr.cull = true;
                culled.Add(cr);
            }

            var rt = FigForgePageCapture.CaptureTemporary(cam);
            for (int i = 0; i < culled.Count; i++) culled[i].cull = false;
            if (rt == null)
            {
                Debug.LogWarning("[FigForge] Page capture failed — SRP without render-request support?", this);
                return;
            }

            var prev = RenderTexture.active;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply(false);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);

            var px = tex.GetPixels32();
            for (int i = 0; i < px.Length; i++) px[i].a = 255;
            tex.SetPixels32(px);

            string path = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(Application.dataPath, "..", "FigForgePageCapture.png"));
            System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
            DestroyImmediate(tex);
            Debug.Log("[FigForge] Page capture saved (" + culled.Count + " Tier-2 source(s) hidden): " + path, this);
        }
#endif
    }
}
