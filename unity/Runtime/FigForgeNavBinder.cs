// =============================================================================
// FigForge — turns captured navigation into behaviour. At Start it finds every
// FigForgeNavLink under it and wires the nearest Button's onClick to
// FrameManager.Show(targetScreen). This is the one place where the deferred
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
        public FrameManager screenManager;

        void Start()
        {
            if (screenManager == null)
                screenManager = GetComponentInParent<FrameManager>();
#if UNITY_2023_1_OR_NEWER
            if (screenManager == null)
                screenManager = Object.FindFirstObjectByType<FrameManager>();
            // Include inactive: most screens start hidden (FrameManager shows one
            // at a time), so their nav links must still be wired regardless of
            // Start() execution order between this and FrameManager.
            var links = Object.FindObjectsByType<FigForgeNavLink>(FindObjectsInactive.Include, FindObjectsSortMode.None);
#else
            if (screenManager == null)
                screenManager = Object.FindObjectOfType<FrameManager>();
            var links = Object.FindObjectsOfType<FigForgeNavLink>(true);
#endif
            if (screenManager == null) { Debug.LogWarning("[FigForge] NavBinder: no FrameManager found."); return; }

            foreach (var link in links)
            {
                if (link == null || string.IsNullOrEmpty(link.targetScreen)) continue;
                var btn = link.GetComponent<Button>() ?? link.GetComponentInChildren<Button>(true);
                if (btn == null) continue;
                var target = link.targetScreen;
                btn.onClick.AddListener(() => screenManager.Show(target));
            }
        }
    }
}
