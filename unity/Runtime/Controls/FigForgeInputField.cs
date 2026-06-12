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
        public TMP_Text tmpTxt_placeholder => placeholder as TMP_Text;

        /// <summary>The placeholder text — `input.Placeholder = "Email"`. The field's value is the
        /// inherited `text`. No-op if the placeholder isn't a TMP_Text.</summary>
        public string Placeholder
        {
            get => tmpTxt_placeholder != null ? tmpTxt_placeholder.text : null;
            set { var t = tmpTxt_placeholder; if (t != null) t.text = value; }
        }

        /// <summary>Show/hide the whole control — `input.IsVisible = false`. Drives
        /// GameObject.SetActive, so a hidden control stops rendering, receiving input,
        /// and contributing to layout.</summary>
        public bool IsVisible
        {
            get => gameObject.activeSelf;
            set => gameObject.SetActive(value);
        }
    }
}
