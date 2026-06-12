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
        [Tooltip("If true this screen mounts inside the persistent Shell's content slot; the Shell stays visible. If false it's full-screen and the Shell hides.")]
        public bool usesShell;

        [Tooltip("Full name of the importer-generated FigForgeFrame subclass for this page " +
                 "(e.g. FigForge.Generated.LaunchPage). The editor swaps this plain base for that " +
                 "subclass + wires its typed refs once it compiles. Empty = no generated frame.")]
        [ReadOnly]
        public string generatedType;

        bool _bound;

        public bool IsVisible => gameObject.activeSelf;
        public bool IsBound => _bound;
        public string ScreenKey => name;

        // Typed navigation: Frames.Settings.Show() — shows this frame (hiding the rest)
        // via the active manager. Internal string key is never exposed to callers.
        public void Show()
        {
            var m = FrameManager.Resolve();
            if (m != null) m.Show(this);
        }

        public virtual void OnShow() { }
        public virtual void OnHide() { }

        // Called once by FrameManager before runtime visibility changes hide inactive
        // pages. Add per-frame listener wiring here instead of Start/OnEnable when the
        // frame may begin hidden.
        public virtual void OnBind() { }

        // Populate the generated [SerializeField] refs from the frame's element registry.
        // Overridden by the generated subclass (strongly typed); no-op on the base.
        public virtual void __WireFrame(FigForgeScreen reg) { }

        protected internal T __Get<T>(ref T field, string name) where T : Component
        {
            if (field == null)
                field = GetComponent<FigForgeScreen>()?.Get<T>(name);
            return field;
        }

        internal void BindOnce()
        {
            if (_bound) return;
            _bound = true;
            __WireFrame(GetComponent<FigForgeScreen>());
            OnBind();
        }

        internal void SetVisible(bool visible)
        {
            BindOnce();
            if (gameObject.activeSelf == visible) return;
            gameObject.SetActive(visible);
            if (visible) OnShow();
            else OnHide();
        }
    }
}
