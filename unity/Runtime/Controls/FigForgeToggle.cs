// =============================================================================
// FigForge — Toggle control (also used for radios via a ToggleGroup). A standard
// uGUI Toggle plus typed references to its label and checkmark, wired by the
// importer. The checkmark may be driven compositely (FigForgeToggleGraphicObject)
// when the Figma checkmark is a rich subtree, so `checkmark` can be null.
// =============================================================================

using TMPro;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UI;

namespace FigForge
{
    [AddComponentMenu("FigForge/Controls/Toggle")]
    [DisallowMultipleComponent]
    public class FigForgeToggle : Toggle
    {
        [Tooltip("The toggle's text label (wired by the importer).")]
        [FormerlySerializedAs("label")] public TMP_Text tmpTxt_label;

        [Tooltip("The checkmark graphic shown when on. May be null for composite checkmarks.")]
        public Graphic checkmark;

        /// <summary>Whether the toggle/radio is on. Mirrors Unity's Toggle.isOn, with an
        /// extra refresh for Figma-authored composite checkmark visuals.</summary>
        public new bool isOn
        {
            get => base.isOn;
            set
            {
                base.isOn = value;
                RefreshStateVisuals();
            }
        }

        /// <summary>The label's text — `toggle.Label = "Enabled"`. No-op if there's no label.
        /// (Use the `tmpTxt_label` TMP_Text for font/colour/advanced styling.)</summary>
        public string Label
        {
            get => tmpTxt_label != null ? tmpTxt_label.text : null;
            set { if (tmpTxt_label != null) tmpTxt_label.text = value; }
        }

        /// <summary>Show/hide the whole control — `toggle.isVisible = false`. Drives
        /// GameObject.SetActive, so a hidden control stops rendering, receiving input,
        /// and contributing to layout.</summary>
        public bool isVisible
        {
            get => gameObject.activeSelf;
            set => gameObject.SetActive(value);
        }

        public new void SetIsOnWithoutNotify(bool value)
        {
            base.SetIsOnWithoutNotify(value);
            RefreshStateVisuals();
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            RefreshStateVisuals();
        }

        public void RefreshStateVisuals()
        {
            var reveals = GetComponents<FigForgeToggleGraphicObject>();
            for (int i = 0; i < reveals.Length; i++)
                if (reveals[i] != null)
                    reveals[i].Apply();
        }
    }
}
