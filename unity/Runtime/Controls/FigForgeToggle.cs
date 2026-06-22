// =============================================================================
// FigForge — Toggle control (also used for radios via a ToggleGroup). A standard
// uGUI Toggle plus typed references to its label and checkmark, wired by the
// importer. The checkmark may be driven compositely (FigForgeToggleGraphicObject)
// when the Figma checkmark is a rich subtree, so `checkmark` can be null.
// =============================================================================

using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
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
            RefreshGraphicObjectCache();
            RefreshStateVisuals();
        }

        // Reusable buffer for the sibling graphic-object reveals, refilled (non-allocating)
        // in OnEnable so dynamically added FigForgeToggleGraphicObjects are still picked up.
        readonly List<FigForgeToggleGraphicObject> _reveals = new List<FigForgeToggleGraphicObject>();

        void RefreshGraphicObjectCache()
        {
            GetComponents(_reveals);
        }

        public void RefreshStateVisuals()
        {
            for (int i = 0; i < _reveals.Count; i++)
                if (_reveals[i] != null)
                    _reveals[i].Apply();
        }

        // --- Behaviour hooks (shared, lazily allocated; see FigForgePointerEvents).
        // Subclasses (FigForgeSwitch) inherit this surface unchanged.
        readonly FigForgePointerEvents _events = new FigForgePointerEvents();

        /// <summary>Pointer moved onto the toggle. Hover behaviour hook; the visual is captured-state driven.</summary>
        public UnityEvent onPointerEnter => _events.onPointerEnter;
        /// <summary>Pointer left the toggle.</summary>
        public UnityEvent onPointerExit => _events.onPointerExit;
        /// <summary>Pointer pressed down on the toggle.</summary>
        public UnityEvent onPointerDown => _events.onPointerDown;
        /// <summary>Pointer released over the toggle.</summary>
        public UnityEvent onPointerUp => _events.onPointerUp;
        /// <summary>Toggle gained keyboard/gamepad navigation focus.</summary>
        public UnityEvent onSelected => _events.onSelected;
        /// <summary>Toggle lost navigation focus.</summary>
        public UnityEvent onDeselected => _events.onDeselected;

        public override void OnPointerEnter(PointerEventData eventData) { base.OnPointerEnter(eventData); _events.RaiseEnter(); }
        public override void OnPointerExit(PointerEventData eventData) { base.OnPointerExit(eventData); _events.RaiseExit(); }
        public override void OnPointerDown(PointerEventData eventData) { base.OnPointerDown(eventData); _events.RaiseDown(); }
        public override void OnPointerUp(PointerEventData eventData) { base.OnPointerUp(eventData); _events.RaiseUp(); }
        public override void OnSelect(BaseEventData eventData) { base.OnSelect(eventData); _events.RaiseSelected(); }
        public override void OnDeselect(BaseEventData eventData) { base.OnDeselect(eventData); _events.RaiseDeselected(); }
    }
}
