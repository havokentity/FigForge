// =============================================================================
// FigForge — default reaction to a blocked navigation. Subscribes to the manager's
// NavigationBlocked event and, by default, logs the reason and surfaces the decision's
// dialog (as an overlay on top of the current screen) or toast. Subclass and override
// ShowDialog / ShowToast / HandleBlocked to plug in your own UX, or remove the
// component and handle blocks purely in code (FigForgeNavigation.AddBlockedHandler).
// =============================================================================

using UnityEngine;

namespace FigForge
{
    [DisallowMultipleComponent]
    public class FigForgeNavBlockedHandler : MonoBehaviour
    {
        UIFrameManager _manager;

        void OnEnable()
        {
            if (_manager == null) _manager = GetComponentInParent<UIFrameManager>();
            if (_manager == null) _manager = UIFrameManager.Resolve();
            if (_manager != null) _manager.AddBlockedHandler(HandleBlocked);
        }

        void OnDisable()
        {
            if (_manager != null) _manager.RemoveBlockedHandler(HandleBlocked);
        }

        protected virtual void HandleBlocked(NavContext ctx, NavDecision decision)
        {
            if (!string.IsNullOrEmpty(decision.Reason))
                Debug.Log($"[FigForge] Navigation to '{ctx.ToKey}' blocked: {decision.Reason}");

            var dialog = decision.DialogFrame;
            if (dialog == null && !string.IsNullOrEmpty(decision.DialogKey) && _manager != null)
                dialog = _manager.Find(decision.DialogKey);

            if (dialog != null) ShowDialog(dialog);
            else if (!string.IsNullOrEmpty(decision.ToastMessage)) ShowToast(decision.ToastMessage);
        }

        // Overlay the dialog frame on top of the current screen WITHOUT routing through
        // UIFrameManager.Show (which would hide the current page). Override for custom UX.
        protected virtual void ShowDialog(FigForgeFrame dialog)
        {
            dialog.SetVisible(true);
            dialog.transform.SetAsLastSibling();
        }

        protected virtual void ShowToast(string message)
        {
            Debug.Log($"[FigForge] Toast: {message}");
        }
    }
}
