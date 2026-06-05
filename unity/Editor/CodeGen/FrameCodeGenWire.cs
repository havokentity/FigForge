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
        [DidReloadScripts]
        static void OnScriptsReloaded()
        {
            // Defer a tick so the freshly compiled types + the scene are fully settled
            // before we mutate components.
            EditorApplication.delayCall += UpgradePendingFrames;
        }

        static void UpgradePendingFrames()
        {
            var frames = Resources.FindObjectsOfTypeAll<FigForgeFrame>();
            bool any = false;
            foreach (var f in frames)
            {
                if (f == null) continue;
                if (f.GetType() != typeof(FigForgeFrame)) continue;   // already the generated subclass
                if (string.IsNullOrEmpty(f.generatedType)) continue;  // no generated frame for this page
                if (!f.gameObject.scene.IsValid()) continue;          // skip prefab assets / non-scene objects

                var t = ResolveType(f.generatedType);
                if (t == null || !typeof(FigForgeFrame).IsAssignableFrom(t)) continue; // not compiled yet

                if (UpgradeFrame(f, t)) any = true;
            }
            if (any)
                EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        }

        static bool UpgradeFrame(FigForgeFrame baseFrame, Type t)
        {
            var go = baseFrame.gameObject;
            string screenName = baseFrame.screenName;
            bool usesShell = baseFrame.usesShell;
            string genType = baseFrame.generatedType;
            var reg = go.GetComponent<FigForgeScreen>();
            var mgr = baseFrame.GetComponentInParent<FrameManager>();
            int idx = mgr != null ? mgr.screens.IndexOf(baseFrame) : -1;

            // FigForgeFrame is [DisallowMultipleComponent], so the base must go before the
            // subclass can be added.
            UnityEngine.Object.DestroyImmediate(baseFrame);
            if (!(go.AddComponent(t) is FigForgeFrame comp)) return false;

            comp.screenName = screenName;
            comp.usesShell = usesShell;
            comp.generatedType = genType;
            comp.__WireFrame(reg);

            if (mgr != null)
            {
                if (idx >= 0 && idx < mgr.screens.Count) mgr.screens[idx] = comp;
                else if (!mgr.screens.Contains(comp)) mgr.Register(comp);
                EditorUtility.SetDirty(mgr);
            }
            EditorUtility.SetDirty(comp);
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
