// =============================================================================
// FigForge — Button control. A standard uGUI Button plus a typed reference to
// its TMP label, wired by the importer. FigForge owns every control so we can
// add parts the base class lacks (a Button has no concept of a label) and grow
// the family with controls Unity doesn't ship (see FigForgeList).
//
// Extend behaviour by subclassing or via extension methods — generated frame
// classes expose this directly: Frames.LaunchPage.save.label.text = "Go".
//
// Beyond Unity's `onClick`, FigForge surfaces the pointer/selection moments code
// usually wants (hover SFX, tooltips, press feedback): onPointerEnter/Exit/Down/
// Up and onSelected/onDeselected. They are BEHAVIOUR hooks only — the rollover/
// pressed *visuals* still come from the captured Figma states (see
// FigForgeButtonStateColors/Objects), not from here. The events allocate lazily,
// so a button nobody subscribes to costs nothing — buttons appear en masse
// inside lists, tables, and steppers.
// =============================================================================

using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
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

        /// <summary>Show/hide the whole control — `button.isVisible = false`. Drives
        /// GameObject.SetActive, so a hidden control stops rendering, receiving input,
        /// and contributing to layout.</summary>
        public bool isVisible
        {
            get => gameObject.activeSelf;
            set => gameObject.SetActive(value);
        }

        // --- Behaviour hooks --------------------------------------------------
        // Shared, lazily-allocated pointer/selection surface (see
        // FigForgePointerEvents). Visuals still come from the captured state.

        readonly FigForgePointerEvents _events = new FigForgePointerEvents();

        /// <summary>Pointer moved onto the button — `btn.onPointerEnter.AddListener(...)`.
        /// For hover SFX, tooltips, previews. The hover *visual* is already driven by the captured state.</summary>
        public UnityEvent onPointerEnter => _events.onPointerEnter;

        /// <summary>Pointer left the button. Pair with onPointerEnter to cancel a hover preview.</summary>
        public UnityEvent onPointerExit => _events.onPointerExit;

        /// <summary>Pointer pressed down on the button (fires before the click resolves on release).</summary>
        public UnityEvent onPointerDown => _events.onPointerDown;

        /// <summary>Pointer released over the button.</summary>
        public UnityEvent onPointerUp => _events.onPointerUp;

        /// <summary>Button gained focus via keyboard/gamepad navigation.</summary>
        public UnityEvent onSelected => _events.onSelected;

        /// <summary>Button lost navigation focus.</summary>
        public UnityEvent onDeselected => _events.onDeselected;

        public override void OnPointerEnter(PointerEventData eventData)
        {
            base.OnPointerEnter(eventData); // keep the captured-state visual transition
            _events.RaiseEnter();
        }

        public override void OnPointerExit(PointerEventData eventData)
        {
            base.OnPointerExit(eventData);
            _events.RaiseExit();
        }

        public override void OnPointerDown(PointerEventData eventData)
        {
            base.OnPointerDown(eventData);
            _events.RaiseDown();
        }

        public override void OnPointerUp(PointerEventData eventData)
        {
            base.OnPointerUp(eventData);
            _events.RaiseUp();
        }

        public override void OnSelect(BaseEventData eventData)
        {
            base.OnSelect(eventData);
            _events.RaiseSelected();
        }

        public override void OnDeselect(BaseEventData eventData)
        {
            base.OnDeselect(eventData);
            _events.RaiseDeselected();
        }
    }
}
