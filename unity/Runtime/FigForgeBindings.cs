// =============================================================================
// FigForge — binding slots on a canonical UI prefab. The importer fills these
// from the Figma instance (label text, icon sprite, dropdown options, initial
// value) without knowing the prefab's internal layout. Leave a slot empty to
// skip it. If a canonical prefab has no FigForgeBindings, the importer falls
// back to child-name matching (a child literally named "Label", "Icon", …).
// =============================================================================

using System.Collections.Generic;
using System.Globalization;
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
        public FigForgeProgress progress; // progress bar (value-driven, not a Selectable — takes no input)
        public FigForgeStepper stepper; // numeric stepper (input + +/- buttons)
        public TMP_Text valueText;     // input display / value label
        public TMP_Dropdown optionsTarget; // dropdown

        // Signature of the canonical DEFINITION this prefab was generated from
        // (shape/colours/font/scale). The importer regenerates the prefab when the
        // Figma component changes (signature differs). Empty = hand-made → never
        // auto-regenerated. Editor-only concern; ignored at runtime.
        [HideInInspector] public string signature;

        /// <summary>Apply manifest canonical data to the bound slots. Editor-time.</summary>
        public void Apply(string labelText, string value, List<string> options, Sprite iconSprite = null)
        {
            if (label != null && !string.IsNullOrEmpty(labelText)) label.text = labelText;
            if (icon != null && iconSprite != null)
            {
                icon.sprite = iconSprite;
                icon.enabled = true;
            }

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
                bool parsed = true;
                bool on = false;
                if (bool.TryParse(value, out var boolValue)) on = boolValue;
                else if (value == "on" || value == "1") on = true;
                else if (value == "off" || value == "0") on = false;
                else parsed = false;

                if (parsed)
                {
                    var figForgeToggle = toggle as FigForgeToggle;
                    if (figForgeToggle != null) figForgeToggle.isOn = on;
                    else toggle.isOn = on;
                }
            }

            // Manifest values are invariant-format ("1.5") — parse culture-invariant so
            // comma-decimal locales (de-DE etc.) don't misread or reject them.
            var slider = control as Slider;
            if (slider != null && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) slider.value = v;

            if (progress != null && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pv))
                progress.SetValueWithoutNotify(pv);

            if (stepper != null && value != null)
                stepper.SetValueFromString(value);

            // A slider/progress owns its read-out formatting (the control rewrites it on
            // the value assignment above — integer/percent by default); stamping the raw
            // manifest string here would override that, showing '0.5' instead.
            if (valueText != null && !string.IsNullOrEmpty(value) && slider == null && progress == null && stepper == null) valueText.text = value;

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
