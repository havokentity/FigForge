// =============================================================================
// FigForge — Slider control. A standard uGUI Slider (value/minValue/maxValue/
// fillRect/handleRect all inherited) plus a typed reference to its label and
// optional slot snapping, wired by the importer. The value range is the Figma
// component's authored [minValue..maxValue] (legacy imports: 0..1, the
// Fill÷Track width ratio).
// =============================================================================

using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FigForge
{
    [AddComponentMenu("FigForge/Controls/Slider")]
    [DisallowMultipleComponent]
    public class FigForgeSlider : Slider
    {
        [Tooltip("The slider's text label (wired by the importer). May be null.")]
        public TMP_Text tmpTxt_label;

        [Tooltip("Discrete slot count (0/1 = continuous). Values snap to `slots` evenly spaced positions across [minValue..maxValue], including both ends — drags, track clicks, and code-assigned values alike.")]
        public int slots;

        /// <summary>The label's text — `slider.Label = "Volume"`. No-op if there's no label.
        /// (Use the `tmpTxt_label` TMP_Text for font/colour/advanced styling.)</summary>
        public string Label
        {
            get => tmpTxt_label != null ? tmpTxt_label.text : null;
            set { if (tmpTxt_label != null) tmpTxt_label.text = value; }
        }

        // Every value write funnels through Set (the value setter, SetValueWithoutNotify,
        // and pointer drags/track clicks), so overriding it snaps ALL of them.
        protected override void Set(float input, bool sendCallback = true)
        {
            base.Set(Snap(input), sendCallback);
        }

        float Snap(float v)
        {
            if (slots < 2 || maxValue <= minValue) return v;
            float t = Mathf.InverseLerp(minValue, maxValue, Mathf.Clamp(v, minValue, maxValue));
            float snapped = Mathf.Round(t * (slots - 1)) / (slots - 1);
            return Mathf.Lerp(minValue, maxValue, snapped);
        }
    }
}
