// =============================================================================
// FigForge — marker the importer stamps on every control it builds, so the editor
// can tell an IMPORTED control from one a developer added by hand (which carries no
// marker). The pre-Forge "manual controls" check relies on this: a name-keyed
// registry alone can't tell them apart, because repeated Figma layer names collapse
// to a single registry entry, making real imported controls look hand-added.
// Runtime (must live on the built GameObjects) but inert at play time.
// =============================================================================

using UnityEngine;

namespace FigForge
{
    [DisallowMultipleComponent]
    public class FigForgeImportedControl : MonoBehaviour { }
}
