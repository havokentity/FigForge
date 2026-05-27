// =============================================================================
// FigForge — CanonicalLibrary. Maps canonical reference names (from element
// names like `Btn_Save_PrimaryButton` → ref "PrimaryButton") to reusable Unity
// prefabs. The importer instantiates these instead of rebuilding the element
// from a PNG/text, so canonical UI (buttons, for now) is defined once and reused
// across every imported page.
// =============================================================================

using System.Collections.Generic;
using UnityEngine;

namespace FigForge
{
    [CreateAssetMenu(fileName = "FigForgeCanonicalLibrary", menuName = "FigForge/Canonical Library")]
    public class CanonicalLibrary : ScriptableObject
    {
        [System.Serializable]
        public class Entry
        {
            [Tooltip("Canonical reference name as used in Figma layer names (the trailing token).")]
            public string referenceName;

            [Tooltip("Prefab instantiated for this reference. For buttons, its root should have a Button + a text label.")]
            public GameObject prefab;
        }

        public List<Entry> buttons = new List<Entry>();

        public GameObject Resolve(string kind, string referenceName)
        {
            // Only "button" is supported today; kept switchable for future kinds.
            if (kind != "button") return null;
            foreach (var e in buttons)
                if (e != null && e.referenceName == referenceName) return e.prefab;
            return null;
        }

        public bool HasAny => buttons != null && buttons.Count > 0;
    }
}
