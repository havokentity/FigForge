// =============================================================================
// FigForge — turns captured navigation into behaviour. At Start it finds every
// FigForgeNavLink under it and wires the nearest Button's onClick to
// UIFrameManager.Show(targetScreen). This is the one place where the deferred
// "events" get added; remove this component to keep navigation inert.
// =============================================================================

using UnityEngine;
using UnityEngine.UI;

namespace FigForge
{
    [DisallowMultipleComponent]
    public class FigForgeNavBinder : MonoBehaviour
    {
        [Tooltip("Target manager. If empty, found in parents / scene at Start.")]
        [SerializeField] UIFrameManager screenManager;

        void Start()
        {
            if (screenManager == null)
                screenManager = GetComponentInParent<UIFrameManager>();
#if UNITY_2023_1_OR_NEWER
            if (screenManager == null)
                screenManager = Object.FindFirstObjectByType<UIFrameManager>();
            // Include inactive: most screens start hidden (UIFrameManager shows one
            // at a time), so their nav links must still be wired regardless of
            // Start() execution order between this and UIFrameManager.
            var links = Object.FindObjectsByType<FigForgeNavLink>(FindObjectsInactive.Include, FindObjectsSortMode.None);
#else
            if (screenManager == null)
                screenManager = Object.FindObjectOfType<UIFrameManager>();
            var links = Object.FindObjectsOfType<FigForgeNavLink>(true);
#endif
            if (screenManager == null) { Debug.LogWarning("[FigForge] NavBinder: no UIFrameManager found."); return; }

            foreach (var link in links)
            {
                if (link == null || string.IsNullOrEmpty(link.targetScreen)) continue;
                // Idempotency guard: the find is scene-global, so every binder
                // sees every link. Skip links a prior binder already wired so each
                // button's onClick gets exactly one listener (one click => one Show).
                if (link.bound) continue;
                // No ?? here: in the editor a missing Button comes back as Unity's
                // fake-null stub, which ?? treats as found — explicit == null is safe.
                var btn = link.GetComponent<Button>();
                if (btn == null) btn = link.GetComponentInChildren<Button>(true);
                if (btn == null) continue;
                var target = link.targetScreen;
                var via = link;
                btn.onClick.AddListener(() => screenManager.Show(target, via));
                link.bound = true;
            }
        }
    }
}
