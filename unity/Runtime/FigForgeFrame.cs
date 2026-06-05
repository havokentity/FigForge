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

        [Tooltip("Full name of the importer-generated FigForgeFrame subclass for this page " +
                 "(e.g. FigForge.Generated.LaunchPage). The editor swaps this plain base for that " +
                 "subclass + wires its typed refs once it compiles. Empty = no generated frame.")]
        public string generatedType;

        public bool IsVisible => gameObject.activeSelf;

        // Typed navigation: Frames.Settings.Show() — shows this frame (hiding the rest)
        // via the active manager. Internal string key is never exposed to callers.
        public void Show()
        {
            if (FrameManager.Active != null) FrameManager.Active.Show(screenName);
        }

        public virtual void OnShow() { }
        public virtual void OnHide() { }

        // Populate the generated [SerializeField] refs from the frame's element registry.
        // Overridden by the generated subclass (strongly typed); no-op on the base.
        public virtual void __WireFrame(FigForgeScreen reg) { }

        internal void SetVisible(bool visible)
        {
            if (gameObject.activeSelf == visible) return;
            gameObject.SetActive(visible);
            if (visible) OnShow();
            else OnHide();
        }
    }
}
