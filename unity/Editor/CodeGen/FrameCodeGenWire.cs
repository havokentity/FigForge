// =============================================================================
// FigForge — post-compile frame wiring. The importer places a plain FigForgeFrame
// on each page and records the generated subclass name (frame.generatedType). Once
// that subclass compiles, this hook swaps the base component for the subclass and
// fills its typed [SerializeField] refs from the frame's element registry
// (FigForgeScreen) via the generated __WireFrame override.
//
// Runs on every script reload but is cheap + idempotent: a frame is upgraded once,
// the first reload after its generated type exists. (Scene frames only for now;
// prefab-asset frames are skipped — a follow-up.)
// =============================================================================

using System;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FigForge
{
    internal static class FrameCodeGenWire
    {
        static bool _pending;

        [DidReloadScripts]
        static void OnScriptsReloaded()
        {
            RequestUpgrade();
        }

        // The importer calls this after (re)building screens. When the generated code is
        // UNCHANGED, no compile follows the import — [DidReloadScripts] never fires — and
        // a rebuilt page would sit on the base FigForgeFrame forever (UIFrames.X casts fail,
        // resolving null). The type already exists in that case, so upgrading right away
        // works; when a compile IS pending the reload hook covers it. Idempotent.
        internal static void RequestUpgrade()
        {
            if (_pending) return;
            _pending = true;
            // Defer a tick so freshly compiled types + the scene are fully settled
            // before we mutate components.
            EditorApplication.delayCall += () => { _pending = false; UpgradePendingFrames(); };
        }

        static void UpgradePendingFrames()
        {
            // Groups first: swap each group placeholder for its generated component so a frame's
            // ref to a group component resolves when the frame wires below.
            UpgradePendingGroups();

            var frames = Resources.FindObjectsOfTypeAll<FigForgeFrame>();
            bool any = false;
            var managers = new System.Collections.Generic.HashSet<UIFrameManager>();
            var roots = new System.Collections.Generic.List<GameObject>();
            foreach (var f in frames)
            {
                if (f == null) continue;
                if (f.GetType() != typeof(FigForgeFrame)) continue;   // already the generated subclass
                if (string.IsNullOrEmpty(f.generatedType)) continue;  // no generated frame for this page
                if (!f.gameObject.scene.IsValid()) continue;          // skip prefab assets / non-scene objects

                var t = ResolveType(f.generatedType);
                if (t == null || !typeof(FigForgeFrame).IsAssignableFrom(t)) continue; // not compiled yet

                if (UpgradeFrame(f, t, out var manager, out var upgraded))
                {
                    any = true;
                    if (manager != null) managers.Add(manager);
                    if (upgraded != null) roots.Add(upgraded.gameObject);
                }
            }
            if (!any) return;

            foreach (var manager in managers)
                if (manager != null)
                    EditorUtility.SetDirty(manager);

            int batchSize = FigForgeImporterWindow.WarmUpBatchSizePref;
            foreach (var root in roots)
                HierarchyBuilder.WarmUpGeneratedGraphics(root, batchSize);

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        }

        // Swap each group placeholder (a plain FigForgeFrameElement carrying a generatedType)
        // for its generated subclass, then wire every group's child refs in a second pass —
        // once all group components exist, so a group's ref to a nested child group resolves
        // regardless of the order they were upgraded in.
        static void UpgradePendingGroups()
        {
            var elements = Resources.FindObjectsOfTypeAll<FigForgeFrameElement>();
            var upgraded = new System.Collections.Generic.List<FigForgeFrameElement>();
            foreach (var el in elements)
            {
                if (el == null) continue;
                if (el.GetType() != typeof(FigForgeFrameElement)) continue;   // already the generated subclass
                if (string.IsNullOrEmpty(el.generatedType)) continue;          // no generated group for this element
                if (!el.gameObject.scene.IsValid()) continue;                 // skip prefab assets / non-scene objects

                var t = ResolveType(el.generatedType);
                if (t == null || !typeof(FigForgeFrameElement).IsAssignableFrom(t)) continue; // not compiled yet

                var comp = UpgradeGroup(el, t);
                if (comp != null) upgraded.Add(comp);
            }
            foreach (var g in upgraded)
            {
                if (g == null) continue;
                g.__WireGroup(g.GetComponentInParent<FigForgeScreen>(true));
                EditorUtility.SetDirty(g);
            }
        }

        static FigForgeFrameElement UpgradeGroup(FigForgeFrameElement baseEl, Type t)
        {
            var go = baseEl.gameObject;
            string typeKey = baseEl.FigmaTypeKey;
            string genType = baseEl.generatedType;
            // FigForgeFrameElement is [DisallowMultipleComponent], so the base must go before the
            // subclass can be added. If the swap fails (AddComponent throws / yields an unexpected
            // type), re-add the base so the GameObject is never left WITHOUT an element component.
            UnityEngine.Object.DestroyImmediate(baseEl);
            FigForgeFrameElement comp = null;
            try { comp = go.AddComponent(t) as FigForgeFrameElement; }
            catch (Exception e) { Debug.LogException(e); }
            if (comp == null)
            {
                var restored = go.AddComponent<FigForgeFrameElement>();
                if (restored != null) { restored.ConfigureType(typeKey); restored.generatedType = genType; }
                return null;
            }
            comp.ConfigureType(typeKey);
            comp.generatedType = genType;
            return comp;
        }

        static bool UpgradeFrame(FigForgeFrame baseFrame, Type t, out UIFrameManager manager, out FigForgeFrame upgraded)
        {
            manager = null;
            upgraded = null;
            var go = baseFrame.gameObject;
            bool isShell = baseFrame.isShell;
            bool usesShell = baseFrame.usesShell;
            bool persistent = baseFrame.persistent;
            string shellKey = baseFrame.shellKey;
            string genType = baseFrame.generatedType;
            var reg = go.GetComponent<FigForgeScreen>();
            var mgr = baseFrame.GetComponentInParent<UIFrameManager>();
            int idx = mgr != null ? mgr.screens.IndexOf(baseFrame) : -1;
            bool wasInitial = mgr != null && mgr.initialScreen == baseFrame;

            // FigForgeFrame is [DisallowMultipleComponent], so the base must go before the
            // subclass can be added. If the swap fails (AddComponent throws / yields an unexpected
            // type), re-add the base so the page is never left WITHOUT a FigForgeFrame component
            // (which would orphan it from the manager and break UIFrames.X navigation).
            UnityEngine.Object.DestroyImmediate(baseFrame);
            FigForgeFrame comp = null;
            try { comp = go.AddComponent(t) as FigForgeFrame; }
            catch (Exception e) { Debug.LogException(e); }
            if (comp == null)
            {
                var restored = go.AddComponent<FigForgeFrame>();
                if (restored != null)
                {
                    restored.isShell = isShell;
                    restored.usesShell = usesShell;
                    restored.persistent = persistent;
                    restored.shellKey = shellKey;
                    restored.generatedType = genType;
                    if (mgr != null)
                    {
                        if (idx >= 0 && idx < mgr.screens.Count) mgr.screens[idx] = restored;
                        else if (!mgr.screens.Contains(restored)) mgr.Register(restored);
                        if (wasInitial) mgr.initialScreen = restored;
                        EditorUtility.SetDirty(mgr);
                    }
                }
                return false;
            }

            comp.isShell = isShell;
            comp.usesShell = usesShell;
            comp.persistent = persistent;
            comp.shellKey = shellKey;
            comp.generatedType = genType;
            comp.__WireFrame(reg);

            if (mgr != null)
            {
                if (idx >= 0 && idx < mgr.screens.Count) mgr.screens[idx] = comp;
                else if (!mgr.screens.Contains(comp)) mgr.Register(comp);
                if (wasInitial) mgr.initialScreen = comp;
                EditorUtility.SetDirty(mgr);
            }
            EditorUtility.SetDirty(comp);
            manager = mgr;
            upgraded = comp;
            return true;
        }

        static Type ResolveType(string fullName)
        {
            var t = Type.GetType(fullName);
            if (t != null) return t;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                t = asm.GetType(fullName);
                if (t != null) return t;
            }
            return null;
        }
    }
}
