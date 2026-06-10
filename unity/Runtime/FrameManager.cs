// =============================================================================
// FigForge — FrameManager. Sits on the shared Canvas and shows exactly one
// FigForgeFrame page at a time, giving a "connected" multi-page app from several
// imported Figma frames.
// =============================================================================

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

// FrameManager is internal — the generated `Frames` class (project assembly) and the
// importer/editor reach its members; consumer code goes through `Frames`, not this.
[assembly: InternalsVisibleTo("FigForge.Editor")]
[assembly: InternalsVisibleTo("FigForge.Generated")]

namespace FigForge
{
    // Internal engine: shows exactly one FigForgeFrame at a time. NOT the public API —
    // the generated `Frames` class is the entry point and proxies into this.
    [DisallowMultipleComponent]
    internal class FrameManager : MonoBehaviour
    {
        [Tooltip("All pages this manager controls (children with a FigForgeFrame).")]
        public List<FigForgeFrame> screens = new List<FigForgeFrame>();

        [Tooltip("Frame shown on Start. None = first registered screen.")]
        public FigForgeFrame initialScreen;

        [Tooltip("Persistent chrome (top/nav menus). Shown only while a screen with usesShell=true is active.")]
        public GameObject shell;

        public FigForgeFrame Current { get; private set; }

        // The active manager — the generated `Frames` accessors resolve through this.
        // Set in play mode; null in the editor.
        internal static FrameManager Active { get; private set; }

        protected void Awake() { Active = this; }
        protected void OnDestroy() { if (Active == this) Active = null; }

        // The active manager, or — when none is set (edit mode) — the one in the open
        // scene, so the generated `Frames` accessors resolve outside play mode too.
        internal static FrameManager Resolve()
            => Active != null ? Active : FindFirstObjectByType<FrameManager>();

        // Resolve a registered frame by its screenName (used by generated accessors).
        public FigForgeFrame Find(string screenName)
        {
            for (int i = 0; i < screens.Count; i++)
                if (screens[i] != null && screens[i].screenName == screenName) return screens[i];
            return null;
        }

        // Static convenience: the named frame on the Active manager (null if none).
        public static FigForgeFrame Frame(string screenName) => Active != null ? Active.Find(screenName) : null;

        void Start()
        {
            if (screens.Count == 0) return;
            Show(initialScreen != null ? initialScreen : screens[0]);
        }

        public void Register(FigForgeFrame screen)
        {
            if (screen != null && !screens.Contains(screen)) screens.Add(screen);
        }

        public bool Show(string screenName) => Show(Find(screenName), screenName);

        // Show a registered frame directly (reference equality, no name lookup).
        public bool Show(FigForgeFrame frame)
            => Show(frame != null && screens.Contains(frame) ? frame : null,
                    frame != null ? frame.screenName : "<none>");

        bool Show(FigForgeFrame target, string label)
        {
            foreach (var s in screens)
                if (s != null) s.SetVisible(s == target);
            if (target != null)
            {
                // The shown root frame snaps to fill the canvas. The design-time side-by-side
                // spread (set up at import) is just for authoring; at runtime the active frame
                // takes the viewport and the rest are hidden.
                if (!target.usesShell) FillParent(target.GetComponent<RectTransform>());
                Current = target;
                if (shell != null) shell.SetActive(target.usesShell);
            }
            else Debug.LogWarning($"[FigForge] FrameManager: no screen named '{label}'.");
            return target != null;
        }

        static void FillParent(RectTransform rt)
        {
            if (rt == null) return;
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        }
    }
}
