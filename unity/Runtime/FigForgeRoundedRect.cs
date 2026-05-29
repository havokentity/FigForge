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

        Material _mat;
        static readonly int IdFill = Shader.PropertyToID("_FillColor");
        static readonly int IdFill2 = Shader.PropertyToID("_Fill2");
        static readonly int IdBorder = Shader.PropertyToID("_BorderColor");
        static readonly int IdBorderW = Shader.PropertyToID("_BorderWidth");
        static readonly int IdRadius = Shader.PropertyToID("_Radius");
        static readonly int IdSize = Shader.PropertyToID("_Size");
        static readonly int IdGrad = Shader.PropertyToID("_GradientDir");
        static readonly int IdPad = Shader.PropertyToID("_Pad");

        // How far the stroke extends OUTSIDE the fill edge (scaled px). Drives both
        // the shader (_Pad) and the mesh padding so an outside/center stroke isn't clipped.
        float StrokeOutset() => Mathf.Max(0f, borderWidth) * Mathf.Clamp01(borderAlign);

        public Color FillColor { get => fillColor; set { fillColor = value; Push(); } }

        // Swap the whole fill at runtime (used by FigForgeButtonStateColors for
        // per-state colours). Setting fill2==fill and grad=(0,0) renders solid;
        // a real second colour + direction renders the gradient — so a gradient
        // button can show its gradient at rest and a solid colour on hover/press.
        public void SetFill(Color fill, Color fill2, Vector2 grad)
        {
            fillColor = fill; fillColor2 = fill2; gradientDir = grad; Push();
        }

        public void Configure(Color fill, Color fill2, Vector2 grad, Color border, float borderW, Vector4 cornerRadii, float borderAlignment = 0f)
        {
            fillColor = fill; fillColor2 = fill2; gradientDir = grad;
            borderColor = border; borderWidth = borderW; corners = cornerRadii;
            borderAlign = borderAlignment;
            Push();
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
            m.SetFloat(IdPad, StrokeOutset());
        }

        void Push()
        {
            var m = EnsureMat();
            if (m == null) return;
            ApplyParams(m);
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
            // Pad the quad by the stroke outset so an outside/center stroke has
            // geometry to draw on (the shader maps UV across this padded span and
            // keeps the SDF box at the rect size). UV stays 0..1.
            float pad = StrokeOutset();
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
