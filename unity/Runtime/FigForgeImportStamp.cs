// =============================================================================
// FigForge — import identity for generated scene roots.
//
// The editor importer stamps generated shell/screen roots so a later live import
// can reuse unchanged screens, replace changed screens, and delete removed ones
// instead of blindly duplicating the whole page hierarchy.
// =============================================================================

using UnityEngine;

namespace FigForge
{
    [DisallowMultipleComponent]
    public class FigForgeImportStamp : MonoBehaviour
    {
        [ReadOnly] public string projectName;
        [ReadOnly] public string screenName;
        [ReadOnly] public string role;
        [ReadOnly] public string section;
        [ReadOnly] public string importKey;
        [ReadOnly] public string manifestHash;
    }
}
