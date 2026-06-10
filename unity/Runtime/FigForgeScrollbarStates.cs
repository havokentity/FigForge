// =============================================================================
// FigForge — hover/press tint for a scrollbar thumb. Mirrors FigForgeListRow:
// recolours the bound thumb graphic (SDF rect or flat Image) per pointer state.
// Lives on the Scrollbar root, so hovering anywhere on the bar lights the thumb.
// =============================================================================

using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace FigForge
{
    [AddComponentMenu("FigForge/Scrollbar States")]
    public class FigForgeScrollbarStates : MonoBehaviour,
        IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler
    {
        // SERIALIZED: built in the editor at import; a private cache would be lost on
        // entering play mode (same lesson as FigForgeListRow.boundGraphic).
        public Graphic bound;
        public FigForgeFill regular = FigForgeFill.Solid(Color.white);
        public FigForgeFill rollover = FigForgeFill.Solid(Color.white);
        public FigForgeFill pressed = FigForgeFill.Solid(Color.white);
        public bool hasRollover, hasPressed;

        bool _over, _down;

        void OnEnable() { Apply(); }

        public void OnPointerEnter(PointerEventData e) { _over = true; Apply(); }
        public void OnPointerExit(PointerEventData e) { _over = false; _down = false; Apply(); }
        public void OnPointerDown(PointerEventData e) { _down = true; Apply(); }
        public void OnPointerUp(PointerEventData e) { _down = false; Apply(); }

        void Apply()
        {
            FigForgeFill f = (_down && hasPressed) ? pressed
                : (_over && hasRollover) ? rollover
                : regular;
            if (bound is FigForgeLayeredRect lr) lr.SetPrimaryFill(f);
            else if (bound is FigForgeRoundedRect rr) rr.SetFill(f);
            else if (bound != null) bound.color = f.color;
        }
    }
}
