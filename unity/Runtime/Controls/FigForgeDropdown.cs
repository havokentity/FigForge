// =============================================================================
// FigForge — Dropdown control. A standard TMP_Dropdown (options, selected index,
// template popup all inherited) under FigForge ownership. `label` surfaces the
// inherited caption text so callers can read/skin the closed-state label without
// reaching for the protected member.
// =============================================================================

using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;

namespace FigForge
{
    [AddComponentMenu("FigForge/Controls/Dropdown")]
    [DisallowMultipleComponent]
    public class FigForgeDropdown : TMP_Dropdown
    {
        /// <summary>The closed-state caption label (TMP_Dropdown.captionText).</summary>
        public TMP_Text tmpTxt_label => captionText;

        /// <summary>The closed-state caption text — `dropdown.Label = "Choose…"`. No-op if none.</summary>
        public string Label
        {
            get => captionText != null ? captionText.text : null;
            set { if (captionText != null) captionText.text = value; }
        }

        /// <summary>Show/hide the whole control — `dropdown.isVisible = false`. Drives
        /// GameObject.SetActive, so a hidden control stops rendering, receiving input,
        /// and contributing to layout.</summary>
        public bool isVisible
        {
            get => gameObject.activeSelf;
            set => gameObject.SetActive(value);
        }

        // --- Behaviour hooks (shared, lazily allocated; see FigForgePointerEvents).
        readonly FigForgePointerEvents _events = new FigForgePointerEvents();

        /// <summary>Pointer moved onto the dropdown. Hover behaviour hook; the visual is captured-state driven.</summary>
        public UnityEvent onPointerEnter => _events.onPointerEnter;
        /// <summary>Pointer left the dropdown.</summary>
        public UnityEvent onPointerExit => _events.onPointerExit;
        /// <summary>Pointer pressed down on the dropdown (fires before the popup opens).</summary>
        public UnityEvent onPointerDown => _events.onPointerDown;
        /// <summary>Pointer released over the dropdown.</summary>
        public UnityEvent onPointerUp => _events.onPointerUp;
        /// <summary>Dropdown gained keyboard/gamepad navigation focus.</summary>
        public UnityEvent onSelected => _events.onSelected;
        /// <summary>Dropdown lost navigation focus.</summary>
        public UnityEvent onDeselected => _events.onDeselected;

        public override void OnPointerEnter(PointerEventData eventData) { base.OnPointerEnter(eventData); _events.RaiseEnter(); }
        public override void OnPointerExit(PointerEventData eventData) { base.OnPointerExit(eventData); _events.RaiseExit(); }
        public override void OnPointerDown(PointerEventData eventData) { base.OnPointerDown(eventData); _events.RaiseDown(); }
        public override void OnPointerUp(PointerEventData eventData) { base.OnPointerUp(eventData); _events.RaiseUp(); }
        public override void OnSelect(BaseEventData eventData) { base.OnSelect(eventData); _events.RaiseSelected(); }
        public override void OnDeselect(BaseEventData eventData) { base.OnDeselect(eventData); _events.RaiseDeselected(); }
    }
}
