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
        [Tooltip("Hide the vertical scrollbar when the content overflows the viewport by no " +
                 "more than this many px. Stock AutoHide shows the bar for ANY overflow — " +
                 "including the few px the list's mask insets add — so a list that visually " +
                 "fits its rows still grew a (useless) scrollbar.")]
        public float scrollbarHideTolerance = 8f;

        bool _dragging;
        bool _allowElastic; // true while a drag/flick is in flight or settling back

        CanvasGroup _scrollbarGroup;  // lazy; hides the bar without fighting base SetActive
        bool _scrollbarSuppressed;    // last state applied to the group

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

        protected override void OnDisable()
        {
            // Drop any in-flight drag/elastic grace so the control doesn't resume mid-bounce
            // when re-enabled (e.g. a frame hidden then shown again): a stale _allowElastic
            // would skip the wheel-overshoot clamp until the next drag.
            _dragging = false;
            _allowElastic = false;
            base.OnDisable();
        }

        protected override void LateUpdate()
        {
            base.LateUpdate();
            EnforceScrollbarTolerance();
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

        // Stock AutoHide re-activates the bar every frame for ANY overflow > 0.01px, so
        // fighting it with SetActive(false) here toggled the GameObject twice per frame
        // forever in the tolerance window — a canvas rebuild every frame. The bar is a
        // pure overlay (AutoHide, never expand-viewport), so the GameObject's active
        // state is left entirely to the base ScrollRect and the tolerance hides the bar
        // visually instead: a CanvasGroup zeroes alpha and blocks interaction. Only a
        // change in the desired state touches the group, so steady frames are free.
        void EnforceScrollbarTolerance()
        {
            if (verticalScrollbar == null || content == null || viewport == null) return;
            float overflow = content.rect.height - viewport.rect.height;
            bool suppress = verticalScrollbarVisibility != ScrollbarVisibility.Permanent
                && overflow <= Mathf.Max(0.01f, scrollbarHideTolerance);
            if (_scrollbarGroup == null)
            {
                if (!suppress) return; // visible is the default — nothing to undo yet
                _scrollbarGroup = verticalScrollbar.GetComponent<CanvasGroup>();
                if (_scrollbarGroup == null) _scrollbarGroup = verticalScrollbar.gameObject.AddComponent<CanvasGroup>();
                _scrollbarSuppressed = !suppress; // force the first apply below
            }
            if (suppress == _scrollbarSuppressed) return;
            _scrollbarSuppressed = suppress;
            _scrollbarGroup.alpha = suppress ? 0f : 1f;
            _scrollbarGroup.interactable = !suppress;
            _scrollbarGroup.blocksRaycasts = !suppress;
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
