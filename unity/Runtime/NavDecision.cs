// =============================================================================
// FigForge — the outcome a navigation guard returns. Build one with the static
// factories: Allow lets the navigation through; Block / BlockDialog / BlockToast
// stop it (and tell the block handler how to surface the failure); Redirect sends
// the navigation to a different screen instead. See NavGuard / NavContext.
// =============================================================================

namespace FigForge
{
    public enum NavVerdict { Allow, Deny, Redirect }

    public readonly struct NavDecision
    {
        public readonly NavVerdict Verdict;
        public readonly string Reason;            // human-readable; surfaced by the block handler
        public readonly FigForgeFrame DialogFrame; // dialog to show on block (by reference)
        public readonly string DialogKey;          // dialog to show on block (by screen key)
        public readonly string ToastMessage;       // toast text to show on block
        public readonly FigForgeFrame RedirectFrame;
        public readonly string RedirectKey;

        NavDecision(NavVerdict verdict, string reason, FigForgeFrame dialogFrame, string dialogKey,
                    string toastMessage, FigForgeFrame redirectFrame, string redirectKey)
        {
            Verdict = verdict;
            Reason = reason;
            DialogFrame = dialogFrame;
            DialogKey = dialogKey;
            ToastMessage = toastMessage;
            RedirectFrame = redirectFrame;
            RedirectKey = redirectKey;
        }

        public bool IsAllowed => Verdict == NavVerdict.Allow;

        /// <summary>Let the navigation proceed.</summary>
        public static NavDecision Allow()
            => new NavDecision(NavVerdict.Allow, null, null, null, null, null, null);

        /// <summary>Block the navigation. Optional reason is logged / shown by the block handler.</summary>
        public static NavDecision Block(string reason = null)
            => new NavDecision(NavVerdict.Deny, reason, null, null, null, null, null);

        /// <summary>Block and ask the handler to show this dialog frame (e.g. UIFrames.CompleteProfileDialog).</summary>
        public static NavDecision BlockDialog(FigForgeFrame dialog, string reason = null)
            => new NavDecision(NavVerdict.Deny, reason, dialog, null, null, null, null);

        /// <summary>Block and ask the handler to show the dialog with this screen key.</summary>
        public static NavDecision BlockDialog(string dialogKey, string reason = null)
            => new NavDecision(NavVerdict.Deny, reason, null, dialogKey, null, null, null);

        /// <summary>Block and ask the handler to show a toast.</summary>
        public static NavDecision BlockToast(string message)
            => new NavDecision(NavVerdict.Deny, message, null, null, message, null, null);

        /// <summary>Send the navigation to a different screen instead (re-runs guards on the target).</summary>
        public static NavDecision Redirect(FigForgeFrame target, string reason = null)
            => new NavDecision(NavVerdict.Redirect, reason, null, null, null, target, null);

        /// <summary>Redirect to a screen by key.</summary>
        public static NavDecision Redirect(string targetKey, string reason = null)
            => new NavDecision(NavVerdict.Redirect, reason, null, null, null, null, targetKey);
    }
}
