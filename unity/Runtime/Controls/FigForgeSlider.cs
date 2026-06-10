// =============================================================================
// FigForge — Slider control. A standard uGUI Slider (value/minValue/maxValue/
// fillRect/handleRect all inherited) plus a typed reference to its label, wired
// by the importer. Value range is 0..1 (the Figma Fill÷Track width ratio).
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

        /// <summary>The label's text — `slider.Label = "Volume"`. No-op if there's no label.
        /// (Use the `tmpTxt_label` TMP_Text for font/colour/advanced styling.)</summary>
        public string Label
        {
            get => tmpTxt_label != null ? tmpTxt_label.text : null;
            set { if (tmpTxt_label != null) tmpTxt_label.text = value; }
        }
    }
}
