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
        {
            if (Active != null) return Active;
#if UNITY_2023_1_OR_NEWER
            return Object.FindFirstObjectByType<FrameManager>();
#else
            return Object.FindObjectOfType<FrameManager>();
#endif
        }

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
            RegisterSceneGuards();
            if (screens.Count == 0) return;
            // The initial screen bypasses guards: there's no "from" yet, and blocking the
            // entry point would leave nothing visible.
            ShowUnguarded(initialScreen != null ? initialScreen : screens[0]);
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

        public bool Show(string frameName) => Show(frameName, null);

        // Navigate by name, carrying the source link so guards can see the trigger/link.
        public bool Show(string frameName, FigForgeNavLink via)
            => ShowInternal(Find(frameName), frameName, true, 0, via);

        // Show a registered frame directly (reference equality, no name lookup).
        public bool Show(FigForgeFrame frame)
            => ShowInternal(frame != null && screens.Contains(frame) ? frame : null,
                            frame != null ? frame.ScreenKey : "<none>", true, 0, null);

        // Show without consulting navigation guards (initial screen / engine-internal).
        internal bool ShowUnguarded(FigForgeFrame frame)
            => ShowInternal(frame != null && screens.Contains(frame) ? frame : null,
                            frame != null ? frame.ScreenKey : "<none>", false, 0, null);

        const int MaxRedirectDepth = 8;

        // Re-entrancy generation token. Hiding other screens fires OnHide(); a handler may
        // navigate re-entrantly during that loop. Each ShowInternal claims a generation, and
        // only the call whose generation is still current commits the final Current/visibility
        // — so the innermost nav wins and the outer call doesn't clobber it on unwind.
        int _showGen;

        bool ShowInternal(FigForgeFrame target, string label, bool runGuards, int redirectDepth, FigForgeNavLink via)
        {
            BindAll();
            if (target == null)
            {
                Debug.LogWarning($"[FigForge] FrameManager: no screen named '{label}'.");
                return false;
            }

            if (runGuards && (_globalGuards.Count > 0 || _guards.Count > 0))
            {
                var ctx = new NavContext(Current, target, target.ScreenKey,
                                         via != null ? via.trigger : "navigate", via);
                var decision = EvaluateGuards(ctx);
                if (decision.Verdict == NavVerdict.Deny)
                {
                    RaiseBlocked(ctx, decision);
                    return false;
                }
                if (decision.Verdict == NavVerdict.Redirect)
                {
                    if (redirectDepth >= MaxRedirectDepth)
                    {
                        Debug.LogWarning($"[FigForge] FrameManager: navigation redirect loop (>{MaxRedirectDepth}) at '{label}' — aborting.");
                        return false;
                    }
                    var redirect = decision.RedirectFrame ?? Find(decision.RedirectKey);
                    if (redirect == null)
                    {
                        Debug.LogWarning($"[FigForge] FrameManager: guard redirect target '{decision.RedirectKey ?? "<frame>"}' not found.");
                        return false;
                    }
                    return ShowInternal(redirect, redirect.ScreenKey, true, redirectDepth + 1, null);
                }
            }

            var activeShell = target.usesShell ? FindShell(target.shellKey) : null;
            if (target.usesShell && activeShell == null)
            {
                Debug.LogWarning($"[FigForge] FrameManager: screen '{target.ScreenKey}' requires shell '{target.shellKey}', but no matching shell is registered.");
                return false;
            }
            // Claim a generation after all early-return guards: any nested Show triggered
            // during the hide loop (or shell SetVisible) below bumps _showGen past ours.
            int gen = ++_showGen;
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
            // If a nested Show ran during the hide loop / shell work above (e.g. from an
            // OnHide handler), it bumped _showGen and already committed ITS target. Bail
            // without touching Current/visibility so we don't clobber the innermost nav.
            if (_showGen != gen) return true;
            // Assign Current BEFORE SetVisible(true): SetVisible fires OnShow(), and if a
            // handler navigates again re-entrantly, that inner call must win. Setting Current
            // first means the inner navigation's result isn't clobbered when we unwind here.
            Current = target;
            target.SetVisible(true);
            if (shell != null) shell.SetActive(target.usesShell && activeShell == null);
            return true;
        }

        // ---- navigation guards (preconditions) ----------------------------------------
        // Checked before Show switches frames. Guards live on THIS manager instance (not
        // static), so nothing survives a domain reload. Public entry: FigForgeNavigation
        // and the generated Frames.Guard helpers.
        readonly Dictionary<string, List<NavGuard>> _guards = new Dictionary<string, List<NavGuard>>();
        readonly List<NavGuard> _globalGuards = new List<NavGuard>();
        internal event System.Action<NavContext, NavDecision> NavigationBlocked;

        internal void AddGuard(string screenKey, NavGuard guard)
        {
            if (guard == null) return;
            var key = SanitizeKey(screenKey);
            if (string.IsNullOrEmpty(key)) { _globalGuards.Add(guard); return; }
            if (!_guards.TryGetValue(key, out var list)) { list = new List<NavGuard>(); _guards[key] = list; }
            list.Add(guard);
        }

        internal void AddGuard(FigForgeFrame frame, NavGuard guard)
        {
            if (frame == null || guard == null) return;
            // A frame whose name sanitizes to empty must NOT silently become a global
            // guard (the string overload treats an empty key as global) — that would
            // apply this frame's precondition to every navigation.
            var key = SanitizeKey(frame.ScreenKey);
            if (string.IsNullOrEmpty(key))
            {
                Debug.LogWarning($"[FigForge] AddGuard: frame '{frame.name}' has no usable screen key; guard not registered. Use AddGlobalGuard for a global guard.");
                return;
            }
            AddGuard(key, guard);
        }

        internal void AddGlobalGuard(NavGuard guard) { if (guard != null) _globalGuards.Add(guard); }

        internal bool RemoveGuard(string screenKey, NavGuard guard)
            => _guards.TryGetValue(SanitizeKey(screenKey), out var list) && list != null && list.Remove(guard);

        internal bool RemoveGlobalGuard(NavGuard guard) => _globalGuards.Remove(guard);

        internal void ClearGuards() { _guards.Clear(); _globalGuards.Clear(); }

        internal void AddBlockedHandler(System.Action<NavContext, NavDecision> handler)
        { if (handler != null) NavigationBlocked += handler; }

        internal void RemoveBlockedHandler(System.Action<NavContext, NavDecision> handler)
        { if (handler != null) NavigationBlocked -= handler; }

        // Global guards first (auth / feature flags), then the destination's own guards.
        // First non-Allow verdict wins.
        NavDecision EvaluateGuards(NavContext ctx)
        {
            for (int i = 0; i < _globalGuards.Count; i++)
            {
                var d = SafeInvoke(_globalGuards[i], ctx);
                if (d.Verdict != NavVerdict.Allow) return d;
            }
            if (_guards.TryGetValue(SanitizeKey(ctx.ToKey), out var list) && list != null)
                for (int i = 0; i < list.Count; i++)
                {
                    var d = SafeInvoke(list[i], ctx);
                    if (d.Verdict != NavVerdict.Allow) return d;
                }
            return NavDecision.Allow();
        }

        // A throwing guard fails CLOSED (blocks) and is logged loudly — a buggy guard
        // should surface, not silently wave navigation through.
        static NavDecision SafeInvoke(NavGuard guard, NavContext ctx)
        {
            if (guard == null) return NavDecision.Allow();
            // A guard bound to a destroyed MonoBehaviour (e.g. a scene-collected
            // FigForgeNavGuard whose object was torn down) would throw in Evaluate and
            // fail-closed to Block, permanently locking the screen. Skip it (Allow)
            // using Unity's null-equality, which reports destroyed objects as null.
            if (guard.Target is UnityEngine.Object owner && owner == null)
                return NavDecision.Allow();
            try { return guard(ctx); }
            catch (System.Exception e)
            {
                Debug.LogError($"[FigForge] Navigation guard for '{ctx.ToKey}' threw; blocking navigation.\n{e}");
                return NavDecision.Block("A navigation guard raised an exception.");
            }
        }

        void RaiseBlocked(NavContext ctx, NavDecision decision)
        {
            var handler = NavigationBlocked;
            if (handler != null)
            {
                try { handler(ctx, decision); }
                catch (System.Exception e) { Debug.LogException(e); }
            }
            else if (!string.IsNullOrEmpty(decision.Reason))
                Debug.Log($"[FigForge] Navigation to '{ctx.ToKey}' blocked: {decision.Reason}");
        }

        // Collect FigForgeNavGuard components (including ones on hidden screens) once at
        // Start — mirrors how FigForgeNavBinder wires inactive nav links.
        void RegisterSceneGuards()
        {
#if UNITY_2023_1_OR_NEWER
            var comps = Object.FindObjectsByType<FigForgeNavGuard>(FindObjectsInactive.Include, FindObjectsSortMode.None);
#else
            var comps = Object.FindObjectsOfType<FigForgeNavGuard>(true);
#endif
            for (int i = 0; i < comps.Length; i++)
            {
                var c = comps[i];
                if (c == null) continue;
                var frame = c.TargetScreen;
                if (frame != null) AddGuard(frame, c.Evaluate);
                else AddGlobalGuard(c.Evaluate);
            }
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
