// =============================================================================
// FigForge — swaps a FigForgeRoundedRect's fill per interaction state (the Figma
// button's Regular / Rollover / Pressed). Lives alongside the Button (transition
// set to None). The base (normal) fill applies immediately, so the button looks
// right even before any pointer input. Each state carries a FULL fill — a solid
// (fill2 == fill, dir = 0) or a 2-stop gradient — so a gradient button shows its
// gradient at rest and the (solid or gradient) hover/press states on input.
// =============================================================================

using UnityEngine;
using UnityEngine.EventSystems;

namespace FigForge
{
    [RequireComponent(typeof(FigForgeRoundedRect))]
    [AddComponentMenu("FigForge/Button State Colors")]
    public class FigForgeButtonStateColors : MonoBehaviour,
        IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler
    {
        // One fill per state (solid or gradient). Grouped into FigForgeFill so the
        // inspector shows three tidy entries instead of nine loose colour/dir fields.
        public FigForgeFill normal = FigForgeFill.Solid(Color.white);
        public FigForgeFill highlighted = FigForgeFill.Solid(Color.white);
        public FigForgeFill pressed = FigForgeFill.Solid(Color.white);

        FigForgeRoundedRect _g;
        bool _over, _down;

        void Awake() { _g = GetComponent<FigForgeRoundedRect>(); Apply(); }
        void OnEnable() { if (_g == null) _g = GetComponent<FigForgeRoundedRect>(); Apply(); }

        public void OnPointerEnter(PointerEventData e) { _over = true; Apply(); }
        public void OnPointerExit(PointerEventData e) { _over = false; _down = false; Apply(); }
        public void OnPointerDown(PointerEventData e) { _down = true; Apply(); }
        public void OnPointerUp(PointerEventData e) { _down = false; Apply(); }

        void Apply()
        {
            if (_g == null) return;
            _g.SetFill(_down ? pressed : (_over ? highlighted : normal));
        }
    }
}
