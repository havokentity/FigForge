// =============================================================================
// FigForge — Slider control. A standard uGUI Slider (value/minValue/maxValue/
// fillRect/handleRect all inherited) plus typed references to its label and
// live value read-out, and optional slot snapping, wired by the importer. The
// value range is the Figma component's authored [minValue..maxValue] (legacy
// imports: 0..1, the Fill÷Track width ratio).
//
// Everything is re-rangeable through code at runtime: the inherited
// minValue/maxValue setters and the Slots property all funnel through Set, so
// the current value re-clamps/re-snaps and the read-out refreshes on change.
// (The tick NOTCH visuals are the baked Figma 'Ticks' layer — changing Slots in
// code changes the snapping, not the notch graphics.)
// =============================================================================

using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine.Serialization;
using UnityEngine.UI;

namespace FigForge
{
    [AddComponentMenu("FigForge/Controls/Slider")]
    [DisallowMultipleComponent]
    public class FigForgeSlider : Slider
    {
        [Tooltip("The slider's text label (wired by the importer). May be null.")]
        public TMP_Text tmpTxt_label;

        [Tooltip("Live value read-out (wired by the importer when the Figma component has a 'Value' layer). Rewritten on every value change. May be null.")]
        public TMP_Text tmpTxt_value;

        [Tooltip("Decimal places the value read-out shows (0 = whole numbers). Display-only — the value itself stays a float.")]
        [SerializeField] int m_ValueTextDecimals; // default 0 — integer read-out

        /// <summary>Decimal places the value read-out shows — `slider.ValueTextDecimals = 2`
        /// → "0.50". Default 0: whole numbers. Display-only rounding; `value` stays the
        /// exact float. Setting it reformats the read-out immediately.</summary>
        public int ValueTextDecimals
        {
            get => m_ValueTextDecimals;
            set
            {
                m_ValueTextDecimals = Mathf.Clamp(value, 0, 7);
                UpdateValueText();
            }
        }

        [Tooltip("Discrete slot count (0/1 = continuous). Values snap to this many evenly spaced positions across [minValue..maxValue], including both ends — drags, track clicks, and code-assigned values alike.")]
        [SerializeField, FormerlySerializedAs("slots")] int m_Slots;

        /// <summary>Discrete slot count (0/1 = continuous) — `slider.Slots = 11`.
        /// Setting it re-snaps the current value to the new grid immediately
        /// (onValueChanged fires if the value moves).</summary>
        public int Slots
        {
            get => m_Slots;
            set
            {
                m_Slots = Mathf.Max(0, value);
                Set(this.value, true); // re-snap the current value to the new grid
            }
        }

        /// <summary>The label's text — `slider.Label = "Volume"`. No-op if there's no label.
        /// (Use the `tmpTxt_label` TMP_Text for font/colour/advanced styling.)</summary>
        public string Label
        {
            get => tmpTxt_label != null ? tmpTxt_label.text : null;
            set { if (tmpTxt_label != null) tmpTxt_label.text = value; }
        }

        /// <summary>Show/hide the whole control — `slider.isVisible = false`. Drives
        /// GameObject.SetActive, so a hidden control stops rendering, receiving input,
        /// and contributing to layout.</summary>
        public bool isVisible
        {
            get => gameObject.activeSelf;
            set => gameObject.SetActive(value);
        }

        // Every value write funnels through Set (the value setter, SetValueWithoutNotify,
        // and pointer drags/track clicks), so overriding it snaps ALL of them — and the
        // read-out refreshes unconditionally (base.Set early-outs on an unchanged value,
        // but a freshly-built read-out still needs its initial text).
        protected override void Set(float input, bool sendCallback = true)
        {
            base.Set(Snap(input), sendCallback);
            UpdateValueText();
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            UpdateValueText();
        }

#if UNITY_EDITOR
        protected override void OnValidate()
        {
            base.OnValidate();
            UpdateValueText();
        }
#endif

        float Snap(float v)
        {
            if (m_Slots < 2 || maxValue <= minValue) return v;
            float t = Mathf.InverseLerp(minValue, maxValue, Mathf.Clamp(v, minValue, maxValue));
            float snapped = Mathf.Round(t * (m_Slots - 1)) / (m_Slots - 1);
            return Mathf.Lerp(minValue, maxValue, snapped);
        }

        void UpdateValueText()
        {
            if (tmpTxt_value == null) return;
            // Whole numbers by default ('25'); ValueTextDecimals > 0 gives fixed
            // decimals ('0.50') so the read-out width stays stable while dragging.
            string fmt = m_ValueTextDecimals <= 0 ? "0" : "F" + m_ValueTextDecimals;
            tmpTxt_value.text = value.ToString(fmt, CultureInfo.InvariantCulture);
        }

        // --- Behaviour hooks (shared, lazily allocated; see FigForgePointerEvents).
        // onValueChanged covers value moves; these cover hover/press/focus.
        readonly FigForgePointerEvents _events = new FigForgePointerEvents();

        /// <summary>Pointer moved onto the slider. Hover behaviour hook; the visual is captured-state driven.</summary>
        public UnityEvent onPointerEnter => _events.onPointerEnter;
        /// <summary>Pointer left the slider.</summary>
        public UnityEvent onPointerExit => _events.onPointerExit;
        /// <summary>Pointer pressed down on the slider (fires before the track-click/drag is handled).</summary>
        public UnityEvent onPointerDown => _events.onPointerDown;
        /// <summary>Pointer released over the slider.</summary>
        public UnityEvent onPointerUp => _events.onPointerUp;
        /// <summary>Slider gained keyboard/gamepad navigation focus.</summary>
        public UnityEvent onSelected => _events.onSelected;
        /// <summary>Slider lost navigation focus.</summary>
        public UnityEvent onDeselected => _events.onDeselected;

        public override void OnPointerEnter(PointerEventData eventData) { base.OnPointerEnter(eventData); _events.RaiseEnter(); }
        public override void OnPointerExit(PointerEventData eventData) { base.OnPointerExit(eventData); _events.RaiseExit(); }
        public override void OnPointerDown(PointerEventData eventData) { base.OnPointerDown(eventData); _events.RaiseDown(); }
        public override void OnPointerUp(PointerEventData eventData) { base.OnPointerUp(eventData); _events.RaiseUp(); }
        public override void OnSelect(BaseEventData eventData) { base.OnSelect(eventData); _events.RaiseSelected(); }
        public override void OnDeselect(BaseEventData eventData) { base.OnDeselect(eventData); _events.RaiseDeselected(); }
    }
}
