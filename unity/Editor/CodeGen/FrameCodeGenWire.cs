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
        // a rebuilt page would sit on the base FigForgeFrame forever (Frames.X casts fail,
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
            var frames = Resources.FindObjectsOfTypeAll<FigForgeFrame>();
            bool any = false;
            var managers = new System.Collections.Generic.HashSet<FrameManager>();
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

        static bool UpgradeFrame(FigForgeFrame baseFrame, Type t, out FrameManager manager, out FigForgeFrame upgraded)
        {
            manager = null;
            upgraded = null;
            var go = baseFrame.gameObject;
            bool isShell = baseFrame.isShell;
            bool usesShell = baseFrame.usesShell;
            string shellKey = baseFrame.shellKey;
            string genType = baseFrame.generatedType;
            var reg = go.GetComponent<FigForgeScreen>();
            var mgr = baseFrame.GetComponentInParent<FrameManager>();
            int idx = mgr != null ? mgr.screens.IndexOf(baseFrame) : -1;
            bool wasInitial = mgr != null && mgr.initialScreen == baseFrame;

            // FigForgeFrame is [DisallowMultipleComponent], so the base must go before the
            // subclass can be added.
            UnityEngine.Object.DestroyImmediate(baseFrame);
            if (!(go.AddComponent(t) is FigForgeFrame comp)) return false;

            comp.isShell = isShell;
            comp.usesShell = usesShell;
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
