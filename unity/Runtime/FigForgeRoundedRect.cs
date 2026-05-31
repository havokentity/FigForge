// =============================================================================
// FigForge — procedural rounded-rect UI graphic. Drives the FigForge/RoundedRect
// SDF shader from serialized params (corner radius, fill, optional gradient,
// border), so a button background is razor-crisp at any size and matches the
// Figma vector — no exported PNG, no 9-slice. Per-instance material; params and
// rect size are pushed to the shader. FigForgeButtonStateColors swaps the fill.
// =============================================================================

using UnityEngine;
using UnityEngine.UI;

namespace FigForge
{
    // One button-state fill: a solid colour OR a 2-stop linear gradient, in a single
    // serialized field. Replaces the old colour/colour2/dir triple so the inspector
    // shows one "Normal / Highlighted / Pressed" entry instead of nine loose fields.
    [System.Serializable]
    public struct FigForgeFill
    {
        public Color color;    // solid, or gradient stop 0
        public Color color2;   // gradient stop 1
        public Vector2 dir;    // gradient direction; (0,0) = solid
        public FigForgeFill(Color c, Color c2, Vector2 d) { color = c; color2 = c2; dir = d; }
        public static FigForgeFill Solid(Color c) => new FigForgeFill(c, c, Vector2.zero);
        public static FigForgeFill Gradient(Color a, Color b, Vector2 d) => new FigForgeFill(a, b, d);
    }

    [System.Serializable]
    public struct FigForgeShapeStyle
    {
        public FigForgeFill fill;
        public Color borderColor;
        public float borderWidth;
        public Vector4 corners;
        public float borderAlign;
        public Color shadowColor;
        public Vector2 shadowOffset;
        public float shadowBlur;
        public float shadowSpread;

        public FigForgeShapeStyle WithFill(FigForgeFill f)
        {
            fill = f;
            return this;
        }
    }

    [AddComponentMenu("FigForge/Rounded Rect")]
    public class FigForgeRoundedRect : MaskableGraphic
    {
        [SerializeField] Color fillColor = Color.white;
        [SerializeField] Color fillColor2 = Color.white;     // gradient end
        [SerializeField] Vector2 gradientDir = Vector2.zero; // (0,0) = solid
        [SerializeField] Color borderColor = new Color(0, 0, 0, 0);
        [SerializeField] float borderWidth = 0f;             // px
        [SerializeField] float borderAlign = 0f;             // 0=inside, 0.5=center, 1=outside
        [SerializeField] Vector4 corners = Vector4.zero;     // per-corner radii px: (tl, tr, br, bl)
        [SerializeField] Color shadowColor = new Color(0, 0, 0, 0); // drop shadow (a==0 → off)
        [SerializeField] Vector2 shadowOffset = Vector2.zero;        // px, Unity space (+y up)
        [SerializeField] float shadowBlur = 0f;              // px
        [SerializeField] float shadowSpread = 0f;            // px

        Material _mat;
        static readonly int IdFill = Shader.PropertyToID("_FillColor");
        static readonly int IdFill2 = Shader.PropertyToID("_Fill2");
        static readonly int IdBorder = Shader.PropertyToID("_BorderColor");
        static readonly int IdBorderW = Shader.PropertyToID("_BorderWidth");
        static readonly int IdRadius = Shader.PropertyToID("_Radius");
        static readonly int IdSize = Shader.PropertyToID("_Size");
        static readonly int IdGrad = Shader.PropertyToID("_GradientDir");
        static readonly int IdPad = Shader.PropertyToID("_Pad");
        static readonly int IdStrokeOutset = Shader.PropertyToID("_StrokeOutset");
        static readonly int IdShadowColor = Shader.PropertyToID("_ShadowColor");
        static readonly int IdShadowOffset = Shader.PropertyToID("_ShadowOffset");
        static readonly int IdShadowParams = Shader.PropertyToID("_ShadowParams");

        // How far the stroke extends OUTSIDE the fill edge (scaled px).
        float StrokeOutset() => Mathf.Max(0f, borderWidth) * Mathf.Clamp01(borderAlign);
        // How far the drop shadow reaches beyond the fill edge (offset + spread + the
        // Gaussian tail + margin). The shader uses sigma = blur/2 and the tail is
        // negligible past ~3·sigma = 1.5·blur, so pad by 1.75·blur to avoid clipping it.
        float ShadowReach() => shadowColor.a <= 0.001f ? 0f
            : Mathf.Max(Mathf.Abs(shadowOffset.x), Mathf.Abs(shadowOffset.y))
              + 1.75f * Mathf.Max(0f, shadowBlur) + Mathf.Max(0f, shadowSpread) + 1f;
        // Total mesh padding so stroke AND shadow have geometry to draw on.
        float MeshPad() => Mathf.Max(StrokeOutset(), ShadowReach());

        public Color FillColor { get => fillColor; set { fillColor = value; Push(); } }

        // Swap the whole fill at runtime (used by FigForgeButtonStateColors for
        // per-state colours). Setting fill2==fill and grad=(0,0) renders solid;
        // a real second colour + direction renders the gradient — so a gradient
        // button can show its gradient at rest and a solid colour on hover/press.
        public void SetFill(Color fill, Color fill2, Vector2 grad)
        {
            fillColor = fill; fillColor2 = fill2; gradientDir = grad; Push();
        }

        // Swap the whole fill from a single FigForgeFill (solid or gradient).
        public void SetFill(FigForgeFill f) => SetFill(f.color, f.color2, f.dir);

        public void Configure(Color fill, Color fill2, Vector2 grad, Color border, float borderW, Vector4 cornerRadii, float borderAlignment = 0f)
        {
            SetShapeFields(fill, fill2, grad, border, borderW, cornerRadii, borderAlignment);
            Push();
        }

        public void SetStyle(FigForgeShapeStyle style)
        {
            SetShapeFields(style.fill.color, style.fill.color2, style.fill.dir,
                style.borderColor, style.borderWidth, style.corners, style.borderAlign);
            SetShadowFields(style.shadowColor, style.shadowOffset, style.shadowBlur, style.shadowSpread);
            SetVerticesDirty(); // mesh padding may have changed
            Push();
        }

        // Drop shadow behind the shape. color.a==0 → no shadow. offset is Unity-space
        // px (+y up); blur/spread in px. The mesh auto-grows to fit the shadow.
        public void SetShadow(Color color, Vector2 offset, float blur, float spread)
        {
            SetShadowFields(color, offset, blur, spread);
            SetVerticesDirty(); // mesh padding may have changed
            Push();
        }

        void SetShapeFields(Color fill, Color fill2, Vector2 grad, Color border, float borderW, Vector4 cornerRadii, float borderAlignment)
        {
            fillColor = fill; fillColor2 = fill2; gradientDir = grad;
            borderColor = border; borderWidth = borderW; corners = cornerRadii;
            borderAlign = borderAlignment;
        }

        void SetShadowFields(Color color, Vector2 offset, float blur, float spread)
        {
            shadowColor = color; shadowOffset = offset; shadowBlur = blur; shadowSpread = spread;
        }

        Material EnsureMat()
        {
            if (_mat == null)
            {
                var sh = Shader.Find("FigForge/RoundedRect");
                if (sh != null) _mat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
            }
            return _mat;
        }

        public override Material material { get => EnsureMat() ?? base.material; set { } }

        // Write our SDF params onto a material. Factored out so we can re-apply them
        // to the mask-modified material too (see GetModifiedMaterial).
        void ApplyParams(Material m)
        {
            if (m == null) return;
            var r = rectTransform.rect;
            m.SetColor(IdFill, fillColor);
            m.SetColor(IdFill2, fillColor2);
            m.SetColor(IdBorder, borderColor);
            m.SetFloat(IdBorderW, borderWidth);
            m.SetVector(IdRadius, corners);
            m.SetVector(IdSize, new Vector4(r.width, r.height, 0, 0));
            m.SetVector(IdGrad, new Vector4(gradientDir.x, gradientDir.y, 0, 0));
            m.SetFloat(IdStrokeOutset, StrokeOutset());
            m.SetFloat(IdPad, MeshPad());
            m.SetColor(IdShadowColor, shadowColor);
            m.SetVector(IdShadowOffset, new Vector4(shadowOffset.x, shadowOffset.y, 0, 0));
            m.SetVector(IdShadowParams, new Vector4(shadowBlur, shadowSpread, 0, 0));
        }

        void Push()
        {
            var m = EnsureMat();
            if (m == null) return;
            ApplyParams(m);
            // Belt-and-suspenders for MASKED graphics: the UGUI Mask renders us through
            // a cached StencilMaterial COPY of _mat, not _mat itself. GetModifiedMaterial
            // re-applies our params to that copy, but only on the next canvas rebuild —
            // a per-frame pointer enter/exit can revert the swap before that runs. So also
            // push directly onto the live copy now (same shader → it's our stencil copy),
            // making a hover/press fill swap take effect immediately under a mask.
            var cr = canvasRenderer;
            var live = cr != null ? cr.GetMaterial() : null;
            if (live != null && live != m && live.shader == m.shader) ApplyParams(live);
            SetMaterialDirty();
        }

        // When this graphic is inside a UGUI Mask, the mask wraps our per-instance
        // material in a StencilMaterial COPY that is cached by base-material reference
        // — so later uniform changes (a hover/press fill swap, or a resize) never reach
        // the actually-rendered copy and nothing updates on screen. SetMaterialDirty
        // re-runs this each time, so re-apply our params to whatever material the mask
        // hands back, keeping the masked copy in sync with fillColor/gradient/size.
        public override Material GetModifiedMaterial(Material baseMaterial)
        {
            var m = base.GetModifiedMaterial(baseMaterial);
            if (m != null && m != baseMaterial) ApplyParams(m);
            return m;
        }

        // White vertices — the shader supplies fill/border/gradient; vertex color
        // is only the CanvasRenderer tint (kept opaque white).
        protected override void OnPopulateMesh(VertexHelper vh)
        {
            var r = GetPixelAdjustedRect();
            // Pad the quad by the stroke outset AND shadow reach so both have geometry
            // to draw on (the shader maps UV across this padded span and keeps the SDF
            // box at the rect size). UV stays 0..1.
            float pad = MeshPad();
            float x0 = r.x - pad, y0 = r.y - pad, x1 = r.xMax + pad, y1 = r.yMax + pad;
            var c = Color.white;
            vh.Clear();
            vh.AddVert(new Vector3(x0, y0), c, new Vector2(0, 0));
            vh.AddVert(new Vector3(x0, y1), c, new Vector2(0, 1));
            vh.AddVert(new Vector3(x1, y1), c, new Vector2(1, 1));
            vh.AddVert(new Vector3(x1, y0), c, new Vector2(1, 0));
            vh.AddTriangle(0, 1, 2);
            vh.AddTriangle(2, 3, 0);
        }

        protected override void OnEnable() { base.OnEnable(); Push(); }
        protected override void OnRectTransformDimensionsChange() { base.OnRectTransformDimensionsChange(); Push(); }
#if UNITY_EDITOR
        protected override void OnValidate() { base.OnValidate(); Push(); }
#endif
        protected override void OnDestroy() { if (_mat != null) DestroyImmediate(_mat); base.OnDestroy(); }
    }
}
