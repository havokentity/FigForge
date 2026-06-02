// =============================================================================
// FigForge — swaps a FigForgeRoundedRect's fill per interaction state (the Figma
// button's Regular / Rollover / Pressed). Lives alongside the Button (transition
// set to None). The base (normal) fill applies immediately, so the button looks
// right even before any pointer input. Each state carries a FULL fill — a solid
// or an n-stop gradient — so a gradient button shows its gradient at rest and
// the hover/press states on input.
// =============================================================================

using UnityEngine;
using UnityEngine.EventSystems;

namespace FigForge
{
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
        FigForgeLayeredRect _layered;
        bool _over, _down;

        void Awake() { ResolveTarget(); Apply(); }
        void OnEnable() { ResolveTarget(); Apply(); }

        public void OnPointerEnter(PointerEventData e) { _over = true; Apply(); }
        public void OnPointerExit(PointerEventData e) { _over = false; _down = false; Apply(); }
        public void OnPointerDown(PointerEventData e) { _down = true; Apply(); }
        public void OnPointerUp(PointerEventData e) { _down = false; Apply(); }

        void Apply()
        {
            var fill = _down ? pressed : (_over ? highlighted : normal);
            if (_layered != null) _layered.SetPrimaryFill(fill);
            else if (_g != null) _g.SetFill(fill);
        }

        void ResolveTarget()
        {
            if (_g == null) _g = GetComponent<FigForgeRoundedRect>();
            if (_layered == null) _layered = GetComponent<FigForgeLayeredRect>();
        }
    }
}
