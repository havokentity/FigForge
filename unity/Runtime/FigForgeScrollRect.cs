// =============================================================================
// FigForge — ScrollRect with tamed mouse-wheel overshoot. Stock uGUI applies
// wheel deltas raw: drags get rubber-band damping outside the content range, but
// OnScroll does not — so Elastic lists shoot far past the end on the wheel and
// snap back violently (worse with trackpad momentum, which keeps streaming
// deltas). Rule enforced here: overshoot that originated from a DRAG (including
// the flick it releases into) keeps the full elastic bounce; any other movement
// (wheel, trackpad momentum, code) hard-stops at the range like dragging the
// scrollbar thumb. Enforced every frame in LateUpdate, so it holds no matter
// which input path moved the content.
// =============================================================================

using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace FigForge
{
    [AddComponentMenu("FigForge/Scroll Rect")]
    public class FigForgeScrollRect : ScrollRect
    {
        bool _dragging;
        bool _allowElastic; // true while a drag/flick is in flight or settling back

        public override void OnBeginDrag(PointerEventData eventData)
        {
            _dragging = true;
            _allowElastic = true;
            base.OnBeginDrag(eventData);
        }

        public override void OnEndDrag(PointerEventData eventData)
        {
            _dragging = false;
            base.OnEndDrag(eventData);
        }

        public override void OnScroll(PointerEventData data)
        {
            base.OnScroll(data);
            if (!_dragging && !_allowElastic) ClampToRange();
        }

        protected override void LateUpdate()
        {
            base.LateUpdate();
            if (movementType != MovementType.Elastic || content == null) return;
            if (_dragging) return;
            if (_allowElastic)
            {
                // A drag/flick is bouncing/settling — once content is back inside the
                // range the elastic grace ends and wheel overshoot clamps again.
                if (InRange()) _allowElastic = false;
                return;
            }
            ClampToRange();
        }

        bool InRange()
        {
            const float eps = 0.0001f;
            if (vertical)
            {
                float p = verticalNormalizedPosition;
                if (p < -eps || p > 1f + eps) return false;
            }
            if (horizontal)
            {
                float p = horizontalNormalizedPosition;
                if (p < -eps || p > 1f + eps) return false;
            }
            return true;
        }

        void ClampToRange()
        {
            if (movementType != MovementType.Elastic || content == null) return;
            if (vertical)
            {
                float p = verticalNormalizedPosition;
                if (p < 0f) { verticalNormalizedPosition = 0f; velocity = new Vector2(velocity.x, 0f); }
                else if (p > 1f) { verticalNormalizedPosition = 1f; velocity = new Vector2(velocity.x, 0f); }
            }
            if (horizontal)
            {
                float p = horizontalNormalizedPosition;
                if (p < 0f) { horizontalNormalizedPosition = 0f; velocity = new Vector2(0f, velocity.y); }
                else if (p > 1f) { horizontalNormalizedPosition = 1f; velocity = new Vector2(0f, velocity.y); }
            }
        }
    }
}
