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
        public string projectName;
        public string screenName;
        public string role;
        public string section;
        public string importKey;
        public string manifestHash;
    }
}
