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
// beneath a blend layer are auto-detected at runtime: a state hash over the
// canvas's graphics (identity, paint order, position, size, color, texture)
// plus TMP's TEXT_CHANGED event for text-content edits. MarkDirty() remains
// for exotic cases — custom shaders animating without color/transform changes,
// or when auto-detect is disabled. (In the editor, hierarchy edits mark the
// compositor dirty automatically.)
// =============================================================================

using System.Collections.Generic;
using TMPro;
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
        readonly List<IFigForgeCompositorSource> _retargetScratch = new List<IFigForgeCompositorSource>();
        readonly List<Graphic> _canvasGraphics = new List<Graphic>();
        readonly List<CanvasRenderer> _culled = new List<CanvasRenderer>();

        // Auto-detect runtime changes to foreign graphics (plain Images, TMP text)
        // under blend layers: a linear scan of the canvas's graphics each canvas
        // update while blend layers are active. Disable and call MarkDirty()
        // manually if the page has a very large graphic count.
        [SerializeField] bool autoDetectForeignChanges = true;

        public bool AutoDetectForeignChanges
        {
            get => autoDetectForeignChanges;
            set => autoDetectForeignChanges = value;
        }

#if UNITY_EDITOR
        // TEMPORARY diagnostic hooks (FigForgeBlurDebugDump): observe captures/blits
        // from INSIDE a live rebuild — the only place organic-dispatch sequencing
        // bugs are visible. No-ops unless armed by the editor tooling.
        internal static System.Action<string, RenderTexture> DebugRebuildSink;
        internal static System.Action<string> DebugRebuildLog;
        string _debugDirtySource;
#endif

        Material _compositeMaterial;
        Material _blurMaterial;
        Canvas _cachedCanvas;
        bool _dirty = true;
        bool _inRebuild;
        bool _warnedCaptureFailed;
        int _layoutHash;
        int _foreignHash;
        bool _foreignHashValid;

        public bool IsActive => isActiveAndEnabled && ActiveAdvancedCount() > 0;

        // Whether this page can be composited at all: needs a Screen Space - Camera
        // canvas to capture through. Sources check this BEFORE registering and stay
        // on their per-graphic fallback path (BIRP GrabPass / URP alpha) otherwise.
        public bool CanComposite => FigForgePageCapture.CanCapture(ParentCanvas());

        // GetComponentInParent walks the hierarchy and this runs (twice) per frame
        // from willRenderCanvases — cache it; reparenting/hierarchy edits invalidate.
        Canvas ParentCanvas()
        {
            if (_cachedCanvas == null) _cachedCanvas = GetComponentInParent<Canvas>();
            return _cachedCanvas;
        }

        // Set by the editor importer for the duration of a page build: elements
        // enable/configure BEFORE BuildPage adds FigForgeScreen + the page
        // compositor to the root, so the canvas fallback in the sources'
        // FindOrCreatePageCompositor would mint a stray canvas-level compositor
        // on every fresh import. While true, sources may still BIND to an
        // existing ancestor compositor (reuse builds) — only creation is
        // suppressed; the importer rebinds all sources after the root gets its
        // compositor (HierarchyBuilder.ConfigurePageCompositor).
        public static bool SuppressAutoCreate;

        // Registered advanced-blend source count — lets the importer tell an
        // inert legacy stray (zero sources after the post-build rebind) from a
        // compositor that hand-made content genuinely registered with.
        public int RegisteredSourceCount => _advancedLayers.Count;

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
#if UNITY_EDITOR
            if (DebugTraceMarkDirty && !_dirty)
                Debug.Log("[FigForge] MarkDirty (frame " + Time.frameCount + ", inRebuild=" + _inRebuild + ")\n"
                          + StackTraceUtility.ExtractStackTrace());
#endif
            _dirty = true;
        }

#if UNITY_EDITOR
        internal static bool DebugTraceMarkDirty;
#endif

        void OnEnable()
        {
            _cachedCanvas = null; // may have been reparented while disabled
            Canvas.willRenderCanvases -= HandleWillRenderCanvases;
            Canvas.willRenderCanvases += HandleWillRenderCanvases;
            // Text-content edits change neither rect, position, color, nor texture
            // instance — the foreign-state hash can't see them, TMP's event can.
            TMPro_EventManager.TEXT_CHANGED_EVENT.Remove(HandleTextChanged);
            TMPro_EventManager.TEXT_CHANGED_EVENT.Add(HandleTextChanged);
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
            TMPro_EventManager.TEXT_CHANGED_EVENT.Remove(HandleTextChanged);
#if UNITY_EDITOR
            UnityEditor.EditorApplication.hierarchyChanged -= HandleHierarchyChanged;
#endif
            ReleaseAllBlendedTargets();
        }

        void OnDestroy()
        {
            Canvas.willRenderCanvases -= HandleWillRenderCanvases;
            TMPro_EventManager.TEXT_CHANGED_EVENT.Remove(HandleTextChanged);
#if UNITY_EDITOR
            UnityEditor.EditorApplication.hierarchyChanged -= HandleHierarchyChanged;
#endif
            ReleaseAllBlendedTargets();
            DestroyRuntimeMaterial(_compositeMaterial);
            DestroyRuntimeMaterial(_blurMaterial);
        }

#if UNITY_EDITOR
        void HandleHierarchyChanged()
        {
            _cachedCanvas = null; // an authoring edit may have inserted/removed a canvas
            MarkDirty();
        }
#endif

        void OnTransformChildrenChanged()
        {
            MarkDirty();
        }

        void OnTransformParentChanged()
        {
            _cachedCanvas = null;
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
            // Inline IsActive: the property's ActiveAdvancedCount would prune again.
            if (!isActiveAndEnabled || _advancedLayers.Count == 0) return;

            var cam = FigForgePageCapture.ResolveCamera(ParentCanvas());
            if (cam == null) return;

            SortOrderedLayers();
#if UNITY_EDITOR
            if (DebugRebuildLog != null) _debugDirtySource = _dirty ? "explicit" : "";
#endif
            if (LayoutChanged(cam))
            {
#if UNITY_EDITOR
                if (DebugRebuildLog != null) _debugDirtySource += "+layout";
#endif
                MarkDirty();
            }
            if (autoDetectForeignChanges) DetectForeignChanges();
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
            _retargetScratch.Clear();
            for (int i = 0; i < _orderedLayers.Count; i++)
                if (EnsureBlendedTarget(_orderedLayers[i]))
                    _retargetScratch.Add(_orderedLayers[i]);

            // Make quad geometry/material bindings current regardless of where this
            // handler sits in the willRenderCanvases subscription order (re-entrancy
            // guarded by _inRebuild).
            Canvas.ForceUpdateCanvases();
            // Twice: source handlers run INSIDE the dispatch above — after its
            // registry pass — and may queue fresh present-quad rebuilds (first-time
            // texture binds). The capture renders below process pending canvas
            // updates while renderers are temporarily culled, and uGUI CONSUMES a
            // queued rebuild it skips for a culled renderer — the quad's geometry/
            // material would never upload (permanently invisible). Flush until clean.
            Canvas.ForceUpdateCanvases();

            CollectCanvasGraphics();
            bool complete = true;
            for (int i = 0; i < _orderedLayers.Count; i++)
            {
                var layer = _orderedLayers[i];
                var surface = layer.GetCompositorSurface();
                var target = GetBlendedSurface(layer);
                if (surface == null || target == null)
                {
                    // Surface not baked yet (first-frame source ordering) — leave the
                    // rebuild incomplete so _dirty survives and the next frame retries,
                    // instead of presenting this layer's stale pixels forever.
                    complete = false;
                    continue;
                }

                CullAtOrAbove(layer);
#if UNITY_EDITOR
                DebugRebuildLog?.Invoke("layer=" + ((Component)layer).name
                    + " dirty=" + _debugDirtySource
                    + " culled=" + _culled.Count
                    + " surface=" + surface.width + "x" + surface.height
                    + " target=" + target.GetInstanceID() + ":" + target.width + "x" + target.height);
#endif
                var capture = FigForgePageCapture.CaptureTemporary(cam);
                RestoreCulled();
                if (capture == null)
                {
                    // Without a capture the layer's blended target is left unwritten —
                    // its quad would present stale/garbage pixels. Say so, loudly once.
                    if (!_warnedCaptureFailed)
                    {
                        _warnedCaptureFailed = true;
                        Debug.LogWarning("[FigForge] Page capture failed — Tier-2 blends on this page are stale. " +
                                         "Camera: " + (cam != null ? cam.name : "null"), this);
                    }
                    complete = false;
                    break;
                }
                _warnedCaptureFailed = false;

                var backdropRect = BackdropRect(layer, cam);
                _compositeMaterial.SetTexture("_Backdrop", capture);
                _compositeMaterial.SetVector("_BackdropRect", backdropRect);
                _compositeMaterial.SetVector("_BackdropSize", new Vector4(capture.width, capture.height, 0f, 0f));
                _compositeMaterial.SetFloat("_BlendMode", (float)layer.CompositorBlendMode);
                _compositeMaterial.SetFloat("_AppearanceOpacity", layer.CompositorOpacity);
                var blurTex = PrepareBackdropBlur(layer, capture, target, backdropRect);
                Graphics.Blit(surface, target, _compositeMaterial, 0);
                if (blurTex != null) RenderTexture.ReleaseTemporary(blurTex);
#if UNITY_EDITOR
                if (DebugRebuildSink != null)
                {
                    var n = ((Component)layer).name;
                    DebugRebuildSink(n + "_capture", capture);
                    DebugRebuildSink(n + "_surface", surface);
                    DebugRebuildSink(n + "_target", target);
                }
#endif
                RenderTexture.ReleaseTemporary(capture);
            }

            // Blit leaves the last blended target active; don't let anything
            // downstream inherit it as a render target.
            RenderTexture.active = null;

            // Rebind re-assert for layers whose target INSTANCE changed this rebuild:
            // a quad rebuild queued while its renderer is culled is consumed without
            // uploading (uGUI skips culled renderers), which would leave the quad
            // presenting the RETURNED pooled RT until something else dirtied it.
            // Nothing is culled here, so one more flush makes the rebind stick.
            if (_retargetScratch.Count > 0)
            {
                for (int i = 0; i < _retargetScratch.Count; i++)
                    SetGraphicDirty(_retargetScratch[i]);
                _retargetScratch.Clear();
                Canvas.ForceUpdateCanvases();
            }

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

        // ---- Backdrop blur (glassmorphism) --------------------------------------

        // Figma background blur: blur the captured backdrop behind the layer's
        // SHAPE, then let the blend pass composite the fill over it (out-alpha =
        // shape coverage, so the blur replaces the sharp backdrop inside the shape
        // while shadows in the pad ring stay over the sharp page). Returns the
        // temporary blurred crop (caller releases) or null when the layer has no
        // background blur. Configures all pass-0 blur uniforms either way.
        RenderTexture PrepareBackdropBlur(IFigForgeCompositorSource layer, RenderTexture capture,
                                          RenderTexture target, Vector4 backdropRect)
        {
            float blurCanvasPx = layer.CompositorBackdropBlur;
            if (blurCanvasPx <= 0.001f || !EnsureBlurMaterial())
            {
                _compositeMaterial.SetFloat("_HasBackdropBlur", 0f);
                return null;
            }

            var rect = layer.CompositorRectTransform.rect;
            float pad = layer.CompositorPad;
            float surfacePerCanvas = target.width / Mathf.Max(1f, rect.width + 2f * pad);
            float surfacePerScreen = target.width / Mathf.Max(1f, backdropRect.z);
            float blurSurfacePx = blurCanvasPx * surfacePerCanvas;

            // Crop a region EXTENDED by the blur kernel's reach: pixels near the
            // shape edge need backdrop context from beyond the layer rect, else the
            // clamped sampling smears the crop's edge pixels across the glass.
            int extend = Mathf.CeilToInt(1.75f * blurSurfacePx + 1f);
            int limit = SystemInfo.maxTextureSize > 0 ? SystemInfo.maxTextureSize : 8192;
            int bw = Mathf.Clamp(target.width + 2 * extend, 1, limit);
            int bh = Mathf.Clamp(target.height + 2 * extend, 1, limit);
            float extendScreen = extend / Mathf.Max(0.0001f, surfacePerScreen);

            var blurTex = RenderTexture.GetTemporary(bw, bh, 0, RenderTextureFormat.ARGB32);
            blurTex.filterMode = FilterMode.Bilinear;
            blurTex.wrapMode = TextureWrapMode.Clamp;
            _compositeMaterial.SetVector("_CropRect", new Vector4(
                backdropRect.x - extendScreen, backdropRect.y - extendScreen,
                backdropRect.z + 2f * extendScreen, backdropRect.w + 2f * extendScreen));
            Graphics.Blit(capture, blurTex, _compositeMaterial, 1);
            ApplySeparableBlur(blurTex, blurSurfacePx);

            // Shape bounds in surface pixels for the analytic rounded-rect coverage.
            float padPx = pad * surfacePerCanvas;
            var shapeRect = new Vector4(padPx, padPx, rect.width * surfacePerCanvas, rect.height * surfacePerCanvas);
            var radii = layer.CompositorShapeCorners * surfacePerCanvas;
            float maxRad = 0.5f * Mathf.Min(shapeRect.z, shapeRect.w);
            radii = new Vector4(Mathf.Min(radii.x, maxRad), Mathf.Min(radii.y, maxRad),
                                Mathf.Min(radii.z, maxRad), Mathf.Min(radii.w, maxRad));

            _compositeMaterial.SetFloat("_HasBackdropBlur", 1f);
            _compositeMaterial.SetTexture("_BackdropBlurTex", blurTex);
            _compositeMaterial.SetVector("_BlurUvParams", new Vector4(extend, extend, bw, bh));
            _compositeMaterial.SetVector("_ShapeRectPx", shapeRect);
            _compositeMaterial.SetVector("_ShapeRadiusPx", radii);
            _compositeMaterial.SetVector("_TargetSize", new Vector4(target.width, target.height, 0f, 0f));
            return blurTex;
        }

        void ApplySeparableBlur(RenderTexture targetRt, float radiusPx)
        {
            // Shared downsample-blur-upsample chain: bounds the 9-tap kernel's tap
            // spacing so large radii don't ring (see FigForgeCompositorCache).
            FigForgeCompositorCache.ApplyLayerBlur(targetRt, _blurMaterial, new Vector4(1f, 0f, radiusPx, radiusPx));
        }

        bool EnsureBlurMaterial()
        {
            if (_blurMaterial != null) return true;
            var shader = Shader.Find("FigForge/LayerBlur");
            if (shader == null) return false;
            _blurMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            return true;
        }

        // ---- Per-layer blended targets -----------------------------------------

        // Returns true when the layer's target INSTANCE changed (rented anew), so the
        // caller can re-assert the present quad's binding after the capture loop.
        bool EnsureBlendedTarget(IFigForgeCompositorSource layer)
        {
            var surface = layer.GetCompositorSurface();
            if (surface == null) return false;
            _blended.TryGetValue(layer, out var rt);
            if (rt != null && rt.width == surface.width && rt.height == surface.height) return false;

            if (rt != null) FigForgeCompositorCache.ReturnPageRT(rt);
            rt = FigForgeCompositorCache.RentPageRT(surface.width, surface.height);
            if (rt == null) { _blended.Remove(layer); return false; }
            rt.name = "FigForgeBlended_" + surface.width + "x" + surface.height;
            // Pooled RTs arrive holding whatever their previous user rendered (often
            // ANOTHER layer's old blend). The blit below normally overwrites every
            // pixel, but if it never lands (capture failure, lost rebind) the quad
            // must present nothing rather than someone else's stale colors.
            var prevActive = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(false, true, Color.clear);
            RenderTexture.active = prevActive;
            _blended[layer] = rt;
            SetGraphicDirty(layer); // rebind mainTexture/material to the new instance
            return true;
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
            var canvas = ParentCanvas();
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
                    hash = hash * 31 + Quantized(layer.CompositorBackdropBlur);
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

        // ---- Foreign-change detection ------------------------------------------

        // Runtime auto-dirty for FOREIGN graphics (plain Images, TMP text, ...)
        // under blend layers: hash identity, paint order, position, size, color,
        // and texture of every graphic on the canvas; any drift → MarkDirty().
        void DetectForeignChanges()
        {
            // Reuses the cached list. Rebuild recollects it afterwards, which is
            // fine — this scan must see the CURRENT canvas population either way.
            CollectCanvasGraphics();
            int hash = ComputeForeignHash();
            if (!_foreignHashValid)
            {
                // Lazy seed: the compositor already starts dirty, so the first
                // computation must not trigger a spurious extra MarkDirty cycle.
                _foreignHashValid = true;
                _foreignHash = hash;
                return;
            }
            if (hash == _foreignHash) return;
            _foreignHash = hash;
#if UNITY_EDITOR
            if (DebugRebuildLog != null) _debugDirtySource += "+foreign";
#endif
            MarkDirty();
        }

        int ComputeForeignHash()
        {
            unchecked
            {
                int hash = 17;
                for (int i = 0; i < _canvasGraphics.Count; i++)
                {
                    var graphic = _canvasGraphics[i];
                    if (graphic == null) continue;
                    var t = graphic.transform;
                    hash = hash * 31 + graphic.GetInstanceID();
                    // Sibling index + parent identity catch reorders/reparents.
                    hash = hash * 31 + t.GetSiblingIndex();
                    hash = hash * 31 + (t.parent != null ? t.parent.GetInstanceID() : 0);
                    var pos = t.localPosition;
                    hash = hash * 31 + Quantized(pos.x);
                    hash = hash * 31 + Quantized(pos.y);
                    var rect = ((RectTransform)graphic.rectTransform).rect;
                    hash = hash * 31 + Quantized(rect.width);
                    hash = hash * 31 + Quantized(rect.height);
                    Color32 c = graphic.color;
                    hash = hash * 31 + (c.r | c.g << 8 | c.b << 16 | c.a << 24);
                    // Texture identity catches sprite swaps. Our own blended-RT
                    // rebinds keep stable instances except on resize (which the
                    // layout hash already dirties), so no feedback loop.
                    var tex = graphic.mainTexture;
                    hash = hash * 31 + (tex != null ? tex.GetInstanceID() : 0);
                }
                return hash;
            }
        }

        // Text-content edits keep rect, position, color, and texture instance all
        // unchanged — invisible to the foreign-state hash — so TMP's regen event
        // dirties us directly.
        void HandleTextChanged(Object obj)
        {
            // TMP regens fired by our own Canvas.ForceUpdateCanvases during Rebuild
            // land in the same capture — re-dirtying here would rebuild every frame.
            if (_inRebuild) return;
            if (!autoDetectForeignChanges) return;
            // Component cast first: Object-typed refs of destroyed components
            // bypass the overloaded null check (same caveat as SetGraphicDirty).
            var component = obj as Component;
            if (component == null) return;
            var canvas = ParentCanvas();
            var root = canvas != null ? (canvas.rootCanvas != null ? canvas.rootCanvas : canvas) : null;
            if (root == null) return;
            if (!component.transform.IsChildOf(root.transform)) return;
            MarkDirty();
        }

        // ---- Layer bookkeeping -------------------------------------------------

        void SortOrderedLayers()
        {
            // PruneAdvancedLayers just ran (sole caller is the willRenderCanvases
            // handler): everything left is live, enabled, and under this compositor,
            // so skip the per-element ShouldRenderLayer re-check — it re-prunes and
            // Contains-scans per layer, O(n^2) every frame for the same answer.
            _orderedLayers.Clear();
            for (int i = 0; i < _advancedLayers.Count; i++)
                _orderedLayers.Add(_advancedLayers[i]);
            _orderedLayers.Sort(HierarchyComparison);
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

        // Cached once: a method-group Sort(CompareHierarchy) argument allocates a
        // fresh Comparison<T> delegate on every per-frame sort.
        static readonly System.Comparison<IFigForgeCompositorSource> HierarchyComparison = CompareHierarchy;

        static int CompareHierarchy(IFigForgeCompositorSource a, IFigForgeCompositorSource b)
        {
            if (a == b) return 0;
            if (a == null) return -1;
            if (b == null) return 1;
            return CompareTransforms(a.transform, b.transform);
        }

        // Paint-order compare, allocation-free (runs per graphic per capture cull and
        // inside the per-frame sort): walk the deeper side up to a common depth, then
        // both up to the common parent, and compare sibling indices there. An ancestor
        // paints before its descendants, matching the sibling-index-path comparison
        // this replaces. (Everything compared here shares the page's root canvas.)
        static int CompareTransforms(Transform a, Transform b)
        {
            if (a == b) return 0;
            int depthA = Depth(a), depthB = Depth(b);
            for (int d = depthA; d > depthB; d--) a = a.parent;
            for (int d = depthB; d > depthA; d--) b = b.parent;
            if (a == b) return depthA - depthB; // one is the other's ancestor
            while (a.parent != b.parent)
            {
                a = a.parent;
                b = b.parent;
            }
            return a.GetSiblingIndex() - b.GetSiblingIndex();
        }

        static int Depth(Transform t)
        {
            int depth = 0;
            for (; t != null; t = t.parent) depth++;
            return depth;
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
