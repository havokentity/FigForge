// =============================================================================
// FigForge — Input field control. A standard TMP_InputField (text, placeholder,
// caret, validation all inherited) under FigForge ownership. `placeholderText`
// surfaces the placeholder as a TMP_Text for convenient read/skin; `text` is the
// inherited value.
// =============================================================================

using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;

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

        /// <summary>Show/hide the whole control — `input.isVisible = false`. Drives
        /// GameObject.SetActive, so a hidden control stops rendering, receiving input,
        /// and contributing to layout.</summary>
        public bool isVisible
        {
            get => gameObject.activeSelf;
            set => gameObject.SetActive(value);
        }

        // --- Behaviour hooks (shared, lazily allocated; see FigForgePointerEvents).
        // onSelected/onDeselected map to the field gaining/losing edit focus.
        readonly FigForgePointerEvents _events = new FigForgePointerEvents();

        /// <summary>Pointer moved onto the field. Hover behaviour hook; the visual is captured-state driven.</summary>
        public UnityEvent onPointerEnter => _events.onPointerEnter;
        /// <summary>Pointer left the field.</summary>
        public UnityEvent onPointerExit => _events.onPointerExit;
        /// <summary>Pointer pressed down on the field (fires before caret placement).</summary>
        public UnityEvent onPointerDown => _events.onPointerDown;
        /// <summary>Pointer released over the field.</summary>
        public UnityEvent onPointerUp => _events.onPointerUp;
        /// <summary>Field gained focus / began editing (pointer, keyboard, or gamepad nav).</summary>
        public UnityEvent onSelected => _events.onSelected;
        /// <summary>Field lost focus / ended editing.</summary>
        public UnityEvent onDeselected => _events.onDeselected;

        public override void OnPointerEnter(PointerEventData eventData) { base.OnPointerEnter(eventData); _events.RaiseEnter(); }
        public override void OnPointerExit(PointerEventData eventData) { base.OnPointerExit(eventData); _events.RaiseExit(); }
        public override void OnPointerDown(PointerEventData eventData) { base.OnPointerDown(eventData); _events.RaiseDown(); }
        public override void OnPointerUp(PointerEventData eventData) { base.OnPointerUp(eventData); _events.RaiseUp(); }
        public override void OnSelect(BaseEventData eventData) { base.OnSelect(eventData); _events.RaiseSelected(); }
        public override void OnDeselect(BaseEventData eventData) { base.OnDeselect(eventData); _events.RaiseDeselected(); }
    }
}
