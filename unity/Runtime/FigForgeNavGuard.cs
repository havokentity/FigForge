// =============================================================================
// FigForge — scene-authored navigation guard. Subclass this and drop it on a screen
// root to gate entry to that screen, with the rule visible in the inspector. The
// UIFrameManager collects every guard at Start (inactive screens included — pages begin
// hidden, so OnEnable can't be relied on), mirroring how FigForgeNavBinder wires links.
//
// Prefer the code API (FigForgeNavigation / UIFrames.Guard) for most rules; use this
// component when you want the guard co-located with the screen or selectable by a
// designer. A guard not on a screen registers as a global (every-navigation) guard.
// =============================================================================

using UnityEngine;

namespace FigForge
{
    public abstract class FigForgeNavGuard : MonoBehaviour
    {
        [Tooltip("Screen this guard protects. Empty = the FigForgeFrame on this object or a parent; " +
                 "if none is found the guard runs on every navigation (global).")]
        public FigForgeFrame targetScreen;

        public virtual FigForgeFrame TargetScreen
            => targetScreen != null ? targetScreen : GetComponentInParent<FigForgeFrame>(true);

        /// <summary>Return Allow to let entry proceed, or Block/BlockDialog/Redirect to stop it.</summary>
        public abstract NavDecision Evaluate(NavContext ctx);
    }
}
