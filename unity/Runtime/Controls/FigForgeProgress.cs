// =============================================================================
// FigForge — Progress bar control. A slider with no thumb and no input, in
// three visual styles (wired by the importer from the Figma master's layers):
//   Bar      — Track rail + value-driven Fill (anchorMax.x tracks the value)
//   Ring     — FigForgeRing arc whose sweep tracks the value (a 270° span makes
//              it a gauge; thickness 1 makes it a pie wedge)
//   Segments — N lit/unlit block pairs; the first round(value·N) light up
// plus typed references to its label and live percentage read-out. The value
// range is 0..1 by default (the Figma master's fill ratio); re-range through
// code via the minValue/maxValue properties — the read-out always shows the
// value as a percentage of the range.
//
// Indeterminate mode (the Figma master carries a hidden 'Indeterminate'
// layer): in play mode the bar sweeps a segment, the ring spins (closed ring)
// or bounces (open gauge) an arc, and the segments march a lit window — for
// work of unknown duration. `value` is kept but not rendered while
// indeterminate; setting Indeterminate=false restores the value-driven fill.
// In edit mode the fill always shows the value, so scenes and prefabs stay
// stable to author against.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.Events;

namespace FigForge
{
    [AddComponentMenu("FigForge/Controls/Progress Bar")]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(RectTransform))]
    [ExecuteAlways]
    public class FigForgeProgress : MonoBehaviour
    {
        [Serializable] public class ProgressEvent : UnityEvent<float> { }

        public enum Style { Bar, Ring, Segments }

        [Tooltip("Visual family — which of the references below drives the fill.")]
        [SerializeField] Style m_Style = Style.Bar;

        [Tooltip("Bar style: the fill RectTransform (wired by the importer). Its anchorMax.x tracks the value; in indeterminate play mode both x anchors sweep.")]
        public RectTransform fillRect;

        [Tooltip("Ring style: the fill arc (wired by the importer). Its Sweep01 tracks the value; the indeterminate spinner cycles its Offset01.")]
        public FigForgeRing fillRing;

        [Tooltip("Segments style: the lit overlay of each block, left to right (wired by the importer). The first round(value·N) are activated; indeterminate marches a lit window.")]
        public List<GameObject> segmentFills = new List<GameObject>();

        [Tooltip("The progress bar's text label (wired by the importer). May be null.")]
        public TMP_Text tmpTxt_label;

        [Tooltip("Live percentage read-out (wired by the importer when the Figma component has a 'Value' layer). Rewritten on every value change; blank while indeterminate. May be null.")]
        public TMP_Text tmpTxt_value;

        [SerializeField] float m_MinValue;
        [SerializeField] float m_MaxValue = 1f;
        [SerializeField] float m_Value;

        [Tooltip("Animated sweep instead of a value-driven fill (work of unknown duration). Animates in play mode only; the editor shows the value fill.")]
        [SerializeField] bool m_Indeterminate;

        [Tooltip("Indeterminate sweep segment width, as a fraction of the track.")]
        [SerializeField, Range(0.05f, 0.95f)] float m_IndeterminateWidth = 0.3f;

        [Tooltip("Indeterminate sweeps per second.")]
        [SerializeField] float m_IndeterminateSpeed = 0.75f;

        [Tooltip("Decimal places the percentage read-out shows (0 = whole numbers). Display-only — the value itself stays a float.")]
        [SerializeField] int m_ValueTextDecimals; // default 0 — integer percent ('42%')

        [Tooltip("Invoked with the new value whenever it changes.")]
        public ProgressEvent onValueChanged = new ProgressEvent();

        /// <summary>Range start — `bar.minValue = 0`. Re-clamps the current value
        /// (onValueChanged fires if it moves) and repaints the fill.</summary>
        public float minValue { get => m_MinValue; set { m_MinValue = value; Set(m_Value); } }

        /// <summary>Range end — `bar.maxValue = 100`. Re-clamps the current value
        /// (onValueChanged fires if it moves) and repaints the fill.</summary>
        public float maxValue { get => m_MaxValue; set { m_MaxValue = value; Set(m_Value); } }

        /// <summary>The current progress — `bar.value = 0.75f`. Clamped to
        /// [minValue..maxValue]; drives the fill width and the percentage read-out.</summary>
        public float value { get => m_Value; set => Set(value); }

        /// <summary>Set the value without invoking onValueChanged.</summary>
        public void SetValueWithoutNotify(float input) => Set(input, false);

        /// <summary>Animated sweep mode — `bar.Indeterminate = true` while the duration
        /// is unknown, back to false (the fill snaps to `value`) once it's measurable.</summary>
        public bool Indeterminate
        {
            get => m_Indeterminate;
            set { m_Indeterminate = value; ApplyFill(); UpdateValueText(); }
        }

        /// <summary>Decimal places of the percentage read-out — `bar.ValueTextDecimals = 1`
        /// → "42.5%". Default 0: whole percent. Display-only rounding.</summary>
        public int ValueTextDecimals
        {
            get => m_ValueTextDecimals;
            set { m_ValueTextDecimals = Mathf.Clamp(value, 0, 7); UpdateValueText(); }
        }

        /// <summary>The label's text — `bar.Label = "Uploading…"`. No-op if there's no label.
        /// (Use the `tmpTxt_label` TMP_Text for font/colour/advanced styling.)</summary>
        public string Label
        {
            get => tmpTxt_label != null ? tmpTxt_label.text : null;
            set { if (tmpTxt_label != null) tmpTxt_label.text = value; }
        }

        /// <summary>Show/hide the whole control — `bar.isVisible = false`. Drives
        /// GameObject.SetActive, so a hidden control stops rendering and contributing
        /// to layout.</summary>
        public bool isVisible
        {
            get => gameObject.activeSelf;
            set => gameObject.SetActive(value);
        }

        /// <summary>The visual family (Bar / Ring / Segments) — set by the importer to
        /// match the Figma master; switch only if you also rewire the matching fill
        /// reference (fillRect / fillRing / segmentFills).</summary>
        public Style VisualStyle
        {
            get => m_Style;
            set { m_Style = value; ApplyFill(); }
        }

        float Ratio => m_MaxValue > m_MinValue ? Mathf.InverseLerp(m_MinValue, m_MaxValue, m_Value) : 0f;

        void Set(float input, bool sendCallback = true)
        {
            float clamped = m_MaxValue > m_MinValue ? Mathf.Clamp(input, m_MinValue, m_MaxValue) : m_MinValue;
            bool changed = !Mathf.Approximately(clamped, m_Value);
            m_Value = clamped;
            ApplyFill();
            UpdateValueText();
            if (changed && sendCallback) onValueChanged.Invoke(m_Value);
        }

        void OnEnable()
        {
            ApplyFill();
            UpdateValueText();
        }

#if UNITY_EDITOR
        void OnValidate()
        {
            m_Value = m_MaxValue > m_MinValue ? Mathf.Clamp(m_Value, m_MinValue, m_MaxValue) : m_MinValue;
            ApplyFill();
            UpdateValueText();
        }
#endif

        void Update()
        {
            // Indeterminate animates only in play mode; everywhere else the fill shows the value.
            if (!Application.isPlaying || !m_Indeterminate) return;
            float w = Mathf.Clamp(m_IndeterminateWidth, 0.05f, 0.95f);
            float t = Mathf.Repeat(Time.unscaledTime * Mathf.Max(0.01f, m_IndeterminateSpeed), 1f);
            switch (m_Style)
            {
                case Style.Ring:
                    if (fillRing == null) return;
                    fillRing.Sweep01 = w;
                    // Closed ring: a spinner arc orbiting forever (the angles wrap).
                    // Open gauge: the arc bounces between the span's ends instead —
                    // wrapping across the gap reads as teleporting.
                    fillRing.Offset01 = fillRing.SpanAngle >= 359.9f
                        ? t
                        : Mathf.PingPong(t * 2f, 1f) * (1f - w);
                    break;

                case Style.Segments:
                {
                    int n = segmentFills.Count;
                    if (n == 0) return;
                    // A lit window of ~w·N blocks marching left → right, wrapping.
                    int win = Mathf.Max(1, Mathf.RoundToInt(w * n));
                    int start = Mathf.FloorToInt(t * n) % n;
                    for (int i = 0; i < n; i++)
                    {
                        bool lit = ((i - start + n) % n) < win;
                        var go = segmentFills[i];
                        if (go != null && go.activeSelf != lit) go.SetActive(lit);
                    }
                    break;
                }

                default:
                    if (fillRect == null) return;
                    // The segment's leading edge travels 0 → 1+w, so it enters from the
                    // left edge and exits fully past the right one before wrapping.
                    float lead = t * (1f + w);
                    SetFillX(Mathf.Clamp01(lead - w), Mathf.Clamp01(lead));
                    break;
            }
        }

        void ApplyFill()
        {
            if (m_Indeterminate && Application.isPlaying) return; // Update() drives the animation
            switch (m_Style)
            {
                case Style.Ring:
                    if (fillRing != null) { fillRing.Offset01 = 0f; fillRing.Sweep01 = Ratio; }
                    break;
                case Style.Segments:
                {
                    int n = segmentFills.Count;
                    int lit = Mathf.Clamp(Mathf.RoundToInt(Ratio * n), 0, n);
                    for (int i = 0; i < n; i++)
                    {
                        var go = segmentFills[i];
                        if (go != null && go.activeSelf != (i < lit)) go.SetActive(i < lit);
                    }
                    break;
                }
                default:
                    if (fillRect != null) SetFillX(0f, Ratio);
                    break;
            }
        }

        void SetFillX(float xMin, float xMax)
        {
            // Only the x anchors move — the Fill keeps the vertical band the importer
            // gave it inside the Fill Area.
            var a = fillRect.anchorMin; a.x = xMin; fillRect.anchorMin = a;
            var b = fillRect.anchorMax; b.x = xMax; fillRect.anchorMax = b;
            fillRect.offsetMin = Vector2.zero;
            fillRect.offsetMax = Vector2.zero;
        }

        void UpdateValueText()
        {
            if (tmpTxt_value == null) return;
            // Percent of the range ('42%'); ValueTextDecimals > 0 gives fixed decimals
            // ('42.5%') so the read-out width stays stable. Blank while indeterminate —
            // there is no meaningful value to show.
            if (m_Indeterminate) { tmpTxt_value.text = ""; return; }
            string fmt = m_ValueTextDecimals <= 0 ? "0" : "F" + m_ValueTextDecimals;
            tmpTxt_value.text = (Ratio * 100f).ToString(fmt, CultureInfo.InvariantCulture) + "%";
        }
    }
}
