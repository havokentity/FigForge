// =============================================================================
// FigForge — FrameRole. The role a root Figma frame plays in the imported app.
//
// Stored on the WIRE (manifest/index JSON, import stamps) as a STRING on purpose:
// a string never fails to deserialize, so a newer exporter can add a role an older
// importer doesn't know and it degrades to Screen instead of throwing. Code works
// with the FrameRole enum via FrameRoles.Parse, which tolerates unknown/empty
// values — so adding enum members can't break serialization of existing data.
// =============================================================================

using System;

namespace FigForge
{
    public enum FrameRole
    {
        Screen = 0,  // a normal full-screen page
        Shell = 1,   // persistent chrome; other frames mount into its content slot
        Overlay = 2, // global layer above shells+screens; never swapped by Show (hosts dialogs)
        // Append new roles HERE (never reorder/remove) so serialized int values stay stable.
    }

    public static class FrameRoles
    {
        public const string DefaultWire = "screen";

        /// <summary>Parse a wire role string to the enum. Unknown / empty / out-of-range
        /// values fall back to Screen — never throws, so new roles don't break old data.</summary>
        public static FrameRole Parse(string role)
        {
            if (!string.IsNullOrEmpty(role)
                && Enum.TryParse(role, ignoreCase: true, out FrameRole r)
                && Enum.IsDefined(typeof(FrameRole), r))
                return r;
            return FrameRole.Screen;
        }

        public static bool IsShell(string role) => Parse(role) == FrameRole.Shell;

        public static bool IsOverlay(string role) => Parse(role) == FrameRole.Overlay;

        /// <summary>The canonical lowercase wire string for a role (round-trips with Parse).</summary>
        public static string ToWire(FrameRole role) => role.ToString().ToLowerInvariant();
    }
}
