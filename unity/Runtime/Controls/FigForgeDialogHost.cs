// =============================================================================
// FigForge — global dialog locator. A registry of the overlay-layer FigForgeModals
// (those tagged with a dialogKey by the importer), so the generated `Dialogs.X`
// accessors can open a dialog from anywhere without a frame reference.
//
// Mirrors FigForgeToastHost: resolves lazily and is scene-scoped — nothing persists
// across loads. Unlike the toast host it renders nothing (the dialogs live in their
// authored Overlay layer); this is a pure locator, so it only materializes at runtime
// the first time a dialog is actually resolved. Screen-local modals (empty dialogKey)
// are deliberately ignored — reach those through their frame.
// =============================================================================

using System.Collections.Generic;
using UnityEngine;

namespace FigForge
{
    [DisallowMultipleComponent]
    public class FigForgeDialogHost : MonoBehaviour
    {
        readonly Dictionary<string, FigForgeModal> _byKey = new Dictionary<string, FigForgeModal>();
        bool _collected;

        static FigForgeDialogHost _host;

        /// <summary>The active host. Lazily created at runtime on first access; returns null in
        /// edit mode when none exists (so generated accessors resolve to null instead of
        /// mutating the open scene). The generated `Dialogs.X` accessors call through here.</summary>
        public static FigForgeDialogHost Resolve()
        {
            if (_host != null) return _host;
#if UNITY_2023_1_OR_NEWER
            _host = Object.FindFirstObjectByType<FigForgeDialogHost>();
#else
            _host = Object.FindObjectOfType<FigForgeDialogHost>();
#endif
            if (_host == null && Application.isPlaying)
                _host = new GameObject("FigForge Dialogs").AddComponent<FigForgeDialogHost>();
            return _host;
        }

        void OnDestroy() { if (_host == this) _host = null; }

        // Scan the scene for tagged dialogs. Inactive included: dialogs self-hide at
        // Awake (FigForgeModal), so they're inactive by the time anything opens one.
        void Collect()
        {
            _byKey.Clear();
#if UNITY_2023_1_OR_NEWER
            var modals = Object.FindObjectsByType<FigForgeModal>(FindObjectsInactive.Include, FindObjectsSortMode.None);
#else
            var modals = Object.FindObjectsOfType<FigForgeModal>(true);
#endif
            foreach (var modal in modals)
            {
                if (modal == null || string.IsNullOrEmpty(modal.dialogKey)) continue;
                if (!_byKey.ContainsKey(modal.dialogKey)) _byKey[modal.dialogKey] = modal;
                else Debug.LogWarning($"[FigForge] Dialogs: duplicate dialog key '{modal.dialogKey}' — only the first is addressable.");
            }
            _collected = true;
        }

        /// <summary>The global dialog registered under this key, or null. A miss re-scans once
        /// in case a dialog was instantiated after the first lookup.</summary>
        public FigForgeModal Find(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            if (!_collected) Collect();
            if (_byKey.TryGetValue(key, out var m) && m != null) return m;
            Collect();
            return _byKey.TryGetValue(key, out var again) ? again : null;
        }

        public bool Open(string key)
        {
            var m = Find(key);
            if (m == null) { WarnMissing(key); return false; }
            m.Open();
            return true;
        }

        public bool Open(string key, ModalData data)
        {
            var m = Find(key);
            if (m == null) { WarnMissing(key); return false; }
            m.Open(data);
            return true;
        }

        public void CloseAll()
        {
            if (!_collected) Collect();
            foreach (var kv in _byKey)
                if (kv.Value != null) kv.Value.Close();
        }

        static void WarnMissing(string key)
            => Debug.LogWarning($"[FigForge] Dialogs: no dialog '{key}' found — is its Overlay layer in the scene?");
    }
}
