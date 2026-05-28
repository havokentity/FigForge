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
        [SerializeField] float cornerRadius = 0f;            // px

        Material _mat;
        static readonly int IdFill = Shader.PropertyToID("_FillColor");
        static readonly int IdFill2 = Shader.PropertyToID("_Fill2");
        static readonly int IdBorder = Shader.PropertyToID("_BorderColor");
        static readonly int IdBorderW = Shader.PropertyToID("_BorderWidth");
        static readonly int IdRadius = Shader.PropertyToID("_Radius");
        static readonly int IdSize = Shader.PropertyToID("_Size");
        static readonly int IdGrad = Shader.PropertyToID("_GradientDir");

        public Color FillColor { get => fillColor; set { fillColor = value; Push(); } }

        public void Configure(Color fill, Color fill2, Vector2 grad, Color border, float borderW, float radius)
        {
            fillColor = fill; fillColor2 = fill2; gradientDir = grad;
            borderColor = border; borderWidth = borderW; cornerRadius = radius;
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

        void Push()
        {
            var m = EnsureMat();
            if (m == null) return;
            var r = rectTransform.rect;
            m.SetColor(IdFill, fillColor);
            m.SetColor(IdFill2, fillColor2);
            m.SetColor(IdBorder, borderColor);
            m.SetFloat(IdBorderW, borderWidth);
            m.SetFloat(IdRadius, cornerRadius);
            m.SetVector(IdSize, new Vector4(r.width, r.height, 0, 0));
            m.SetVector(IdGrad, new Vector4(gradientDir.x, gradientDir.y, 0, 0));
            SetMaterialDirty();
        }

        // White vertices — the shader supplies fill/border/gradient; vertex color
        // is only the CanvasRenderer tint (kept opaque white).
        protected override void OnPopulateMesh(VertexHelper vh)
        {
            var r = GetPixelAdjustedRect();
            var c = Color.white;
            vh.Clear();
            vh.AddVert(new Vector3(r.x, r.y), c, new Vector2(0, 0));
            vh.AddVert(new Vector3(r.x, r.yMax), c, new Vector2(0, 1));
            vh.AddVert(new Vector3(r.xMax, r.yMax), c, new Vector2(1, 1));
            vh.AddVert(new Vector3(r.xMax, r.y), c, new Vector2(1, 0));
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
