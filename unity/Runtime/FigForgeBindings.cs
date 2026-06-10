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

        // Signature of the canonical DEFINITION this prefab was generated from
        // (shape/colours/font/scale). The importer regenerates the prefab when the
        // Figma component changes (signature differs). Empty = hand-made → never
        // auto-regenerated. Editor-only concern; ignored at runtime.
        [HideInInspector] public string signature;

        /// <summary>Apply manifest canonical data to the bound slots. Editor-time.</summary>
        public void Apply(string labelText, string value, List<string> options)
        {
            if (label != null && !string.IsNullOrEmpty(labelText)) label.text = labelText;

            var input = control as TMP_InputField;
            if (input != null)
            {
                if (input.placeholder is TMP_Text placeholder && !string.IsNullOrEmpty(labelText))
                    placeholder.text = labelText;
                if (value != null) input.text = value;
            }

            var toggle = control as Toggle;
            if (toggle != null && value != null)
            {
                if (bool.TryParse(value, out var on)) toggle.isOn = on;
                else if (value == "on" || value == "1") toggle.isOn = true;
                else if (value == "off" || value == "0") toggle.isOn = false;
            }

            var slider = control as Slider;
            if (slider != null && float.TryParse(value, out var v)) slider.value = v;

            // A slider owns its read-out formatting (FigForgeSlider rewrites it on the
            // value assignment above — integer by default); stamping the raw manifest
            // string here would override that, showing '0.5' on an integer read-out.
            if (valueText != null && !string.IsNullOrEmpty(value) && slider == null) valueText.text = value;

            if (optionsTarget != null && options != null && options.Count > 0)
            {
                optionsTarget.ClearOptions();
                optionsTarget.AddOptions(options);
                // 'value' is the selected option text — select it once options are loaded.
                if (!string.IsNullOrEmpty(value))
                {
                    int idx = options.IndexOf(value);
                    if (idx >= 0) optionsTarget.SetValueWithoutNotify(idx);
                }
            }
        }
    }
}
