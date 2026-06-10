// =============================================================================
// FigForge — Dropdown control. A standard TMP_Dropdown (options, selected index,
// template popup all inherited) under FigForge ownership. `label` surfaces the
// inherited caption text so callers can read/skin the closed-state label without
// reaching for the protected member.
// =============================================================================

using TMPro;
using UnityEngine;

namespace FigForge
{
    [AddComponentMenu("FigForge/Controls/Dropdown")]
    [DisallowMultipleComponent]
    public class FigForgeDropdown : TMP_Dropdown
    {
        /// <summary>The closed-state caption label (TMP_Dropdown.captionText).</summary>
        public TMP_Text tmpTxt_label => captionText;

        /// <summary>The closed-state caption text — `dropdown.Label = "Choose…"`. No-op if none.</summary>
        public string Label
        {
            get => captionText != null ? captionText.text : null;
            set { if (captionText != null) captionText.text = value; }
        }

        /// <summary>Show/hide the whole control — `dropdown.Visible = false`. Drives
        /// GameObject.SetActive, so a hidden control stops rendering, receiving input,
        /// and contributing to layout.</summary>
        public bool Visible
        {
            get => gameObject.activeSelf;
            set => gameObject.SetActive(value);
        }
    }
}
