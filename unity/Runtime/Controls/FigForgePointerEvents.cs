// =============================================================================
// FigForge — reusable pointer/selection behaviour-hook surface shared by the
// Selectable controls (Button, Toggle, Dropdown, Slider, InputField).
//
// C# is single-inheritance and each control already extends a different Unity
// Selectable subclass (Button, Toggle, TMP_Dropdown, Slider, TMP_InputField),
// so a shared *base* class is impossible. Controls instead COMPOSE this helper
// and forward their OnPointer*/OnSelect/OnDeselect overrides to the Raise*
// methods, exposing the same `control.onPointerEnter` API everywhere.
//
// Every event allocates lazily: the backing UnityEvent is created only when code
// touches the property to subscribe, and Raise* only fires when one exists — so
// an unsubscribed control allocates nothing (controls appear en masse inside
// lists, tables, and steppers). These are BEHAVIOUR hooks only; the rollover/
// pressed *visuals* still come from the captured Figma states (see
// FigForgeButtonStateColors/Objects), not from here.
// =============================================================================

using UnityEngine.Events;

namespace FigForge
{
    public sealed class FigForgePointerEvents
    {
        UnityEvent _enter;
        UnityEvent _exit;
        UnityEvent _down;
        UnityEvent _up;
        UnityEvent _selected;
        UnityEvent _deselected;

        static UnityEvent Lazy(ref UnityEvent ev)
        {
            if (ev == null) ev = new UnityEvent();
            return ev;
        }

        /// <summary>Pointer moved onto the control — `ctrl.onPointerEnter.AddListener(...)`.
        /// For hover SFX, tooltips, previews. The hover *visual* is already driven by the captured state.</summary>
        public UnityEvent onPointerEnter => Lazy(ref _enter);

        /// <summary>Pointer left the control. Pair with onPointerEnter to cancel a hover preview.</summary>
        public UnityEvent onPointerExit => Lazy(ref _exit);

        /// <summary>Pointer pressed down on the control (fires before any click/value change resolves).</summary>
        public UnityEvent onPointerDown => Lazy(ref _down);

        /// <summary>Pointer released over the control.</summary>
        public UnityEvent onPointerUp => Lazy(ref _up);

        /// <summary>Control gained focus via keyboard/gamepad navigation.</summary>
        public UnityEvent onSelected => Lazy(ref _selected);

        /// <summary>Control lost navigation focus.</summary>
        public UnityEvent onDeselected => Lazy(ref _deselected);

        public void RaiseEnter() { if (_enter != null) _enter.Invoke(); }
        public void RaiseExit() { if (_exit != null) _exit.Invoke(); }
        public void RaiseDown() { if (_down != null) _down.Invoke(); }
        public void RaiseUp() { if (_up != null) _up.Invoke(); }
        public void RaiseSelected() { if (_selected != null) _selected.Invoke(); }
        public void RaiseDeselected() { if (_deselected != null) _deselected.Invoke(); }
    }
}
