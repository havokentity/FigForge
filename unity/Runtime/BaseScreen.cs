// =============================================================================
// FigForge — BaseScreen. Each imported Figma page becomes one BaseScreen under
// a shared Canvas; the ScreenManager toggles them. Subclass to add per-screen
// behaviour, or use as-is for a plain page.
// =============================================================================

using UnityEngine;

namespace FigForge
{
    [DisallowMultipleComponent]
    public class BaseScreen : MonoBehaviour
    {
        [Tooltip("Unique key used by ScreenManager.Show(name).")]
        public string screenName;

        public bool IsVisible => gameObject.activeSelf;

        public virtual void OnShow() { }
        public virtual void OnHide() { }

        internal void SetVisible(bool visible)
        {
            if (gameObject.activeSelf == visible) return;
            gameObject.SetActive(visible);
            if (visible) OnShow();
            else OnHide();
        }
    }
}
