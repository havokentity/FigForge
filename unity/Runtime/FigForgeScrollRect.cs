// =============================================================================
// FigForge — ScrollRect with tamed mouse-wheel overshoot. Stock uGUI applies
// wheel deltas raw: drags get rubber-band damping outside the content range, but
// OnScroll does not — so Elastic lists shoot far past the end on the wheel and
// snap back violently. Clamping wheel-driven movement to the range gives the
// wheel a hard stop (like dragging the scrollbar thumb) while drags/flicks keep
// the full elastic bounce.
// =============================================================================

using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace FigForge
{
    [UnityEngine.AddComponentMenu("FigForge/Scroll Rect")]
    public class FigForgeScrollRect : ScrollRect
    {
        public override void OnScroll(PointerEventData data)
        {
            base.OnScroll(data);
            if (movementType != MovementType.Elastic || content == null) return;
            if (vertical)
            {
                float p = verticalNormalizedPosition;
                if (p < 0f) verticalNormalizedPosition = 0f;
                else if (p > 1f) verticalNormalizedPosition = 1f;
            }
            if (horizontal)
            {
                float p = horizontalNormalizedPosition;
                if (p < 0f) horizontalNormalizedPosition = 0f;
                else if (p > 1f) horizontalNormalizedPosition = 1f;
            }
        }
    }
}
