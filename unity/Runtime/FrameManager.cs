// =============================================================================
// FigForge — FrameManager. Sits on the shared Canvas and shows exactly one
// FigForgeFrame page at a time, giving a "connected" multi-page app from several
// imported Figma frames.
// =============================================================================

using System.Collections.Generic;
using UnityEngine;

namespace FigForge
{
    // `partial` so the importer-generated companion (FrameManager.g.cs) can add
    // strongly-typed static frame accessors, e.g. FrameManager.LaunchPage.
    [DisallowMultipleComponent]
    public partial class FrameManager : MonoBehaviour
    {
        [Tooltip("All pages this manager controls (children with a FigForgeFrame).")]
        public List<FigForgeFrame> screens = new List<FigForgeFrame>();

        [Tooltip("screenName shown on Start. Empty = first registered screen.")]
        public string initialScreen;

        [Tooltip("Persistent chrome (top/nav menus). Shown only while a screen with usesShell=true is active.")]
        public GameObject shell;

        public FigForgeFrame Current { get; private set; }

        // The active manager — the generated typed accessors (FrameManager.LaunchPage)
        // resolve through this. Set in play mode; null in the editor.
        public static FrameManager Active { get; private set; }

        protected void Awake() { Active = this; }
        protected void OnDestroy() { if (Active == this) Active = null; }

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
            if (!string.IsNullOrEmpty(initialScreen)) Show(initialScreen);
            else Show(screens[0].screenName);
        }

        public void Register(FigForgeFrame screen)
        {
            if (screen != null && !screens.Contains(screen)) screens.Add(screen);
        }

        public bool Show(string screenName)
        {
            FigForgeFrame target = null;
            foreach (var s in screens)
            {
                if (s == null) continue;
                bool match = s.screenName == screenName;
                s.SetVisible(match);
                if (match) target = s;
            }
            if (target != null)
            {
                Current = target;
                if (shell != null) shell.SetActive(target.usesShell);
            }
            else Debug.LogWarning($"[FigForge] FrameManager: no screen named '{screenName}'.");
            return target != null;
        }
    }
}
