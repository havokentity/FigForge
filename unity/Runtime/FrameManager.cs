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

        [Tooltip("Editor-only import layout: number of root screens per row when pages are spread out for authoring.")]
        public int editorColumns = 5;

        public FigForgeFrame Current { get; private set; }

        // The active manager — the generated `Frames` accessors resolve through this.
        // Set in play mode; null in the editor.
        internal static FrameManager Active { get; private set; }

        protected void Awake()
        {
            Active = this;
            BindAll();
        }
        protected void OnDestroy() { if (Active == this) Active = null; }

        // The active manager, or — when none is set (edit mode) — the one in the open
        // scene, so the generated `Frames` accessors resolve outside play mode too.
        internal static FrameManager Resolve()
            => Active != null ? Active : FindFirstObjectByType<FrameManager>();

        // Resolve a registered frame by GameObject name. Matching also accepts the
        // sanitized Figma key used by generated accessors and prototype navigation.
        public FigForgeFrame Find(string frameName)
        {
            for (int i = 0; i < screens.Count; i++)
            {
                var screen = screens[i];
                if (screen == null) continue;
                if (Matches(screen.ScreenKey, frameName)) return screen;
            }
            return null;
        }

        static bool Matches(string candidate, string requested)
        {
            if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(requested)) return false;
            return candidate == requested || SanitizeKey(candidate) == requested || candidate == SanitizeKey(requested);
        }

        static string SanitizeKey(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            var chars = new char[raw.Length];
            int count = 0;
            bool lastUnderscore = true;
            for (int i = 0; i < raw.Length; i++)
            {
                char c = char.ToLowerInvariant(raw[i]);
                bool keep = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
                if (keep)
                {
                    chars[count++] = c;
                    lastUnderscore = false;
                }
                else if (!lastUnderscore)
                {
                    chars[count++] = '_';
                    lastUnderscore = true;
                }
            }
            if (count > 0 && chars[count - 1] == '_') count--;
            return count > 0 ? new string(chars, 0, count) : "node";
        }

        // Static convenience: the named frame on the Active manager (null if none).
        public static FigForgeFrame Frame(string frameName) => Active != null ? Active.Find(frameName) : null;

        void Start()
        {
            if (screens.Count == 0) return;
            Show(initialScreen != null ? initialScreen : screens[0]);
        }

        public void Register(FigForgeFrame screen)
        {
            if (screen == null) return;
            if (!screens.Contains(screen)) screens.Add(screen);
            if (Application.isPlaying) screen.BindOnce();
        }

        public void BindAll()
        {
            for (int i = 0; i < screens.Count; i++)
                if (screens[i] != null)
                    screens[i].BindOnce();
        }

        public bool Show(string frameName) => Show(Find(frameName), frameName);

        // Show a registered frame directly (reference equality, no name lookup).
        public bool Show(FigForgeFrame frame)
            => Show(frame != null && screens.Contains(frame) ? frame : null,
                    frame != null ? frame.ScreenKey : "<none>");

        bool Show(FigForgeFrame target, string label)
        {
            BindAll();
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
