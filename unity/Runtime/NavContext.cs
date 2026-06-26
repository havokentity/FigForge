// =============================================================================
// FigForge — a navigation guard and the context it receives. A NavGuard is just a
// function (NavContext -> NavDecision); register one per screen (or globally) to
// gate navigation before UIFrameManager switches frames.
// =============================================================================

namespace FigForge
{
    public delegate NavDecision NavGuard(NavContext ctx);

    public readonly struct NavContext
    {
        /// <summary>The currently-shown screen (null on the very first navigation).</summary>
        public readonly FigForgeFrame From;

        /// <summary>The destination screen being navigated to.</summary>
        public readonly FigForgeFrame To;

        /// <summary>Destination screen key (To.ScreenKey) — handy for logging.</summary>
        public readonly string ToKey;

        /// <summary>What triggered the navigation (e.g. "click"); "navigate" for code-driven nav.</summary>
        public readonly string Trigger;

        /// <summary>The captured Figma nav link when the navigation came from a button; null otherwise.</summary>
        public readonly FigForgeNavLink Link;

        public NavContext(FigForgeFrame from, FigForgeFrame to, string toKey, string trigger, FigForgeNavLink link)
        {
            From = from;
            To = to;
            ToKey = toKey;
            Trigger = trigger;
            Link = link;
        }
    }
}
