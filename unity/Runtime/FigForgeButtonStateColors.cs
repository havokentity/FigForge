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
        public Color normal = Color.white,      normal2 = Color.white;
        public Color highlighted = Color.white, highlighted2 = Color.white;
        public Color pressed = Color.white,     pressed2 = Color.white;
        public Vector2 normalDir, highlightedDir, pressedDir;

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
            if (_down) _g.SetFill(pressed, pressed2, pressedDir);
            else if (_over) _g.SetFill(highlighted, highlighted2, highlightedDir);
            else _g.SetFill(normal, normal2, normalDir);
        }
    }
}
