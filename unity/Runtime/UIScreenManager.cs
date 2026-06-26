// =============================================================================
// FigForge — UI Toolkit multi-page manager (the UITK counterpart of
// UIFrameManager/FigForgeFrame). Sits next to a UIDocument and shows one generated
// page (a VisualTreeAsset) at a time by cloning it into the document root.
// =============================================================================

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace FigForge
{
    [RequireComponent(typeof(UIDocument))]
    [DisallowMultipleComponent]
    public class UIScreenManager : MonoBehaviour
    {
        [System.Serializable]
        public class Page
        {
            public string name;
            public VisualTreeAsset tree;
        }

        public List<Page> pages = new List<Page>();
        [Tooltip("Page shown on Start. None = first registered page.")]
        public VisualTreeAsset initialScreen;

        public string Current { get; private set; }

        UIDocument _doc;

        void Awake() { _doc = GetComponent<UIDocument>(); }

        void Start()
        {
            if (pages.Count == 0) return;
            var init = initialScreen != null ? pages.Find(x => x != null && x.tree == initialScreen) : null;
            Show(init != null ? init.name : pages[0].name);
        }

        /// <summary>Register (or replace) a page. Used by the importer at build time.</summary>
        public void Register(string name, VisualTreeAsset tree)
        {
            var p = pages.Find(x => x != null && x.name == name);
            if (p != null) p.tree = tree;
            else pages.Add(new Page { name = name, tree = tree });
        }

        public bool Show(string name)
        {
            var p = pages.Find(x => x != null && x.name == name);
            if (p == null || p.tree == null)
            {
                Debug.LogWarning($"[FigForge] UIScreenManager: no UITK page '{name}'.");
                return false;
            }
            if (_doc == null) _doc = GetComponent<UIDocument>();
            var root = _doc.rootVisualElement;
            if (root == null) return false;
            root.Clear();
            p.tree.CloneTree(root);
            Current = name;
            return true;
        }
    }
}
