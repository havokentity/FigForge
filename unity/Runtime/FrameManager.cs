// =============================================================================
// FigForge — FrameManager. Sits on the shared Canvas and shows one route frame at
// a time, plus the matching shell frame when that route mounts inside shell chrome.
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
    // Internal engine: shows one route frame at a time, optionally with its shell.
    // NOT the public API — the generated `Frames` class is the entry point and proxies into this.
    [DisallowMultipleComponent]
    internal class FrameManager : MonoBehaviour
    {
        [Tooltip("All pages this manager controls (children with a FigForgeFrame).")]
        public List<FigForgeFrame> screens = new List<FigForgeFrame>();

        [Tooltip("Frame shown on Start. None = first registered screen.")]
        public FigForgeFrame initialScreen;

        [Tooltip("Legacy single-shell slot. New imports register shell frames in Screens with Is Shell + Shell Key.")]
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
            if (target != null)
            {
                var activeShell = target.usesShell ? FindShell(target.shellKey) : null;
                if (target.usesShell && activeShell == null)
                {
                    Debug.LogWarning($"[FigForge] FrameManager: screen '{target.ScreenKey}' requires shell '{target.shellKey}', but no matching shell is registered.");
                    return false;
                }
                foreach (var s in screens)
                    if (s != null && s != target && s != activeShell)
                        s.SetVisible(false);

                // The shown route snaps to fill its parent viewport. The design-time
                // spread/thumbnail layout is just for authoring; at runtime the active
                // frame takes the available space and the rest are hidden.
                if (activeShell != null)
                {
                    FillParent(activeShell.GetComponent<RectTransform>());
                    activeShell.SetVisible(true);
                    var content = FindContentSlot(activeShell.gameObject);
                    target.transform.SetParent(content != null ? content : activeShell.transform, false);
                    RefreshCompositors(target.gameObject);
                }
                FillParent(target.GetComponent<RectTransform>());
                target.SetVisible(true);
                Current = target;
                if (shell != null) shell.SetActive(target.usesShell && activeShell == null);
            }
            else Debug.LogWarning($"[FigForge] FrameManager: no screen named '{label}'.");
            return target != null;
        }

        FigForgeFrame FindShell(string key)
        {
            for (int i = 0; i < screens.Count; i++)
            {
                var screen = screens[i];
                if (screen == null || !screen.isShell) continue;
                if (Matches(screen.shellKey, key)) return screen;
            }
            return null;
        }

        static Transform FindContentSlot(GameObject root)
        {
            if (root == null) return null;
            var transforms = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < transforms.Length; i++)
            {
                var n = transforms[i].name.ToLowerInvariant();
                if (n == "content" || n == "content_slot") return transforms[i];
            }
            return null;
        }

        static void RefreshCompositors(GameObject root)
        {
            if (root == null) return;
            var sources = root.GetComponentsInChildren<IFigForgeCompositorSource>(true);
            for (int i = 0; i < sources.Length; i++)
                if (sources[i] != null)
                    sources[i].RebindPageCompositor();
            var compositors = root.GetComponentsInChildren<FigForgePageCompositor>(true);
            for (int i = 0; i < compositors.Length; i++)
                if (compositors[i] != null)
                    compositors[i].MarkDirty();
        }

        static void FillParent(RectTransform rt)
        {
            if (rt == null) return;
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            rt.localScale = Vector3.one;
        }
    }
}
