// =============================================================================
// FigForge — ScreenManager. Sits on the shared Canvas and shows exactly one
// BaseScreen page at a time, giving a "connected" multi-page app from several
// imported Figma frames.
// =============================================================================

using System.Collections.Generic;
using UnityEngine;

namespace FigForge
{
    [DisallowMultipleComponent]
    public class ScreenManager : MonoBehaviour
    {
        [Tooltip("All pages this manager controls (children with a BaseScreen).")]
        public List<BaseScreen> screens = new List<BaseScreen>();

        [Tooltip("screenName shown on Start. Empty = first registered screen.")]
        public string initialScreen;

        public BaseScreen Current { get; private set; }

        void Start()
        {
            if (screens.Count == 0) return;
            if (!string.IsNullOrEmpty(initialScreen)) Show(initialScreen);
            else Show(screens[0].screenName);
        }

        public void Register(BaseScreen screen)
        {
            if (screen != null && !screens.Contains(screen)) screens.Add(screen);
        }

        public bool Show(string screenName)
        {
            BaseScreen target = null;
            foreach (var s in screens)
            {
                if (s == null) continue;
                bool match = s.screenName == screenName;
                s.SetVisible(match);
                if (match) target = s;
            }
            if (target != null) Current = target;
            else Debug.LogWarning($"[FigForge] ScreenManager: no screen named '{screenName}'.");
            return target != null;
        }
    }
}
