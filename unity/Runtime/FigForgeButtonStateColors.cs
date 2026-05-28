// =============================================================================
// FigForge — swaps a FigForgeRoundedRect's fill colour per interaction state
// (the Figma button's Regular / Rollover / Pressed). Lives alongside the Button
// (transition set to None). The base (normal) colour applies immediately, so the
// button looks right even before any pointer input.
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
        public Color normal = Color.white;
        public Color highlighted = Color.white;
        public Color pressed = Color.white;

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
            _g.FillColor = _down ? pressed : (_over ? highlighted : normal);
        }
    }
}
