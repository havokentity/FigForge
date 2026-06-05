// =============================================================================
// FigForge — FigForgeFrame. Each imported Figma page becomes one FigForgeFrame under
// a shared Canvas; the FrameManager toggles them. Subclass to add per-screen
// behaviour, or use as-is for a plain page.
// =============================================================================

using UnityEngine;

namespace FigForge
{
    [DisallowMultipleComponent]
    public class FigForgeFrame : MonoBehaviour
    {
        [Tooltip("Unique key used by FrameManager.Show(name).")]
        public string screenName;

        [Tooltip("If true this screen mounts inside the persistent Shell's content slot; the Shell stays visible. If false it's full-screen and the Shell hides.")]
        public bool usesShell;

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
