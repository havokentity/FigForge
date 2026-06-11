// =============================================================================
// FigForge — swaps a simple Image sprite per pointer state. Used for dropdown
// Arrow parts exported as Figma-rendered sprites so vectors/text/groups survive.
// =============================================================================

using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace FigForge
{
    [AddComponentMenu("FigForge/Graphic State Sprites")]
    public class FigForgeGraphicStateSprites : MonoBehaviour,
        IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler
    {
        public Image target;
        public Sprite normal;
        public Sprite highlighted;
        public Sprite pressed;

        bool _over, _down;

        void Awake() { Apply(); }
        void OnEnable() { Apply(); }
        // Disabled mid-interaction would re-enable stuck on the hover/press sprite —
        // clear the flags and restore normal (same pattern as FigForgeButtonStateObjects).
        void OnDisable() { _over = false; _down = false; Apply(); }

        public void OnPointerEnter(PointerEventData e) { _over = true; Apply(); }
        public void OnPointerExit(PointerEventData e) { _over = false; _down = false; Apply(); }
        public void OnPointerDown(PointerEventData e) { _down = true; Apply(); }
        public void OnPointerUp(PointerEventData e) { _down = false; Apply(); }

        void Apply()
        {
            if (target == null) return;
            var sprite = _down ? pressed : (_over ? highlighted : normal);
            if (sprite != null) target.sprite = sprite;
        }
    }
}
