// =============================================================================
// FigForge — binding slots on a canonical UI prefab. The importer fills these
// from the Figma instance (label text, icon sprite, dropdown options, initial
// value) without knowing the prefab's internal layout. Leave a slot empty to
// skip it. If a canonical prefab has no FigForgeBindings, the importer falls
// back to child-name matching (a child literally named "Label", "Icon", …).
// =============================================================================

using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FigForge
{
    [DisallowMultipleComponent]
    public class FigForgeBindings : MonoBehaviour
    {
        [Header("Generic")]
        public TMP_Text label;
        public Image icon;
        public Graphic background;

        [Header("Control (assign the one this prefab is)")]
        public Selectable control;     // Button / Toggle / Slider / TMP_InputField / TMP_Dropdown root
        public TMP_Text valueText;     // input display / value label
        public TMP_Dropdown optionsTarget; // dropdown

        /// <summary>Apply manifest canonical data to the bound slots. Editor-time.</summary>
        public void Apply(string labelText, string value, List<string> options)
        {
            if (label != null && !string.IsNullOrEmpty(labelText)) label.text = labelText;

            var input = control as TMP_InputField;
            if (input != null && value != null) input.text = value;

            var toggle = control as Toggle;
            if (toggle != null && bool.TryParse(value, out var on)) toggle.isOn = on;

            var slider = control as Slider;
            if (slider != null && float.TryParse(value, out var v)) slider.value = v;

            if (valueText != null && !string.IsNullOrEmpty(value)) valueText.text = value;

            if (optionsTarget != null && options != null && options.Count > 0)
            {
                optionsTarget.ClearOptions();
                optionsTarget.AddOptions(options);
            }
        }
    }
}
