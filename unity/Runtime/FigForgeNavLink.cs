// =============================================================================
// FigForge — passive navigation link captured from a Figma prototype reaction.
// Holds WHERE this control navigates, but wires no behaviour. A later
// FigForgeNavBinder can turn every link into UIFrameManager.Show(targetScreen).
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

        // Runtime-only idempotency guard. A FigForgeNavBinder sets this once it
        // has wired this link's Button.onClick. Because NavBinder.Start() does a
        // scene-global find, every binder sees every link; this marker ensures a
        // link's button is wired AT MOST ONCE no matter how many binders run.
        // NonSerialized so it never persists and always starts false in a fresh
        // scene / re-enable, letting each link be wired exactly once per load.
        [System.NonSerialized] public bool bound;
    }
}
