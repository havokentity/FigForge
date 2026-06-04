// =============================================================================
// FigForge — Button control. A standard uGUI Button plus a typed reference to
// its TMP label, wired by the importer. FigForge owns every control so we can
// add parts the base class lacks (a Button has no concept of a label) and grow
// the family with controls Unity doesn't ship (see FigForgeList).
//
// Extend behaviour by subclassing or via extension methods — generated frame
// classes expose this directly: FrameManager.LaunchPage.save.label.text = "Go".
// =============================================================================

using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FigForge
{
    [AddComponentMenu("FigForge/Controls/Button")]
    [DisallowMultipleComponent]
    public class FigForgeButton : Button
    {
        [Tooltip("The button's text label (wired by the importer).")]
        public TMP_Text label;

        /// <summary>Convenience get/set for the label's text (no-op if no label).</summary>
        public string text
        {
            get => label != null ? label.text : null;
            set { if (label != null) label.text = value; }
        }
    }
}
