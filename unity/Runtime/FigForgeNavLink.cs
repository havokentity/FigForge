// =============================================================================
// FigForge — passive navigation link captured from a Figma prototype reaction.
// Holds WHERE this control navigates, but wires no behaviour. A later
// FigForgeNavBinder can turn every link into ScreenManager.Show(targetScreen).
// =============================================================================

using UnityEngine;

namespace FigForge
{
    [DisallowMultipleComponent]
    public class FigForgeNavLink : MonoBehaviour
    {
        [Tooltip("Destination screen name (from the Figma prototype 'Navigate to' reaction).")]
        public string targetScreen;

        [Tooltip("What triggers it in the design (data only — no listener is added).")]
        public string trigger = "click";
    }
}
