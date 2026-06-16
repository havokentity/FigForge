// =============================================================================
// FigForge — grouped rounded-rect renderer. Stores Figma-style paint lists
// (fills, strokes, effects) on one component and renders common recipes through
// one shader pass. Current shader supports up to four stroke paint layers.
// =============================================================================

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace FigForge
{
    public enum FigForgeBlendMode
    {
        PassThrough,
        Normal,
        Darken,
        Multiply,
        PlusDarker,
        ColorBurn,
        Lighten,
        Screen,
        PlusLighter,
        ColorDodge,
        Overlay,
        SoftLight,
        HardLight,
        Difference,
        Exclusion,
        Hue,
        Saturation,
        Color,
        Luminosity,
    }

    public enum FigForgeEffectKind { DropShadow, InnerShadow, LayerBlur, BackgroundBlur }
    public enum FigForgeLayerBlurMode { Uniform, Progressive }
    public enum FigForgeStrokeStyle { Solid, Dashed }
    public enum FigForgeCompositorMode { Auto, Direct, CachedSource }

    [System.Serializable]
    public struct FigForgeStrokeLayer
    {
        public bool enabled;
        public FigForgeStrokeStyle style;
        public FigForgeFill paint;
        public float weight;
        public FigForgeStrokeAlign align;
        public float dash;
        public float gap;

        public float Outset => !enabled ? 0f : Mathf.Max(0f, weight) * AlignFactor(align);

        public static FigForgeStrokeLayer Create(FigForgeFill paint, float weight, FigForgeStrokeAlign align,
                                                 bool dashed = false, float dash = 0f, float gap = 0f)
        {
            paint.Normalize();
            return new FigForgeStrokeLayer
            {
                enabled = weight > 0.001f && VisibleFill(paint),
                style = dashed ? FigForgeStrokeStyle.Dashed : FigForgeStrokeStyle.Solid,
                paint = paint,
                weight = Mathf.Max(0f, weight),
                align = align,
                dash = Mathf.Max(0f, dash),
                gap = Mathf.Max(0f, gap),
            };
        }

        public void Normalize()
        {
            paint.Normalize();
            weight = Mathf.Max(0f, weight);
            dash = Mathf.Max(0f, dash);
            gap = Mathf.Max(0f, gap);
            enabled = enabled && weight > 0.001f && VisibleFill(paint);
            if (style != FigForgeStrokeStyle.Dashed) style = FigForgeStrokeStyle.Solid;
        }

        static bool VisibleFill(FigForgeFill fill)
        {
            if (fill.disabled) return false;
            if (fill.kind == FigForgeFillKind.Solid) return fill.color.a > 0.001f;
            if (fill.gradient == null) return fill.color.a > 0.001f;
            var alphas = fill.gradient.alphaKeys;
            if (alphas == null || alphas.Length == 0) return fill.color.a > 0.001f;
            for (int i = 0; i < alphas.Length; i++)
                if (alphas[i].alpha > 0.001f) return true;
            return false;
        }

        static float AlignFactor(FigForgeStrokeAlign align)
        {
            switch (align)
            {
                case FigForgeStrokeAlign.Outside: return 1f;
                case FigForgeStrokeAlign.Center: return 0.5f;
                default: return 0f;
            }
        }
    }

    [System.Serializable]
    public struct FigForgeEffectLayer
    {
        public bool enabled;
        public FigForgeEffectKind kind;
        public Color color;
        public Vector2 offset;
        public float blur;
        public float spread;
        public FigForgeLayerBlurMode blurMode;
        public float endBlur;
        // Drop shadows only — Figma's showShadowBehindNode. False (the Figma default)
        // erases the shadow under the shape's geometry, so it never shows through
        // translucent fills.
        public bool showBehindShape;

        public static FigForgeEffectLayer DropShadow(Color color, Vector2 offset, float blur, float spread)
        {
            return new FigForgeEffectLayer
            {
                enabled = color.a > 0.001f,
                kind = FigForgeEffectKind.DropShadow,
                color = color,
                offset = offset,
                blur = Mathf.Max(0f, blur),
                spread = spread,
                blurMode = FigForgeLayerBlurMode.Uniform,
                endBlur = Mathf.Max(0f, blur),
            };
        }

        public static FigForgeEffectLayer InnerShadow(Color color, Vector2 offset, float blur, float spread)
        {
            return new FigForgeEffectLayer
            {
                enabled = color.a > 0.001f,
                kind = FigForgeEffectKind.InnerShadow,
                color = color,
                offset = offset,
                blur = Mathf.Max(0f, blur),
                spread = spread,
                blurMode = FigForgeLayerBlurMode.Uniform,
                endBlur = Mathf.Max(0f, blur),
            };
        }

        public static FigForgeEffectLayer LayerBlur(float blur)
        {
            return new FigForgeEffectLayer
            {
                enabled = blur > 0.001f,
                kind = FigForgeEffectKind.LayerBlur,
                color = new Color(0, 0, 0, 0),
                offset = Vector2.zero,
                blur = Mathf.Max(0f, blur),
                spread = 0f,
                blurMode = FigForgeLayerBlurMode.Uniform,
                endBlur = Mathf.Max(0f, blur),
            };
        }

        // Figma background blur (glassmorphism): blurs the BACKDROP behind the
        // shape, not the layer's own paint. Composited by the page compositor.
        public static FigForgeEffectLayer BackgroundBlur(float blur)
        {
            return new FigForgeEffectLayer
            {
                enabled = blur > 0.001f,
                kind = FigForgeEffectKind.BackgroundBlur,
                color = new Color(0, 0, 0, 0),
                offset = Vector2.zero,
                blur = Mathf.Max(0f, blur),
                spread = 0f,
                blurMode = FigForgeLayerBlurMode.Uniform,
                endBlur = Mathf.Max(0f, blur),
            };
        }

        public void Normalize()
        {
            blur = Mathf.Max(0f, blur);
            endBlur = Mathf.Max(0f, endBlur);
            if (kind == FigForgeEffectKind.LayerBlur)
            {
                if (blurMode != FigForgeLayerBlurMode.Progressive) blurMode = FigForgeLayerBlurMode.Uniform;
                enabled = enabled && Mathf.Max(blur, endBlur) > 0.001f;
                return;
            }
            if (kind == FigForgeEffectKind.BackgroundBlur)
            {
                blurMode = FigForgeLayerBlurMode.Uniform;
                enabled = enabled && blur > 0.001f;
                return;
            }
            if (kind != FigForgeEffectKind.InnerShadow) kind = FigForgeEffectKind.DropShadow;
            if (color.a <= 0.001f) enabled = false;
        }
    }

    [RequireComponent(typeof(CanvasRenderer))]
    [AddComponentMenu("FigForge/Layered Rect")]
    public class FigForgeLayeredRect : MaskableGraphic, IFigForgeCompositorSource
    {
        const float MaxCachedSurfaceScale = 16f;
        const int SurfaceShaderRevision = 3;

        [SerializeField] List<FigForgeFill> fills = new List<FigForgeFill>();
        [SerializeField] List<FigForgeStrokeLayer> strokes = new List<FigForgeStrokeLayer>();
        [SerializeField] List<FigForgeEffectLayer> effects = new List<FigForgeEffectLayer>();
        [SerializeField, Range(0f, 1f)] float appearanceOpacity = 1f;
        [SerializeField] FigForgeBlendMode blendMode = FigForgeBlendMode.Normal;
        [SerializeField] FigForgeCompositorMode compositorMode = FigForgeCompositorMode.Auto;
        [SerializeField] string recipe = "F0_S0_E0";
        [SerializeField] Vector4 corners = Vector4.zero;

        Material _directMaterial;
        Material _cachedMaterial;
        Material _bakeMaterial;
        Material _blurMaterial;
        Texture2D _paintTexture;
        RenderTexture _cachedSurface;
        string _cachedSurfaceKey;
        // uint twin of _cachedSurfaceKey — the per-canvas-update validity check compares
        // this instead of building the key string, so the steady state allocates nothing.
        uint _cachedSurfaceHash;
        int _cachedSurfaceWidth;
        int _cachedSurfaceHeight;
        readonly Vector3[] _worldCorners = new Vector3[4];
        // Cached surface scale keyed by the projection camera — recomputed only when the
        // camera actually moves, so an idle SceneView doesn't thrash the quantized scale
        // across a 0.25 boundary and re-bake the cached surface every canvas update.
        float _cachedScaleFactor = -1f;
        float _cachedRawScaleFactor = -1f;
        Matrix4x4 _scaleCamKey;
        // RefreshRecipe runs on the per-canvas-update validity path — cache the visible
        // counts so the (serialized, debug-facing) string is only rebuilt on change.
        int _recipeFills = -1, _recipeStrokes = -1, _recipeEffects = -1;
        // NormalizeLists also runs on that per-canvas-update path (via CurrentSurfaceDescriptor),
        // and it loops + writes back every fill/stroke/effect. Normalization is idempotent and
        // only matters when the data changed, so keep the lists normalized AT REST: do the work
        // once, then no-op until a mutation point (edit/Configure/Set) clears this. Non-serialized,
        // so it defaults false on load/instantiate → the first access normalizes.
        bool _listsNormalized;
        FigForgePageCompositor _pageCompositor;

        public IReadOnlyList<FigForgeFill> Fills => fills;
        public IReadOnlyList<FigForgeStrokeLayer> Strokes => strokes;
        public IReadOnlyList<FigForgeEffectLayer> Effects => effects;
        public string Recipe => recipe;
        public FigForgeBlendMode CompositorBlendMode => blendMode;
        public float CompositorOpacity => appearanceOpacity;
        public float CompositorPad => MeshPad();
        public float CompositorBackdropBlur => BackdropBlurRadius();
        public Vector4 CompositorShapeCorners => corners;
        public RectTransform CompositorRectTransform => rectTransform;
        // Tier-2 (destination-reading) blends AND background blur route through the
        // page compositor on BOTH pipelines: it captures the real page (foreign uGUI
        // included) and hands this graphic a pre-blended texture to present at its
        // own hierarchy position. Registration is additionally gated on the canvas
        // being capturable (Screen Space - Camera); otherwise the per-graphic
        // fallback applies — GrabPass under Built-in, plain alpha under SRP (and
        // background blur degrades to no blur).
        public const bool PageCompositorEnabled = true;
        public bool RequiresPageCompositor =>
            PageCompositorEnabled && (BlendTier(blendMode) == 2 || BackdropBlurRadius() > 0.001f);

        // First enabled background-blur effect's radius (canvas px, 0 = none).
        float BackdropBlurRadius()
        {
            for (int i = 0; i < effects.Count; i++)
            {
                var e = effects[i];
                if (e.enabled && e.kind == FigForgeEffectKind.BackgroundBlur && e.blur > 0.001f)
                    return e.blur;
            }
            return 0f;
        }
        public float AppearanceOpacity
        {
            get => appearanceOpacity;
            set
            {
                appearanceOpacity = Mathf.Clamp01(value);
                MarkPageCompositorDirty();
                SetMaterialDirty();
            }
        }
        public FigForgeBlendMode BlendMode
        {
            get => blendMode;
            set
            {
                if (blendMode == value) return;
                blendMode = value;
                UpdatePageCompositorRegistration();
                MarkPageCompositorDirty();
                SetVerticesDirty();
                SetMaterialDirty();
            }
        }
        public FigForgeCompositorMode CompositorMode
        {
            get => compositorMode;
            set
            {
                if (compositorMode == value) return;
                compositorMode = value;
                if (compositorMode == FigForgeCompositorMode.Direct)
                    ReleaseCachedSurface();
                UpdatePageCompositorRegistration();
                MarkPageCompositorDirty();
                SetVerticesDirty();
                SetMaterialDirty();
            }
        }
        public string DebugSummary
        {
            get
            {
                NormalizeLists();
                RefreshRecipe();
                return $"{recipe} drop={VisibleDropShadowCount()} inner={VisibleInnerShadowCount()} blur={VisibleLayerBlurCount()}";
            }
        }

        public void ConfigureLayers(List<FigForgeFill> fills, List<FigForgeStrokeLayer> strokes,
                                    List<FigForgeEffectLayer> effects, Vector4 cornerRadii)
        {
            ReleaseCachedSurface();
            MarkPageCompositorDirty();
            ClampGraphicState();
            this.fills = fills != null ? new List<FigForgeFill>(fills) : new List<FigForgeFill>();
            this.strokes = strokes != null ? new List<FigForgeStrokeLayer>(strokes) : new List<FigForgeStrokeLayer>();
            this.effects = effects != null ? new List<FigForgeEffectLayer>(effects) : new List<FigForgeEffectLayer>();
            corners = cornerRadii;
            _listsNormalized = false;
            NormalizeLists();
            RefreshRecipe();
            MarkPageCompositorDirty();
            SetVerticesDirty();
            SetMaterialDirty();
        }

        public void ConfigureAppearance(float opacity, FigForgeBlendMode blend)
        {
            appearanceOpacity = Mathf.Clamp01(opacity);
            blendMode = blend;
            UpdatePageCompositorRegistration();
            MarkPageCompositorDirty();
            SetMaterialDirty();
        }

        public void SetPrimaryFill(FigForgeFill fill)
        {
            fill.Normalize();
            if (fills == null) fills = new List<FigForgeFill>();
            // No-change early-out: state-swap callers (FigForgeScrollbarStates et al.)
            // re-apply on every OnEnable — the scrollbar fade re-enables per frame,
            // and without this check each call released the cached surface and
            // marked the page compositor dirty, recompositing the page every frame.
            if (fills.Count > 0 && fills[0].ValueEquals(fill)) return;
            if (fills.Count == 0) fills.Add(fill);
            else fills[0] = fill;
            ReleaseCachedSurface();
            MarkPageCompositorDirty();
            _listsNormalized = false;
            NormalizeLists();
            RefreshRecipe();
            SetVerticesDirty();
            SetMaterialDirty();
        }

        // Per-corner radii px (tl, tr, br, bl) — e.g. list rows matching their container's
        // rounded corners. Keeps fills/strokes/effects untouched.
        public void SetCorners(Vector4 cornerRadii)
        {
            if (corners == cornerRadii) return;
            corners = cornerRadii;
            ReleaseCachedSurface();
            MarkPageCompositorDirty();
            SetVerticesDirty();
            SetMaterialDirty();
        }

        public void SetStyle(FigForgeShapeStyle style)
        {
            var nextFills = new List<FigForgeFill>();
            if (VisibleFill(style.fill)) nextFills.Add(style.fill);

            var nextStrokes = new List<FigForgeStrokeLayer>();
            if (style.stroke.enabled)
            {
                nextStrokes.Add(FigForgeStrokeLayer.Create(
                    FigForgeFill.Solid(style.stroke.color),
                    style.stroke.weight,
                    style.stroke.align,
                    style.stroke.style == FigForgeStrokeStyle.Dashed,
                    style.stroke.dash,
                    style.stroke.gap));
            }

            var nextEffects = new List<FigForgeEffectLayer>();
            if (style.shadowColor.a > 0.001f)
                nextEffects.Add(FigForgeEffectLayer.DropShadow(style.shadowColor, style.shadowOffset, style.shadowBlur, style.shadowSpread));

            ConfigureLayers(nextFills, nextStrokes, nextEffects, style.corners);
        }

        public override Material material
        {
            get
            {
                var active = ActiveRenderMaterial();
                UpdateActiveMaterial(active);
                return active != null ? active : base.material;
            }
            set { }
        }

        public override Material materialForRendering
        {
            get
            {
                var active = ActiveRenderMaterial();
                UpdateActiveMaterial(active);
                var renderMaterial = base.materialForRendering;
                if (renderMaterial != null && renderMaterial != active)
                    UpdateActiveMaterial(renderMaterial);
                return renderMaterial;
            }
        }

        public override Texture mainTexture
        {
            get
            {
                if (ShouldUseCachedSource())
                {
                    EnsureCachedSurface();
                    // Compositor-owned: present the pre-blended texture. Falls back to
                    // the cached surface (normal alpha) until the first composite lands.
                    var blended = BlendedSurfaceOrNull();
                    if (blended != null) return blended;
                    if (_cachedSurface != null) return _cachedSurface;
                }
                EnsurePaintTexture();
                return _paintTexture != null ? _paintTexture : Texture2D.whiteTexture;
            }
        }

        // The compositor's pre-blended premultiplied texture for this layer, or null
        // when this graphic isn't compositor-owned (or hasn't been composited yet).
        RenderTexture BlendedSurfaceOrNull()
        {
            var comp = _pageCompositor != null ? _pageCompositor : GetComponentInParent<FigForgePageCompositor>();
            if (comp == null || !comp.ShouldRenderLayer(this)) return null;
            return comp.GetBlendedSurface(this);
        }

        Material ActiveRenderMaterial()
        {
            if (!ShouldUseCachedSource()) return EnsureDirectMaterial();
            return EnsureCachedMaterial();
        }

        Material EnsureDirectMaterial()
        {
            if (_directMaterial != null) return _directMaterial;
            var shader = Shader.Find("FigForge/LayeredRect4");
            if (shader != null)
                _directMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            return _directMaterial;
        }

        // GrabPass (FigForge/CachedBlend, for destination-reading Tier-2 blends) exists
        // ONLY in the Built-in pipeline. Under any SRP (URP/HDRP) it's unavailable and the
        // shader would render magenta, so we detect the active pipeline — honouring a
        // Quality-level RP override via currentRenderPipeline — and degrade Tier-2 to
        // normal alpha compositing through CachedQuad. Full Tier-2 fidelity under URP is
        // the job of the GrabPass-free page compositor (FigForgePageCompositor), which
        // composites against bound textures instead of grabbing the framebuffer.
        internal static bool DestinationReadBlendSupported
            => GraphicsSettings.currentRenderPipeline == null;

        Material EnsureCachedMaterial()
        {
            // Compositor-owned Tier-2 layers present their pre-blended texture through
            // the cheap premult-over quad. Otherwise Tier-2 falls back to the GrabPass
            // blend shader (Built-in only); under an SRP without a compositor it
            // degrades to the quad (normal alpha). Recreate if the chosen shader changed.
            string shaderName = (BlendTier(blendMode) == 2 && DestinationReadBlendSupported && !IsRenderedByPageCompositor())
                ? "FigForge/CachedBlend" : "FigForge/CachedQuad";
            if (_cachedMaterial != null && _cachedMaterial.shader != null && _cachedMaterial.shader.name == shaderName)
                return _cachedMaterial;
            if (_cachedMaterial != null) DestroyRuntimeMaterial(_cachedMaterial);
            var shader = Shader.Find(shaderName);
            _cachedMaterial = shader != null ? new Material(shader) { hideFlags = HideFlags.HideAndDontSave } : null;
            return _cachedMaterial;
        }

        Material EnsureBakeMaterial()
        {
            if (_bakeMaterial != null) return _bakeMaterial;
            var shader = Shader.Find("FigForge/LayeredRect4");
            if (shader != null)
                _bakeMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            return _bakeMaterial;
        }

        Material EnsureBlurMaterial()
        {
            if (_blurMaterial != null) return _blurMaterial;
            var shader = Shader.Find("FigForge/LayerBlur");
            if (shader != null)
                _blurMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            return _blurMaterial;
        }

        void ClampGraphicState()
        {
            color = Color.white;
        }

        void EnsureCanvasRenderer()
        {
            if (GetComponent<CanvasRenderer>() == null)
                gameObject.AddComponent<CanvasRenderer>();
        }

        void UpdateActiveMaterial(Material target)
        {
            if (target == null) return;
            if (ShouldUseCachedSource())
            {
                EnsureCachedSurface();
                UpdateCachedMaterialProperties(target);
                return;
            }
            UpdateResolverMaterialProperties(target, appearanceOpacity, true, false, false, 0f, true);
        }

        void UpdateResolverMaterialProperties(Material target, float opacity, bool alphaBlend, bool premultiplyOutput,
                                              bool useMeshPadOverride, float meshPadOverride, bool layerBlurAffectsEdge)
        {
            if (target == null) return;
            NormalizeLists();
            RefreshRecipe();
            var stroke = FirstStroke();
            EnsurePaintTexture();

            target.mainTexture = _paintTexture != null ? _paintTexture : Texture2D.whiteTexture;
            target.SetFloat("_AppearanceOpacity", Mathf.Clamp01(opacity));
            target.SetFloat("_PremultiplyOutput", premultiplyOutput ? 1f : 0f);
            target.SetFloat("_LayerBlurAffectsEdge", layerBlurAffectsEdge ? 1f : 0f);
            target.SetVector("_MeshPadOverride", useMeshPadOverride ? new Vector4(1f, meshPadOverride, 0f, 0f) : Vector4.zero);
            ApplyBlendState(target, alphaBlend);
            target.SetFloat("_FillCount", Mathf.Min(VisibleFillCount(), 4));
            target.SetVector("_FillFlags", FillFlags());
            // All paint/shadow tints must reach the shader as RAW sRGB: the frag
            // composites paints in Figma (sRGB) space and converts to project space
            // exactly once itself via figmaToProjectRgb — mirroring RoundedRect's
            // unpackColor (raw sRGB vertex bytes, converted once). What guarantees
            // raw delivery is the shader DECLARING these properties as Vector, not
            // Color — in a Linear project Unity sRGB→linear-converts Color-DECLARED
            // properties at upload regardless of whether SetColor or SetVector was
            // called (pixel-verified: SetVector into a Color-declared property still
            // arrived linearized and double-darkened every solid fill).
            target.SetVector("_FillColor0", FillColor(0));
            target.SetVector("_FillColor1", FillColor(1));
            target.SetVector("_FillColor2", FillColor(2));
            target.SetVector("_FillColor3", FillColor(3));
            target.SetVector("_FillKind", FillKind());
            target.SetVector("_FillDir0", FillDir(0));
            target.SetVector("_FillDir1", FillDir(1));
            target.SetVector("_FillDir2", FillDir(2));
            target.SetVector("_FillDir3", FillDir(3));
            target.SetFloat("_StrokeCount", Mathf.Min(VisibleStrokeCount(), 4));
            target.SetVector("_StrokeFlags", StrokeFlags());
            target.SetVector("_StrokeColor0", StrokeColor(0));
            target.SetVector("_StrokeColor1", StrokeColor(1));
            target.SetVector("_StrokeColor2", StrokeColor(2));
            target.SetVector("_StrokeColor3", StrokeColor(3));
            target.SetVector("_StrokeKind", StrokeKind());
            target.SetVector("_StrokeDir0", StrokeDir(0));
            target.SetVector("_StrokeDir1", StrokeDir(1));
            target.SetVector("_StrokeDir2", StrokeDir(2));
            target.SetVector("_StrokeDir3", StrokeDir(3));
            target.SetFloat("_StrokeWidth", stroke.enabled ? stroke.weight : 0f);
            target.SetFloat("_StrokeOutset", stroke.Outset);
            target.SetVector("_StrokeDash", StrokeDash(stroke));
            target.SetVector("_Radius", corners);
            var rt = rectTransform.rect;
            target.SetVector("_Size", new Vector4(Mathf.Max(1f, rt.width), Mathf.Max(1f, rt.height), 0, 0));
            target.SetFloat("_ShadowCount", Mathf.Min(VisibleDropShadowCount(), 4));
            target.SetVector("_ShadowColor0", ShadowColor(0));
            target.SetVector("_ShadowColor1", ShadowColor(1));
            target.SetVector("_ShadowColor2", ShadowColor(2));
            target.SetVector("_ShadowColor3", ShadowColor(3));
            target.SetVector("_ShadowOffset0", ShadowOffset(0));
            target.SetVector("_ShadowOffset1", ShadowOffset(1));
            target.SetVector("_ShadowOffset2", ShadowOffset(2));
            target.SetVector("_ShadowOffset3", ShadowOffset(3));
            target.SetVector("_ShadowParams0", ShadowParams(0));
            target.SetVector("_ShadowParams1", ShadowParams(1));
            target.SetVector("_ShadowParams2", ShadowParams(2));
            target.SetVector("_ShadowParams3", ShadowParams(3));
            target.SetVector("_ShadowBehind", ShadowBehindFlags());
            target.SetFloat("_InnerShadowCount", Mathf.Min(VisibleInnerShadowCount(), 4));
            target.SetVector("_InnerShadowColor0", InnerShadowColor(0));
            target.SetVector("_InnerShadowColor1", InnerShadowColor(1));
            target.SetVector("_InnerShadowColor2", InnerShadowColor(2));
            target.SetVector("_InnerShadowColor3", InnerShadowColor(3));
            target.SetVector("_InnerShadowOffset0", InnerShadowOffset(0));
            target.SetVector("_InnerShadowOffset1", InnerShadowOffset(1));
            target.SetVector("_InnerShadowOffset2", InnerShadowOffset(2));
            target.SetVector("_InnerShadowOffset3", InnerShadowOffset(3));
            target.SetVector("_InnerShadowParams0", InnerShadowParams(0));
            target.SetVector("_InnerShadowParams1", InnerShadowParams(1));
            target.SetVector("_InnerShadowParams2", InnerShadowParams(2));
            target.SetVector("_InnerShadowParams3", InnerShadowParams(3));
            target.SetVector("_LayerBlur", LayerBlurParams());
        }

        void UpdateCachedMaterialProperties(Material target)
        {
            // Compositor-owned: opacity is already baked into the blended texture's
            // coverage at composite time — applying it here would double it.
            var blended = BlendedSurfaceOrNull();
            if (blended != null)
            {
                target.mainTexture = blended;
                target.SetFloat("_AppearanceOpacity", 1f);
            }
            else
            {
                target.mainTexture = _cachedSurface != null ? _cachedSurface : Texture2D.whiteTexture;
                target.SetFloat("_AppearanceOpacity", appearanceOpacity);
            }
            target.SetFloat("_BlendMode", (float)blendMode);
            ApplyCachedBlendState(target);
        }

        void ApplyCachedBlendState(Material target)
        {
            var src = UnityEngine.Rendering.BlendMode.One;
            var dst = UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha;
            var op = BlendOp.Add;
            var srcA = UnityEngine.Rendering.BlendMode.One;
            var dstA = UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha;
            var opA = BlendOp.Add;

            switch (blendMode)
            {
                case FigForgeBlendMode.Multiply:
                    src = UnityEngine.Rendering.BlendMode.DstColor;
                    dst = UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha;
                    break;
                case FigForgeBlendMode.Screen:
                    src = UnityEngine.Rendering.BlendMode.OneMinusDstColor;
                    dst = UnityEngine.Rendering.BlendMode.One;
                    break;
                case FigForgeBlendMode.PlusLighter:
                    src = UnityEngine.Rendering.BlendMode.One;
                    dst = UnityEngine.Rendering.BlendMode.One;
                    srcA = UnityEngine.Rendering.BlendMode.One;
                    dstA = UnityEngine.Rendering.BlendMode.One;
                    break;
            }

            target.SetInt("_SrcBlend", (int)src);
            target.SetInt("_DstBlend", (int)dst);
            target.SetInt("_SrcBlendA", (int)srcA);
            target.SetInt("_DstBlendA", (int)dstA);
            target.SetInt("_BlendOp", (int)op);
            target.SetInt("_BlendOpA", (int)opA);
        }

        void NormalizeLists()
        {
            if (_listsNormalized) return;
            _listsNormalized = true;
            appearanceOpacity = Mathf.Clamp01(appearanceOpacity);
            for (int i = 0; i < fills.Count; i++)
            {
                var f = fills[i];
                f.Normalize();
                fills[i] = f;
            }
            for (int i = 0; i < strokes.Count; i++)
            {
                var s = strokes[i];
                s.Normalize();
                strokes[i] = s;
            }
            for (int i = 0; i < effects.Count; i++)
            {
                var e = effects[i];
                e.Normalize();
                effects[i] = e;
            }
        }

        void RefreshRecipe()
        {
            int f = VisibleFillCount(), s = VisibleStrokeCount(), e = VisibleEffectCount();
            if (f == _recipeFills && s == _recipeStrokes && e == _recipeEffects && !string.IsNullOrEmpty(recipe))
                return;
            _recipeFills = f;
            _recipeStrokes = s;
            _recipeEffects = e;
            recipe = $"F{f}_S{s}_E{e}";
        }

        FigForgeStrokeLayer FirstStroke()
        {
            for (int i = 0; i < strokes.Count; i++)
                if (strokes[i].enabled) return strokes[i];
            return default;
        }

        int VisibleFillCount()
        {
            int count = 0;
            for (int i = 0; i < fills.Count; i++)
                if (VisibleFill(fills[i])) count++;
            return count;
        }

        int VisibleStrokeCount()
        {
            int count = 0;
            for (int i = 0; i < strokes.Count; i++)
                if (strokes[i].enabled) count++;
            return count;
        }

        int VisibleEffectCount()
        {
            int count = 0;
            for (int i = 0; i < effects.Count; i++)
                if (effects[i].enabled) count++;
            return count;
        }

        int VisibleDropShadowCount()
        {
            int count = 0;
            for (int i = 0; i < effects.Count; i++)
                if (IsVisibleDropShadow(effects[i])) count++;
            return count;
        }

        static bool IsVisibleDropShadow(FigForgeEffectLayer effect)
            => effect.enabled && effect.kind == FigForgeEffectKind.DropShadow && effect.color.a > 0.001f;

        int VisibleInnerShadowCount()
        {
            int count = 0;
            for (int i = 0; i < effects.Count; i++)
                if (IsVisibleInnerShadow(effects[i])) count++;
            return count;
        }

        static bool IsVisibleInnerShadow(FigForgeEffectLayer effect)
            => effect.enabled && effect.kind == FigForgeEffectKind.InnerShadow && effect.color.a > 0.001f;

        int VisibleLayerBlurCount()
        {
            int count = 0;
            for (int i = 0; i < effects.Count; i++)
                if (IsVisibleLayerBlur(effects[i])) count++;
            return count;
        }

        static bool IsVisibleLayerBlur(FigForgeEffectLayer effect)
            => effect.enabled && effect.kind == FigForgeEffectKind.LayerBlur && Mathf.Max(effect.blur, effect.endBlur) > 0.001f;

        Vector4 FillFlags()
        {
            return new Vector4(FillFlag(0), FillFlag(1), FillFlag(2), FillFlag(3));
        }

        float FillFlag(int index)
        {
            int f = VisibleFillIndex(index);
            return f >= 0 && fills[f].HasGradient ? 1f : 0f;
        }

        Color FillColor(int index)
        {
            int f = VisibleFillIndex(index);
            if (f < 0) return new Color(0, 0, 0, 0);
            var paint = fills[f];
            return paint.HasGradient ? paint.gradient.Evaluate(0f) : paint.color;
        }

        Vector4 FillKind()
        {
            return new Vector4(FillKindAt(0), FillKindAt(1), FillKindAt(2), FillKindAt(3));
        }

        float FillKindAt(int index)
        {
            int f = VisibleFillIndex(index);
            if (f < 0 || !fills[f].HasGradient) return 0f;
            return (float)fills[f].gradientKind + 1f;
        }

        Vector4 FillDir(int index)
        {
            int f = VisibleFillIndex(index);
            if (f < 0) return Vector4.zero;
            var d = fills[f].dir;
            return new Vector4(d.x, d.y, 0, 0);
        }

        int VisibleFillIndex(int visibleIndex)
        {
            int count = 0;
            for (int i = 0; i < fills.Count; i++)
            {
                if (!VisibleFill(fills[i])) continue;
                if (count == visibleIndex) return i;
                count++;
            }
            return -1;
        }

        Vector4 StrokeFlags()
        {
            return new Vector4(StrokeFlag(0), StrokeFlag(1), StrokeFlag(2), StrokeFlag(3));
        }

        float StrokeFlag(int index)
        {
            int s = VisibleStrokeIndex(index);
            return s >= 0 && strokes[s].paint.HasGradient ? 1f : 0f;
        }

        Color StrokeColor(int index)
        {
            int s = VisibleStrokeIndex(index);
            if (s < 0) return new Color(0, 0, 0, 0);
            var paint = strokes[s].paint;
            return paint.HasGradient ? paint.gradient.Evaluate(0f) : paint.color;
        }

        Vector4 StrokeKind()
        {
            return new Vector4(StrokeKindAt(0), StrokeKindAt(1), StrokeKindAt(2), StrokeKindAt(3));
        }

        float StrokeKindAt(int index)
        {
            int s = VisibleStrokeIndex(index);
            if (s < 0 || !strokes[s].paint.HasGradient) return 0f;
            return (float)strokes[s].paint.gradientKind + 1f;
        }

        Vector4 StrokeDir(int index)
        {
            int s = VisibleStrokeIndex(index);
            if (s < 0) return Vector4.zero;
            var d = strokes[s].paint.dir;
            return new Vector4(d.x, d.y, 0, 0);
        }

        static Vector4 StrokeDash(FigForgeStrokeLayer stroke)
        {
            if (!stroke.enabled || stroke.style != FigForgeStrokeStyle.Dashed) return Vector4.zero;
            float dash = stroke.dash > 0.001f ? stroke.dash : Mathf.Max(1f, stroke.weight * 3f);
            float gap = stroke.gap > 0.001f ? stroke.gap : dash;
            return new Vector4(1f, dash, gap, 0f);
        }

        int VisibleStrokeIndex(int visibleIndex)
        {
            int count = 0;
            for (int i = 0; i < strokes.Count; i++)
            {
                if (!strokes[i].enabled) continue;
                if (count == visibleIndex) return i;
                count++;
            }
            return -1;
        }

        Color ShadowColor(int index)
        {
            int s = VisibleDropShadowIndex(index);
            return s >= 0 ? effects[s].color : new Color(0, 0, 0, 0);
        }

        Vector4 ShadowOffset(int index)
        {
            int s = VisibleDropShadowIndex(index);
            if (s < 0) return Vector4.zero;
            var offset = effects[s].offset;
            return new Vector4(offset.x, offset.y, 0f, 0f);
        }

        Vector4 ShadowParams(int index)
        {
            int s = VisibleDropShadowIndex(index);
            if (s < 0) return Vector4.zero;
            var e = effects[s];
            return new Vector4(e.blur, e.spread, 0f, 0f);
        }

        Vector4 ShadowBehindFlags()
        {
            return new Vector4(ShadowBehindFlag(0), ShadowBehindFlag(1), ShadowBehindFlag(2), ShadowBehindFlag(3));
        }

        float ShadowBehindFlag(int index)
        {
            int s = VisibleDropShadowIndex(index);
            return s >= 0 && effects[s].showBehindShape ? 1f : 0f;
        }

        int VisibleDropShadowIndex(int visibleIndex)
        {
            int count = 0;
            for (int i = 0; i < effects.Count; i++)
            {
                if (!IsVisibleDropShadow(effects[i])) continue;
                if (count == visibleIndex) return i;
                count++;
            }
            return -1;
        }

        Color InnerShadowColor(int index)
        {
            int s = VisibleInnerShadowIndex(index);
            return s >= 0 ? effects[s].color : new Color(0, 0, 0, 0);
        }

        Vector4 InnerShadowOffset(int index)
        {
            int s = VisibleInnerShadowIndex(index);
            if (s < 0) return Vector4.zero;
            var offset = effects[s].offset;
            return new Vector4(offset.x, offset.y, 0f, 0f);
        }

        Vector4 InnerShadowParams(int index)
        {
            int s = VisibleInnerShadowIndex(index);
            if (s < 0) return Vector4.zero;
            var e = effects[s];
            return new Vector4(e.blur, e.spread, 0f, 0f);
        }

        int VisibleInnerShadowIndex(int visibleIndex)
        {
            int count = 0;
            for (int i = 0; i < effects.Count; i++)
            {
                if (!IsVisibleInnerShadow(effects[i])) continue;
                if (count == visibleIndex) return i;
                count++;
            }
            return -1;
        }

        Vector4 LayerBlurParams()
        {
            for (int i = 0; i < effects.Count; i++)
            {
                var e = effects[i];
                if (!IsVisibleLayerBlur(e)) continue;
                float progressive = e.blurMode == FigForgeLayerBlurMode.Progressive ? 1f : 0f;
                float end = e.endBlur > 0.001f ? e.endBlur : e.blur;
                return new Vector4(1f, progressive, e.blur, end);
            }
            return Vector4.zero;
        }

        void EnsurePaintTexture()
        {
            int count = Mathf.Min(VisibleFillCount(), 4) + Mathf.Min(VisibleStrokeCount(), 4);
            if (count == 0)
            {
                _paintTexture = Texture2D.whiteTexture;
                return;
            }
            _paintTexture = FigForgePaintTextureCache.Get(fills, strokes);
        }

        float StrokeOutset()
        {
            var s = FirstStroke();
            return s.enabled ? s.Outset : 0f;
        }

        float ShadowReach()
        {
            float reach = 0f;
            int count = 0;
            for (int i = 0; i < effects.Count && count < 4; i++)
            {
                var shadow = effects[i];
                if (!IsVisibleDropShadow(shadow)) continue;
                reach = Mathf.Max(reach,
                    Mathf.Max(Mathf.Abs(shadow.offset.x), Mathf.Abs(shadow.offset.y))
                    + 1.75f * Mathf.Max(0f, shadow.blur) + Mathf.Max(0f, shadow.spread) + 1f);
                count++;
            }
            return reach;
        }

        float LayerBlurReach()
        {
            for (int i = 0; i < effects.Count; i++)
            {
                var e = effects[i];
                if (!IsVisibleLayerBlur(e)) continue;
                return 1.75f * Mathf.Max(e.blur, e.endBlur) + 1f;
            }
            return 0f;
        }

        float MeshPad() => Mathf.Max(Mathf.Max(StrokeOutset(), ShadowReach()), LayerBlurReach());

        bool ShouldUseCachedSource()
        {
            if (compositorMode == FigForgeCompositorMode.Direct) return false;
            if (WouldCacheHitTextureLimit()) return false;
            if (compositorMode == FigForgeCompositorMode.CachedSource) return true;
            // Tier-2 (destination-reading) modes present through the GrabPass blend
            // shader, which needs the flattened cached surface.
            if (BlendTier(blendMode) == 2) return true;
            // Background blur composites through the page compositor even at Normal
            // blend — the quad presents the pre-composited glass.
            if (BackdropBlurRadius() > 0.001f) return true;
            if (BlendTier(blendMode) == 1 && blendMode != FigForgeBlendMode.PassThrough && blendMode != FigForgeBlendMode.Normal)
                return true;
            return VisibleDropShadowCount() > 0
                || VisibleInnerShadowCount() > 0
                || VisibleLayerBlurCount() > 0
                || VisibleFillCount() + VisibleStrokeCount() > 3;
        }

        public static int BlendTier(FigForgeBlendMode mode)
        {
            switch (mode)
            {
                case FigForgeBlendMode.PassThrough:
                case FigForgeBlendMode.Normal:
                case FigForgeBlendMode.Multiply:
                case FigForgeBlendMode.Screen:
                case FigForgeBlendMode.PlusLighter:
                    return 1;
                default:
                    return 2;
            }
        }

        bool WouldCacheHitTextureLimit()
        {
            float pad = MeshPad();
            var rect = rectTransform.rect;
            float scale = RawScaleFactorCached();
            int limit = MaxSurfaceTextureSize();
            return scale > MaxCachedSurfaceScale
                || Mathf.CeilToInt(Mathf.Max(1f, rect.width + 2f * pad) * scale) >= limit
                || Mathf.CeilToInt(Mathf.Max(1f, rect.height + 2f * pad) * scale) >= limit;
        }

        void EnsureCachedSurface()
        {
            var bakeMaterial = EnsureBakeMaterial();
            if (bakeMaterial == null) return;

            EnsurePaintTexture();
            float scale = SurfaceScaleFactor();
            if (!CurrentSurfaceDescriptor(out int width, out int height, out float pad, out uint hash))
                return;
            if (_cachedSurface != null && _cachedSurfaceHash == hash && _cachedSurfaceWidth == width && _cachedSurfaceHeight == height)
                return;

            ReleaseCachedSurface();
            var layerBlur = LayerBlurParams();
            var blurMaterial = layerBlur.x > 0.5f ? EnsureBlurMaterial() : null;
            var scaledBlur = new Vector4(layerBlur.x, layerBlur.y, layerBlur.z * scale, layerBlur.w * scale);
            UpdateResolverMaterialProperties(bakeMaterial, 1f, false, true, true, pad, false);
            // The string key is built only here, off the re-bake path — the steady-state
            // validity check compares the uint hash without allocating.
            string key = hash.ToString("X8");
            _cachedSurface = FigForgeCompositorCache.Acquire(key, width, height, _paintTexture, bakeMaterial, blurMaterial, scaledBlur);
            if (_cachedSurface == null) return;
            _cachedSurfaceKey = key;
            _cachedSurfaceHash = hash;
            _cachedSurfaceWidth = width;
            _cachedSurfaceHeight = height;
        }

        void ReleaseCachedSurface()
        {
            if (!string.IsNullOrEmpty(_cachedSurfaceKey))
                FigForgeCompositorCache.Release(_cachedSurfaceKey);
            _cachedSurface = null;
            _cachedSurfaceKey = null;
            _cachedSurfaceHash = 0;
            _cachedSurfaceWidth = 0;
            _cachedSurfaceHeight = 0;
        }

        public RenderTexture GetCompositorSurface()
        {
            EnsureCachedSurface();
            return _cachedSurface;
        }

        public bool IsRenderedByPageCompositor()
        {
            var comp = _pageCompositor != null ? _pageCompositor : GetComponentInParent<FigForgePageCompositor>();
            return comp != null && comp.ShouldRenderLayer(this);
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
            // No capturable canvas (Screen Space - Overlay etc.) → stay on the
            // per-graphic fallback: GrabPass under Built-in, plain alpha under SRP.
            if (!FigForgePageCapture.CanCapture(canvas)) return;
            _pageCompositor = FindOrCreatePageCompositor();
            if (_pageCompositor != null)
                _pageCompositor.Register(this);
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

            var c = canvas;
            if (c != null) return c.gameObject.AddComponent<FigForgePageCompositor>();

            return null;
        }

        void MarkPageCompositorDirty()
        {
            var comp = _pageCompositor != null ? _pageCompositor : GetComponentInParent<FigForgePageCompositor>();
            if (comp != null) comp.MarkDirty();
        }

        float SurfaceScaleFactor()
        {
            RefreshScaleCache();
            return _cachedScaleFactor;
        }

        // Raw (unquantized, unclamped) scale through the same camera-keyed cache —
        // WouldCacheHitTextureLimit also runs per canvas update and must not redo the
        // WorldToScreenPoint projection while nothing moves.
        float RawScaleFactorCached()
        {
            RefreshScaleCache();
            return _cachedRawScaleFactor;
        }

        void RefreshScaleCache()
        {
            // The only per-frame-varying input is the camera projection (used by
            // RawSurfaceScaleFactor via WorldToScreenPoint). Reuse the cached quantized
            // scale unless the camera actually moved; rect-size changes invalidate it via
            // OnRectTransformDimensionsChange. Since ProjectionCamera() now keys on the
            // PRESENTATION camera (stable during editor navigation, not the Scene view),
            // this stays a cache-hit through pan/orbit/zoom — the editor pays no re-bake
            // for merely looking around, matching play mode.
            Camera cam = ProjectionCamera();
            Matrix4x4 camKey = cam != null ? cam.projectionMatrix * cam.worldToCameraMatrix : Matrix4x4.identity;
            if (_cachedScaleFactor > 0f && camKey == _scaleCamKey)
                return;
            _scaleCamKey = camKey;
            _cachedRawScaleFactor = RawSurfaceScaleFactor();
            float scale = Mathf.Clamp(_cachedRawScaleFactor, 1f, MaxCachedSurfaceScale);
            _cachedScaleFactor = Mathf.Ceil(scale * 4f) * 0.25f;
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
            // Bake resolution must track the camera that PRESENTS this surface at
            // runtime — the canvas's worldCamera — NOT the editor Scene-view camera.
            // Keying on the Scene view made every pan/orbit/zoom change camKey, which
            // (a) thrashed the scale cache (O(rects) WorldToScreenPoint per repaint) and
            // (b) drifted the quantized bake size → re-bake + compositor dirty storm.
            // Play mode never touches the Scene-view camera — which is exactly why it's
            // butter-smooth — so the editor should bake for the same camera and stay
            // cache-hot through navigation. Overlay has no present camera:
            // WorldToScreenPoint(null) maps world→screen 1:1 (× canvasScaler), the right
            // navigation-stable scale, so don't substitute the Scene-view camera there
            // either. Fall back to the Scene-view/current camera ONLY when a camera-space
            // canvas has no camera assigned, or there's no canvas at all.
            var c = canvas;
            if (c != null)
            {
                if (c.renderMode == RenderMode.ScreenSpaceOverlay) return null;
                if (c.worldCamera != null) return c.worldCamera;
            }
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                var sv = EditorSceneViewCamera();
                if (sv != null) return sv;
            }
#endif
            return Camera.current;
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

        bool CurrentSurfaceDescriptor(out int width, out int height, out float pad, out uint hash)
        {
            NormalizeLists();
            RefreshRecipe();
            pad = MeshPad();
            var rect = rectTransform.rect;
            float scale = SurfaceScaleFactor();
            int limit = MaxSurfaceTextureSize();
            width = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(1f, rect.width + 2f * pad) * scale), 1, limit);
            height = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(1f, rect.height + 2f * pad) * scale), 1, limit);
            hash = ComputeSurfaceHash(width, height, pad);
            return true;
        }

        static int MaxSurfaceTextureSize()
        {
            int systemLimit = SystemInfo.maxTextureSize > 0 ? SystemInfo.maxTextureSize : 8192;
            return Mathf.Clamp(systemLimit, 4096, 16384);
        }

        void CheckCachedSurfaceValidity()
        {
            if (_cachedSurface == null || string.IsNullOrEmpty(_cachedSurfaceKey))
                return;

            if (!ShouldUseCachedSource())
            {
                ReleaseCachedSurface();
                MarkPageCompositorDirty();
                SetVerticesDirty();
                SetMaterialDirty();
                return;
            }

            if (!CurrentSurfaceDescriptor(out int width, out int height, out _, out uint hash))
                return;

            if (_cachedSurfaceHash == hash && _cachedSurfaceWidth == width && _cachedSurfaceHeight == height)
                return;

            ReleaseCachedSurface();
            MarkPageCompositorDirty();
            SetVerticesDirty();
            SetMaterialDirty();
        }

        void HandleWillRenderCanvases()
        {
            // Skip off-screen elements: when uGUI has culled this renderer (e.g.
            // RectMask2D fast-cull for content scrolled out of a viewport), it isn't
            // being drawn, so don't spend the per-canvas-update budget re-hashing or
            // re-baking its surface. This tracks uGUI's OWN draw decision exactly — we
            // never skip something that's actually drawn — and self-heals: when the
            // renderer un-culls, the next validity check re-bakes if the content drifted
            // while it was off-screen.
            if (canvasRenderer != null && canvasRenderer.cull) return;
            CheckCachedSurfaceValidity();
        }

        uint ComputeSurfaceHash(int width, int height, float pad)
        {
            unchecked
            {
                uint hash = 2166136261u;
                Hash(ref hash, SurfaceShaderRevision);
                Hash(ref hash, width);
                Hash(ref hash, height);
                Hash(ref hash, pad);
                Hash(ref hash, rectTransform.rect.width);
                Hash(ref hash, rectTransform.rect.height);
                Hash(ref hash, corners);
                Hash(ref hash, StrokeOutset());
                Hash(ref hash, LayerBlurParams());

                int visibleFills = 0;
                for (int i = 0; i < fills.Count && visibleFills < 4; i++)
                {
                    if (!VisibleFill(fills[i])) continue;
                    Hash(ref hash, fills[i]);
                    visibleFills++;
                }

                int visibleStrokes = 0;
                for (int i = 0; i < strokes.Count && visibleStrokes < 4; i++)
                {
                    if (!strokes[i].enabled) continue;
                    Hash(ref hash, strokes[i]);
                    visibleStrokes++;
                }

                int visibleEffects = 0;
                for (int i = 0; i < effects.Count && visibleEffects < 9; i++)
                {
                    if (!effects[i].enabled) continue;
                    Hash(ref hash, effects[i]);
                    visibleEffects++;
                }

                return hash;
            }
        }

        void ApplyBlendState(Material target, bool alphaBlend)
        {
            target.SetFloat("_BlendMode", (float)blendMode);
            target.SetInt("_SrcBlend", alphaBlend ? (int)UnityEngine.Rendering.BlendMode.SrcAlpha : (int)UnityEngine.Rendering.BlendMode.One);
            target.SetInt("_DstBlend", alphaBlend ? (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha : (int)UnityEngine.Rendering.BlendMode.Zero);
            target.SetInt("_BlendOp", (int)BlendOp.Add);
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            // Compositor-owned layers populate the same padded quad as the cached
            // present path — they just sample the pre-blended texture instead. The
            // quad lives at this graphic's own hierarchy position, so masks, raycasts
            // and z-order against foreign uGUI behave like any other graphic.
            UpdateActiveMaterial(ActiveRenderMaterial());
            var r = GetPixelAdjustedRect();
            float pad = MeshPad();
            float x0 = r.x - pad, y0 = r.y - pad, x1 = r.xMax + pad, y1 = r.yMax + pad;
            vh.Clear();
            AddVert(vh, new Vector3(x0, y0), color, new Vector4(0, 0, 0, 0));
            AddVert(vh, new Vector3(x0, y1), color, new Vector4(0, 1, 0, 0));
            AddVert(vh, new Vector3(x1, y1), color, new Vector4(1, 1, 0, 0));
            AddVert(vh, new Vector3(x1, y0), color, new Vector4(1, 0, 0, 0));
            vh.AddTriangle(0, 1, 2);
            vh.AddTriangle(2, 3, 0);
        }

        static void AddVert(VertexHelper vh, Vector3 pos, Color32 color, Vector4 uv0)
        {
            var v = UIVertex.simpleVert;
            v.position = pos;
            v.color = color;
            v.uv0 = uv0;
            vh.AddVert(v);
        }

        static bool VisibleFill(FigForgeFill fill)
        {
            if (fill.disabled) return false;
            if (fill.kind == FigForgeFillKind.Solid) return fill.color.a > 0.001f;
            if (fill.gradient == null) return fill.color.a > 0.001f;
            var alphas = fill.gradient.alphaKeys;
            if (alphas == null || alphas.Length == 0) return fill.color.a > 0.001f;
            for (int i = 0; i < alphas.Length; i++)
                if (alphas[i].alpha > 0.001f) return true;
            return false;
        }

        protected override void OnEnable()
        {
            EnsureCanvasRenderer();
            _listsNormalized = false; // re-normalize once after (re)deserialization/enable
            Canvas.willRenderCanvases -= HandleWillRenderCanvases;
            Canvas.willRenderCanvases += HandleWillRenderCanvases;
            base.OnEnable();
            UpdatePageCompositorRegistration();
            ClampGraphicState();
            SetVerticesDirty();
            SetMaterialDirty();
        }

        protected override void OnRectTransformDimensionsChange()
        {
            base.OnRectTransformDimensionsChange();
            _cachedScaleFactor = -1f; // rect size feeds the surface scale — re-evaluate it
            // Don't release the cached surface here: CheckCachedSurfaceValidity releases it
            // only if the quantized surface size actually changed, so sub-pixel layout
            // settling (list rows, ContentSizeFitter) no longer thrashes a re-bake.
            MarkPageCompositorDirty();
            SetVerticesDirty();
            SetMaterialDirty();
        }

        protected override void OnCanvasHierarchyChanged()
        {
            base.OnCanvasHierarchyChanged();
            ReleaseCachedSurface();
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
            ReleaseCachedSurface();
            base.OnDisable();
        }

        protected override void OnDestroy()
        {
            Canvas.willRenderCanvases -= HandleWillRenderCanvases;
            ReleaseCachedSurface();
            DestroyRuntimeMaterial(_directMaterial);
            DestroyRuntimeMaterial(_cachedMaterial);
            DestroyRuntimeMaterial(_bakeMaterial);
            DestroyRuntimeMaterial(_blurMaterial);
            base.OnDestroy();
        }

#if UNITY_EDITOR
        protected override void OnValidate()
        {
            ClampGraphicState();
            _listsNormalized = false;
            NormalizeLists();
            RefreshRecipe();
            ReleaseCachedSurface();
            if (FigForgePageCompositor.SuppressAutoCreate)
            {
                base.OnValidate();
                SetVerticesDirty();
                SetMaterialDirty();
                return;
            }
            // UpdatePageCompositorRegistration can AddComponent (FindOrCreatePageCompositor),
            // which Unity disallows inside OnValidate ("SendMessage cannot be called during
            // OnValidate") — defer it to the next editor tick, guarding against the
            // component being destroyed or disabled in between.
            UnityEditor.EditorApplication.delayCall += () =>
            {
                if (this == null || !isActiveAndEnabled) return;
                UpdatePageCompositorRegistration();
                MarkPageCompositorDirty();
            };
            MarkPageCompositorDirty();
            base.OnValidate();
            SetVerticesDirty();
            SetMaterialDirty();
        }
#endif

        static void DestroyRuntimeMaterial(Material material)
        {
            if (material == null) return;
            if (Application.isPlaying) Destroy(material);
            else DestroyImmediate(material);
        }

        static void Hash(ref uint hash, FigForgeFill paint)
        {
            Hash(ref hash, paint.disabled ? 1 : 0);
            Hash(ref hash, (int)paint.kind);
            Hash(ref hash, (int)paint.gradientKind);
            Hash(ref hash, paint.color);
            Hash(ref hash, paint.dir.x);
            Hash(ref hash, paint.dir.y);
            if (paint.gradient == null) return;
            // Gradient.colorKeys/alphaKeys allocate a fresh array on every access and
            // this runs per canvas update (CheckCachedSurfaceValidity) — sample the
            // gradient at fixed stops instead; Evaluate is allocation-free.
            for (int i = 0; i <= 8; i++)
                Hash(ref hash, paint.gradient.Evaluate(i / 8f));
        }

        static void Hash(ref uint hash, FigForgeStrokeLayer stroke)
        {
            Hash(ref hash, stroke.enabled ? 1 : 0);
            Hash(ref hash, (int)stroke.style);
            Hash(ref hash, stroke.weight);
            Hash(ref hash, (int)stroke.align);
            Hash(ref hash, stroke.dash);
            Hash(ref hash, stroke.gap);
            Hash(ref hash, stroke.paint);
        }

        static void Hash(ref uint hash, FigForgeEffectLayer effect)
        {
            Hash(ref hash, effect.enabled ? 1 : 0);
            Hash(ref hash, (int)effect.kind);
            Hash(ref hash, effect.color);
            Hash(ref hash, effect.offset.x);
            Hash(ref hash, effect.offset.y);
            Hash(ref hash, effect.blur);
            Hash(ref hash, effect.spread);
            Hash(ref hash, (int)effect.blurMode);
            Hash(ref hash, effect.endBlur);
            Hash(ref hash, effect.showBehindShape ? 1 : 0);
        }

        static void Hash(ref uint hash, Vector4 value)
        {
            Hash(ref hash, value.x);
            Hash(ref hash, value.y);
            Hash(ref hash, value.z);
            Hash(ref hash, value.w);
        }

        static void Hash(ref uint hash, Color value)
        {
            Hash(ref hash, value.r);
            Hash(ref hash, value.g);
            Hash(ref hash, value.b);
            Hash(ref hash, value.a);
        }

        static void Hash(ref uint hash, float value)
        {
            Hash(ref hash, Mathf.RoundToInt(value * 10000f));
        }

        static void Hash(ref uint hash, int value)
        {
            unchecked
            {
                hash ^= (uint)value;
                hash *= 16777619u;
            }
        }
    }

    static class FigForgePaintTextureCache
    {
        const int Res = 256;
        const int Rows = 8;
        const int StrokeRowOffset = 4;
        static readonly Dictionary<string, Texture2D> _mem = new Dictionary<string, Texture2D>();

        public static Texture2D Get(List<FigForgeFill> fills, List<FigForgeStrokeLayer> strokes)
        {
            string key = Key(fills, strokes);
            if (_mem.TryGetValue(key, out var cached) && cached != null) return cached;

            var tex = new Texture2D(Res, Rows, TextureFormat.RGBA32, false, true)
            {
                name = "FigForgePaintStack_" + key,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave,
            };
            var px = new Color[Res * Rows];
            for (int row = 0; row < Rows; row++)
            {
                for (int x = 0; x < Res; x++)
                {
                    Color c = Color.clear;
                    if (row < StrokeRowOffset)
                    {
                        int idx = VisibleFillIndex(fills, row);
                        if (idx >= 0)
                        {
                            var paint = fills[idx];
                            c = paint.HasGradient ? paint.gradient.Evaluate((float)x / (Res - 1)) : paint.color;
                        }
                    }
                    else
                    {
                        int idx = VisibleStrokeIndex(strokes, row - StrokeRowOffset);
                        if (idx >= 0)
                        {
                            var paint = strokes[idx].paint;
                            c = paint.HasGradient ? paint.gradient.Evaluate((float)x / (Res - 1)) : paint.color;
                        }
                    }
                    px[row * Res + x] = c;
                }
            }
            tex.SetPixels(px);
            tex.Apply(false, true);
            _mem[key] = tex;
            return tex;
        }

        static int VisibleFillIndex(List<FigForgeFill> fills, int visibleIndex)
        {
            int count = 0;
            for (int i = 0; i < fills.Count; i++)
            {
                if (!VisibleFill(fills[i])) continue;
                if (count == visibleIndex) return i;
                count++;
            }
            return -1;
        }

        static bool VisibleFill(FigForgeFill fill)
        {
            if (fill.disabled) return false;
            if (fill.kind == FigForgeFillKind.Solid) return fill.color.a > 0.001f;
            if (fill.gradient == null) return fill.color.a > 0.001f;
            var alphas = fill.gradient.alphaKeys;
            if (alphas == null || alphas.Length == 0) return fill.color.a > 0.001f;
            for (int i = 0; i < alphas.Length; i++)
                if (alphas[i].alpha > 0.001f) return true;
            return false;
        }

        static int VisibleStrokeIndex(List<FigForgeStrokeLayer> strokes, int visibleIndex)
        {
            int count = 0;
            for (int i = 0; i < strokes.Count; i++)
            {
                if (!strokes[i].enabled) continue;
                if (count == visibleIndex) return i;
                count++;
            }
            return -1;
        }

        static string Key(List<FigForgeFill> fills, List<FigForgeStrokeLayer> strokes)
        {
            unchecked
            {
                uint hash = 2166136261u;
                int visibleFills = 0;
                for (int i = 0; i < fills.Count && visibleFills < StrokeRowOffset; i++)
                {
                    if (!VisibleFill(fills[i])) continue;
                    Hash(ref hash, fills[i]);
                    visibleFills++;
                }
                int visibleStrokes = 0;
                for (int i = 0; i < strokes.Count && visibleStrokes < StrokeRowOffset; i++)
                {
                    if (!strokes[i].enabled) continue;
                    Hash(ref hash, strokes[i].weight);
                    Hash(ref hash, (int)strokes[i].align);
                    Hash(ref hash, strokes[i].paint);
                    visibleStrokes++;
                }
                return hash.ToString("X8");
            }
        }

        static void Hash(ref uint hash, FigForgeFill paint)
        {
            Hash(ref hash, (int)paint.kind);
            Hash(ref hash, (int)paint.gradientKind);
            Hash(ref hash, paint.color);
            Hash(ref hash, paint.dir.x);
            Hash(ref hash, paint.dir.y);
            if (paint.gradient != null)
            {
                var colors = paint.gradient.colorKeys;
                for (int c = 0; c < colors.Length; c++)
                {
                    Hash(ref hash, colors[c].time);
                    Hash(ref hash, colors[c].color);
                }
                var alphas = paint.gradient.alphaKeys;
                for (int a = 0; a < alphas.Length; a++)
                {
                    Hash(ref hash, alphas[a].time);
                    Hash(ref hash, alphas[a].alpha);
                }
            }
        }

        static void Hash(ref uint hash, Color c)
        {
            Hash(ref hash, c.r);
            Hash(ref hash, c.g);
            Hash(ref hash, c.b);
            Hash(ref hash, c.a);
        }

        static void Hash(ref uint hash, float v)
        {
            Hash(ref hash, Mathf.RoundToInt(v * 10000f));
        }

        static void Hash(ref uint hash, int v)
        {
            unchecked
            {
                hash ^= (uint)v;
                hash *= 16777619u;
            }
        }
    }
}
