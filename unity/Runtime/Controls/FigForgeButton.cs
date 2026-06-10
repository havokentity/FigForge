// =============================================================================
// FigForge — Button control. A standard uGUI Button plus a typed reference to
// its TMP label, wired by the importer. FigForge owns every control so we can
// add parts the base class lacks (a Button has no concept of a label) and grow
// the family with controls Unity doesn't ship (see FigForgeList).
//
// Extend behaviour by subclassing or via extension methods — generated frame
// classes expose this directly: Frames.LaunchPage.save.label.text = "Go".
// =============================================================================

using TMPro;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UI;

namespace FigForge
{
    [AddComponentMenu("FigForge/Controls/Button")]
    [DisallowMultipleComponent]
    public class FigForgeButton : Button
    {
        [Tooltip("The button's text label (wired by the importer).")]
        [FormerlySerializedAs("label")] public TMP_Text tmpTxt_label;

        /// <summary>The label's text — `button.Label = "Go"`. No-op if there's no label.
        /// (Use the `tmpTxt_label` TMP_Text for font/colour/advanced styling.)</summary>
        public string Label
        {
            get => tmpTxt_label != null ? tmpTxt_label.text : null;
            set { if (tmpTxt_label != null) tmpTxt_label.text = value; }
        }

        /// <summary>Show/hide the whole control — `button.Visible = false`. Drives
        /// GameObject.SetActive, so a hidden control stops rendering, receiving input,
        /// and contributing to layout.</summary>
        public bool Visible
        {
            get => gameObject.activeSelf;
            set => gameObject.SetActive(value);
        }
    }
}
