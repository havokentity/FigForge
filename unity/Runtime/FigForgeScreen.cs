// =============================================================================
// FigForge — per-screen registry. Lets your code fetch imported controls by
// their Figma instance name instead of walking the hierarchy:
//     screen.Get<Button>("Save")
// Populated by the importer at build time.
// =============================================================================

using System.Collections.Generic;
using UnityEngine;

namespace FigForge
{
    [DisallowMultipleComponent]
    public class FigForgeScreen : MonoBehaviour
    {
        [System.Serializable]
        public class Entry
        {
            public string name;
            public GameObject target;
        }

        public List<Entry> elements = new List<Entry>();

        public void Register(string name, GameObject go)
        {
            if (string.IsNullOrEmpty(name) || go == null) return;
            var e = elements.Find(x => x.name == name);
            if (e != null) e.target = go;
            else elements.Add(new Entry { name = name, target = go });
        }

        public GameObject Get(string name)
        {
            var e = elements.Find(x => x.name == name);
            return e != null ? e.target : null;
        }

        public T Get<T>(string name) where T : Component
        {
            var go = Get(name);
            return go != null ? go.GetComponentInChildren<T>(true) : null;
        }
    }
}
