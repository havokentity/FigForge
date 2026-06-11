// =============================================================================
// FigForge — procedural arc/donut UI graphic for the ring/gauge Progress
// styles. Builds a triangle-strip arc mesh between an inner and outer radius:
// a closed ring (span 360), an open gauge arc (span < 360), or a filled pie
// wedge (thickness 1). Vertex-coloured (Graphic.color) over the shared white
// UI texture, so rings batch like any plain Image.
//
// Angle convention: degrees from 12 o'clock, CLOCKWISE (matches the Figma arc
// capture in the exporter). `Sweep01`/`Offset01` are fractions of `SpanAngle`,
// which is what FigForgeProgress drives — the value fill sets Sweep01, the
// indeterminate spinner cycles Offset01.
// =============================================================================

using UnityEngine;
using UnityEngine.UI;

namespace FigForge
{
    [AddComponentMenu("FigForge/Ring")]
    [DisallowMultipleComponent]
    public class FigForgeRing : MaskableGraphic
    {
        [Tooltip("Arc start, in degrees from 12 o'clock going clockwise (e.g. a 270° gauge with the gap at the bottom starts at 225).")]
        [SerializeField] float m_StartAngle;

        [Tooltip("Total arc span in degrees (360 = closed ring, 270 = speedometer gauge).")]
        [SerializeField, Range(1f, 360f)] float m_SpanAngle = 360f;

        [Tooltip("Stroke thickness as a fraction of the outer radius (1 = filled pie wedge, no hole).")]
        [SerializeField, Range(0.01f, 1f)] float m_Thickness = 0.25f;

        [Tooltip("Filled fraction of the span (the progress value for a fill ring; 1 for a track ring).")]
        [SerializeField, Range(0f, 1f)] float m_Sweep01 = 1f;

        [Tooltip("Start offset of the filled arc within the span, as a fraction (the indeterminate spinner cycles this).")]
        [SerializeField, Range(0f, 1f)] float m_Offset01;

        public float StartAngle { get => m_StartAngle; set { m_StartAngle = value; SetVerticesDirty(); } }
        public float SpanAngle { get => m_SpanAngle; set { m_SpanAngle = Mathf.Clamp(value, 1f, 360f); SetVerticesDirty(); } }
        public float Thickness { get => m_Thickness; set { m_Thickness = Mathf.Clamp(value, 0.01f, 1f); SetVerticesDirty(); } }
        public float Sweep01 { get => m_Sweep01; set { m_Sweep01 = Mathf.Clamp01(value); SetVerticesDirty(); } }
        public float Offset01 { get => m_Offset01; set { m_Offset01 = Mathf.Clamp01(value); SetVerticesDirty(); } }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            float sweepDeg = m_Sweep01 * m_SpanAngle;
            if (sweepDeg <= 0.01f) return;

            var r = GetPixelAdjustedRect();
            Vector2 c = r.center;
            float outer = Mathf.Min(r.width, r.height) * 0.5f;
            if (outer <= 0f) return;
            float inner = outer * (1f - m_Thickness);
            float a0 = m_StartAngle + m_Offset01 * m_SpanAngle;
            // ~4° per step keeps the silhouette round at dial sizes without
            // ballooning the vert count (90 steps max for a full circle).
            int steps = Mathf.Max(2, Mathf.CeilToInt(sweepDeg / 4f));
            Color32 col = color;

            var v = UIVertex.simpleVert;
            v.color = col;
            for (int i = 0; i <= steps; i++)
            {
                // Visual angle from 12 o'clock clockwise → UI space (y up).
                float a = (a0 + sweepDeg * i / steps) * Mathf.Deg2Rad;
                var dir = new Vector2(Mathf.Sin(a), Mathf.Cos(a));
                v.position = c + dir * outer;
                vh.AddVert(v);
                v.position = c + dir * inner;
                vh.AddVert(v);
                if (i > 0)
                {
                    int b = (i - 1) * 2;
                    vh.AddTriangle(b, b + 2, b + 3);
                    vh.AddTriangle(b, b + 3, b + 1);
                }
            }
        }
    }
}
