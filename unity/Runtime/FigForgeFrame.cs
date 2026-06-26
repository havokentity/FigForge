// =============================================================================
// FigForge — FigForgeFrame. Each imported Figma page becomes one FigForgeFrame under
// a shared Canvas; the UIFrameManager toggles them. Subclass to add per-screen
// behaviour, or use as-is for a plain page.
// =============================================================================

using UnityEngine;

namespace FigForge
{
    [DisallowMultipleComponent]
    public class FigForgeFrame : MonoBehaviour
    {
        [Tooltip("If true this frame is persistent chrome for screens with the same Shell Key. Shell frames can also be shown directly.")]
        public bool isShell;

        [Tooltip("If true this screen mounts inside its shell's content slot; the matching shell stays visible. If false it's full-screen and shells hide.")]
        public bool usesShell;

        [Tooltip("If true this frame is NOT auto-hidden when another screen is shown — it's an " +
                 "independent overlay you control. It starts HIDDEN; reveal it with Show() / " +
                 "SetVisible(true) (which overlays the current screen WITHOUT hiding it) and take " +
                 "it down with Hide() / SetVisible(false). Preserved across re-imports.")]
        public bool persistent;

        [Tooltip("Importer shell group key. For shell frames this identifies the shell; for shell-mounted screens this identifies which shell to show.")]
        [ReadOnly]
        public string shellKey;

        [Tooltip("Full name of the importer-generated FigForgeFrame subclass for this page " +
                 "(e.g. FigForge.Generated.LaunchPage). The editor swaps this plain base for that " +
                 "subclass + wires its typed refs once it compiles. Empty = no generated frame.")]
        [ReadOnly]
        public string generatedType;

        bool _bound;

        public bool isVisible => gameObject.activeSelf;
        public bool IsBound => _bound;
        public string ScreenKey => name;

        // Typed navigation: UIFrames.Settings.Show().
        //   • A normal screen NAVIGATES — shows this frame and hides the rest.
        //   • A `persistent` frame OVERLAYS — becomes visible over the current screen
        //     without hiding it, and navigation won't auto-hide it afterward.
        public void Show()
        {
            var m = UIFrameManager.Resolve();
            if (m == null) return;
            if (persistent) m.ShowPersistent(this);
            else m.Show(this);
        }

        // Hide this frame. Meant for `persistent` overlays the user dismisses; hiding the
        // current navigated screen just leaves nothing shown (the caller's choice).
        public void Hide() => SetVisible(false);

        // Gate navigation INTO this screen: UIFrames.Settings.Guard(ctx => ...).
        public void Guard(NavGuard guard)
        {
            var m = UIFrameManager.Resolve();
            if (m != null) m.AddGuard(this, guard);
        }

        public virtual void OnShow() { }
        public virtual void OnHide() { }

        // Called once by UIFrameManager before runtime visibility changes hide inactive
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

        // Lazy, null-safe resolve for a generated collection accessor: returns the wired array,
        // filling any null slot (or a fresh array) from the registry by the element's key.
        protected internal System.Collections.Generic.IReadOnlyList<T> __GetList<T>(ref T[] field, params string[] names) where T : Component
        {
            if (field == null || field.Length != names.Length) field = new T[names.Length];
            FigForgeScreen reg = null;
            for (int i = 0; i < names.Length; i++)
            {
                if (field[i] != null) continue;
                if (reg == null) reg = GetComponent<FigForgeScreen>();
                if (reg != null) field[i] = reg.Get<T>(names[i]);
            }
            return field;
        }

        internal void BindOnce()
        {
            if (_bound) return;
            _bound = true;
            __WireFrame(GetComponent<FigForgeScreen>());
            OnBind();
        }

        // Public so consumer code can directly toggle a frame's visibility (e.g. dismiss a
        // `persistent` overlay). UIFrameManager also drives this during navigation.
        public void SetVisible(bool visible)
        {
            BindOnce();
            if (gameObject.activeSelf == visible) return;
            gameObject.SetActive(visible);
            if (visible) OnShow();
            else OnHide();
        }
    }
}
