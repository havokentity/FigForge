// =============================================================================
// FigForge — swaps a simple Graphic colour per pointer state. Used for dropdown
// chevrons whose Regular / Rollover / Pressed states are text-fill colours.
// =============================================================================

using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace FigForge
{
    [AddComponentMenu("FigForge/Graphic State Colors")]
    public class FigForgeGraphicStateColors : MonoBehaviour,
        IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler
    {
        public Graphic target;
        public Color normal = Color.white;
        public Color highlighted = Color.white;
        public Color pressed = Color.white;

        bool _over, _down;

        void Awake() { Apply(); }
        void OnEnable() { Apply(); }
        // Disabled mid-interaction would re-enable stuck on the hover/press colour —
        // clear the flags and restore normal (same pattern as FigForgeButtonStateObjects).
        void OnDisable() { _over = false; _down = false; Apply(); }

        public void OnPointerEnter(PointerEventData e) { _over = true; Apply(); }
        public void OnPointerExit(PointerEventData e) { _over = false; _down = false; Apply(); }
        public void OnPointerDown(PointerEventData e) { _down = true; Apply(); }
        public void OnPointerUp(PointerEventData e) { _down = false; Apply(); }

        void Apply()
        {
            if (target == null) return;
            target.color = _down ? pressed : (_over ? highlighted : normal);
        }
    }
}
