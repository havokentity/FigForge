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
        public TMP_Text label => captionText;
    }
}
