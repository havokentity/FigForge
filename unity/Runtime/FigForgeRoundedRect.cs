// =============================================================================
// FigForge — procedural rounded-rect UI graphic. Drives the FigForge/RoundedRect
// SDF shader from serialized params (corner radius, fill, optional gradient,
// border), so a button background is razor-crisp at any size and matches the
// Figma vector — no exported PNG, no 9-slice. All instances share one material;
// per-rect params are encoded into the quad vertices so uGUI can batch them.
// FigForgeButtonStateColors swaps the fill.
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

        static Material _sharedMat;
        const AdditionalCanvasShaderChannels SdfChannels =
            AdditionalCanvasShaderChannels.TexCoord1 |
            AdditionalCanvasShaderChannels.TexCoord2 |
            AdditionalCanvasShaderChannels.TexCoord3 |
            AdditionalCanvasShaderChannels.Normal |
            AdditionalCanvasShaderChannels.Tangent;

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

        static Material EnsureMat()
        {
            if (_sharedMat == null)
            {
                var sh = Shader.Find("FigForge/RoundedRect");
                if (sh != null) _sharedMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
            }
            return _sharedMat;
        }

        public override Material material { get => EnsureMat() ?? base.material; set { } }

        void EnsureCanvasChannels()
        {
            var c = canvas;
            if (c != null && (c.additionalShaderChannels & SdfChannels) != SdfChannels)
                c.additionalShaderChannels |= SdfChannels;
        }

        void Push()
        {
            EnsureMat();
            EnsureCanvasChannels();
            SetVerticesDirty();
        }

        // The shader supplies fill/border/gradient; vertex color is reserved for
        // Graphic.color / CanvasRenderer tint.
        protected override void OnPopulateMesh(VertexHelper vh)
        {
            var r = GetPixelAdjustedRect();
            // Pad the quad by the stroke outset AND shadow reach so both have geometry
            // to draw on (the shader maps UV across this padded span and keeps the SDF
            // box at the rect size). UV stays 0..1.
            float pad = MeshPad();
            float x0 = r.x - pad, y0 = r.y - pad, x1 = r.xMax + pad, y1 = r.yMax + pad;
            var c = color;
            var fill = PackColor(fillColor);
            var fill2 = PackColor(fillColor2);
            var border = PackColor(borderColor);
            var shadow = PackColor(shadowColor);
            float grad = PackSignedUnitPair(gradientDir);
            var uv0 = new Vector4(0, 0, fill.x, fill.y);
            var uv1 = new Vector4(fill2.x, fill2.y, border.x, border.y);
            var uv2 = new Vector4(shadow.x, shadow.y, r.width, r.height);
            var uv3 = new Vector4(grad, corners.x, corners.y, corners.z);
            var n = new Vector3(corners.w, borderWidth, StrokeOutset());
            var t = new Vector4(shadowOffset.x, shadowOffset.y, shadowBlur, shadowSpread);
            vh.Clear();
            AddVert(vh, new Vector3(x0, y0), c, uv0, uv1, uv2, uv3, n, t);
            uv0.y = 1f; AddVert(vh, new Vector3(x0, y1), c, uv0, uv1, uv2, uv3, n, t);
            uv0.x = 1f; AddVert(vh, new Vector3(x1, y1), c, uv0, uv1, uv2, uv3, n, t);
            uv0.y = 0f; AddVert(vh, new Vector3(x1, y0), c, uv0, uv1, uv2, uv3, n, t);
            vh.AddTriangle(0, 1, 2);
            vh.AddTriangle(2, 3, 0);
        }

        static void AddVert(VertexHelper vh, Vector3 pos, Color32 color, Vector4 uv0, Vector4 uv1, Vector4 uv2, Vector4 uv3, Vector3 normal, Vector4 tangent)
        {
            var v = UIVertex.simpleVert;
            v.position = pos;
            v.color = color;
            v.uv0 = uv0;
            v.uv1 = uv1;
            v.uv2 = uv2;
            v.uv3 = uv3;
            v.normal = normal;
            v.tangent = tangent;
            vh.AddVert(v);
        }

        static Vector2 PackColor(Color c)
            => new Vector2(PackBytes(c.r, c.g), PackBytes(c.b, c.a));

        static float PackBytes(float a, float b)
        {
            int ai = Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(a) * 255f), 0, 255);
            int bi = Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(b) * 255f), 0, 255);
            return (ai * 256 + bi) / 65535f;
        }

        static float PackSignedUnitPair(Vector2 v)
        {
            if (v.sqrMagnitude <= 1e-8f) return 0f;
            v = Vector2.ClampMagnitude(v, 1f);
            int x = Mathf.Clamp(Mathf.RoundToInt((v.x * 0.5f + 0.5f) * 255f), 0, 255);
            int y = Mathf.Clamp(Mathf.RoundToInt((v.y * 0.5f + 0.5f) * 255f), 0, 255);
            return (x * 256 + y + 1) / 65536f;
        }

        protected override void OnEnable() { base.OnEnable(); Push(); }
        protected override void OnCanvasHierarchyChanged() { base.OnCanvasHierarchyChanged(); EnsureCanvasChannels(); }
        protected override void OnRectTransformDimensionsChange() { base.OnRectTransformDimensionsChange(); Push(); }
#if UNITY_EDITOR
        protected override void OnValidate() { base.OnValidate(); Push(); }
#endif
    }
}
