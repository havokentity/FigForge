// =============================================================================
// FigForge — turns captured navigation into behaviour. At Start it finds every
// FigForgeNavLink under it and wires the nearest Button's onClick to
// ScreenManager.Show(targetScreen). This is the one place where the deferred
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
        public ScreenManager screenManager;

        void Start()
        {
            if (screenManager == null)
                screenManager = GetComponentInParent<ScreenManager>();
#if UNITY_2023_1_OR_NEWER
            if (screenManager == null)
                screenManager = Object.FindFirstObjectByType<ScreenManager>();
            var links = Object.FindObjectsByType<FigForgeNavLink>(FindObjectsSortMode.None);
#else
            if (screenManager == null)
                screenManager = Object.FindObjectOfType<ScreenManager>();
            var links = Object.FindObjectsOfType<FigForgeNavLink>();
#endif
            if (screenManager == null) { Debug.LogWarning("[FigForge] NavBinder: no ScreenManager found."); return; }

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
