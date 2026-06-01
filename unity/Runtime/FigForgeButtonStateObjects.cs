// =============================================================================
// FigForge — toggles Figma-authored state child objects for a Button.
//
// Generated buttons use child objects named Regular, RollOver, Pressed, HitArea,
// and Label. Only the visual state children are swapped; Label remains shared so
// instance text binding updates one place.
// =============================================================================

using UnityEngine;
using UnityEngine.EventSystems;

namespace FigForge
{
    [AddComponentMenu("FigForge/Button State Objects")]
    public class FigForgeButtonStateObjects : MonoBehaviour,
        IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler
    {
        public GameObject regular;
        public GameObject rollOver;
        public GameObject pressed;

        bool _over, _down;

        void Awake() { Apply(); }
        void OnEnable() { _over = false; _down = false; Apply(); }
        void OnDisable() { _over = false; _down = false; Apply(); }

        public void OnPointerEnter(PointerEventData e) { _over = true; Apply(); }
        public void OnPointerExit(PointerEventData e) { _over = false; _down = false; Apply(); }
        public void OnPointerDown(PointerEventData e) { _down = true; Apply(); }
        public void OnPointerUp(PointerEventData e) { _down = false; Apply(); }

        public void Refresh() { Apply(); }

        void Apply()
        {
            var active = _down ? (pressed != null ? pressed : regular)
                : (_over ? (rollOver != null ? rollOver : regular) : regular);
            SetActive(regular, active == regular);
            SetActive(rollOver, active == rollOver);
            SetActive(pressed, active == pressed);
        }

        static void SetActive(GameObject go, bool active)
        {
            if (go != null && go.activeSelf != active) go.SetActive(active);
        }
    }
}
