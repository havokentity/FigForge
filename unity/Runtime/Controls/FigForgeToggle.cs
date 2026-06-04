// =============================================================================
// FigForge — Toggle control (also used for radios via a ToggleGroup). A standard
// uGUI Toggle plus typed references to its label and checkmark, wired by the
// importer. The checkmark may be driven compositely (FigForgeToggleGraphicObject)
// when the Figma checkmark is a rich subtree, so `checkmark` can be null.
// =============================================================================

using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FigForge
{
    [AddComponentMenu("FigForge/Controls/Toggle")]
    [DisallowMultipleComponent]
    public class FigForgeToggle : Toggle
    {
        [Tooltip("The toggle's text label (wired by the importer).")]
        public TMP_Text label;

        [Tooltip("The checkmark graphic shown when on. May be null for composite checkmarks.")]
        public Graphic checkmark;

        /// <summary>Convenience get/set for the label's text (no-op if no label).</summary>
        public string text
        {
            get => label != null ? label.text : null;
            set { if (label != null) label.text = value; }
        }
    }
}
