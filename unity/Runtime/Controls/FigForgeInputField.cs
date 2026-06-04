// =============================================================================
// FigForge — Input field control. A standard TMP_InputField (text, placeholder,
// caret, validation all inherited) under FigForge ownership. `placeholderText`
// surfaces the placeholder as a TMP_Text for convenient read/skin; `text` is the
// inherited value.
// =============================================================================

using TMPro;
using UnityEngine;

namespace FigForge
{
    [AddComponentMenu("FigForge/Controls/Input Field")]
    [DisallowMultipleComponent]
    public class FigForgeInputField : TMP_InputField
    {
        /// <summary>The placeholder as a TMP_Text (TMP_InputField.placeholder typed). Null if not a TMP_Text.</summary>
        public TMP_Text placeholderText => placeholder as TMP_Text;
    }
}
