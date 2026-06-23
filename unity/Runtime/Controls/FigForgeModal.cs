// =============================================================================
// FigForge - Modal/Dialog control. Owns the usual dialog parts (backdrop, panel,
// title/body/actions, close button) and gives generated frame code a small typed
// API: dialog.Open(), dialog.Close(), dialog.SetContent(...).
// =============================================================================

using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace FigForge
{
    [System.Serializable]
    public class ModalData
    {
        public string title;
        public string body;
        public string primaryText;
        public string secondaryText;
        public string tertiaryText;
        public ModalButtons? buttons;    // which action buttons to show (null = leave as-is)
        public bool? showClose;          // show/hide the corner ✕ (null = leave as-is)
        public bool? closeOnBackdrop;    // dismiss on backdrop click (null = leave as-is)
        public System.Action<FigForgeModal> onPrimary;    // primary click handler — receives the modal (null = leave existing)
        public System.Action<FigForgeModal> onSecondary;  // secondary click handler (null = leave existing)
        public System.Action<FigForgeModal> onTertiary;   // tertiary click handler (null = leave existing)
    }

    /// <summary>Which of a modal's designed action buttons to show — a script-level modal
    /// "type" à la VB's MessageBoxButtons. Labels still come from your Figma design.</summary>
    public enum ModalButtons
    {
        Primary,                   // just the emphasized action
        PrimarySecondary,          // primary + secondary (default)
        PrimarySecondaryTertiary,  // all three
    }

    [DisallowMultipleComponent]
    [AddComponentMenu("FigForge/Controls/Modal")]
    public class FigForgeModal : MonoBehaviour
    {
        [Header("Parts")]
        public RectTransform backdrop;
        public RectTransform panel;
        public RectTransform actions;
        public TMP_Text tmpTxt_title;
        public TMP_Text tmpTxt_body;
        public Button primaryButton;
        public Button secondaryButton;
        public Button tertiaryButton;
        public Button closeButton;
        public Button backdropButton;

        [Header("Behavior")]
        public bool startOpen;
        [Tooltip("Click outside the panel (on the backdrop) to dismiss. OFF by default — the user must " +
                 "pick an action or the close button. Turn on for a light dismiss-anywhere dialog.")]
        public bool closeOnBackdrop = false;
        public bool closeOnEscape = true;

        [ReadOnly]
        [Tooltip("Set by the importer for dialogs in an Overlay layer. When non-empty this modal is a " +
                 "GLOBAL dialog, registered in FigForgeDialogHost under this key and reachable as Dialogs.<Name>. " +
                 "Empty = a screen-local modal (reach it via its frame instead).")]
        public string dialogKey;

        [Header("Buttons")]
        [SerializeField]
        [Tooltip("Which action buttons to show — like VB's MessageBoxButtons. The labels stay as drawn in Figma.")]
        ModalButtons _buttons = ModalButtons.PrimarySecondary;

        /// <summary>Which action buttons are shown (script-level modal "type"). Setting it
        /// re-applies visibility immediately; the action row reflows.</summary>
        public ModalButtons Buttons
        {
            get => _buttons;
            set { _buttons = value; ApplyButtonVisibility(); }
        }

        [SerializeField]
        [Tooltip("Show the corner ✕ close button.")]
        bool _showClose = true;

        /// <summary>Whether the corner close (✕) button is shown. Setting it re-applies immediately.</summary>
        public bool ShowClose
        {
            get => _showClose;
            set { _showClose = value; ApplyButtonVisibility(); }
        }

        public UnityEvent onOpened = new UnityEvent();
        public UnityEvent onClosed = new UnityEvent();
        public UnityEvent onPrimary = new UnityEvent();
        public UnityEvent onSecondary = new UnityEvent();
        public UnityEvent onTertiary = new UnityEvent();

        bool _bound;

        // The last ModalData-installed closure for each action, so Bind* can remove ONLY its own
        // previously-added listener instead of wiping Inspector/user-added listeners.
        UnityAction _modalPrimary;
        UnityAction _modalSecondary;
        UnityAction _modalTertiary;

        // Per-instance content stack: opening the SAME modal again pushes the current state
        // and shows the new one; each close peels back (LIFO) until empty, then dismisses —
        // so one GameObject "stacks" without cloning a second instance.
        ModalData _current;
        readonly System.Collections.Generic.List<ModalData> _contentStack =
            new System.Collections.Generic.List<ModalData>();

        // Matches the modal stack's liveness test (activeInHierarchy): a modal under an
        // inactive parent isn't reachable/visible, so it must not report as open.
        public bool IsOpen => gameObject.activeInHierarchy;

        // ---- modal stack (LIFO) ------------------------------------------------------
        // Opening pushes; closing pops. The top modal is the interactive one (its backdrop
        // covers the ones beneath); Escape and CloseTop() act only on the top.
        static readonly System.Collections.Generic.List<FigForgeModal> _stack =
            new System.Collections.Generic.List<FigForgeModal>();
        static int _escapeFrame = -1;

        /// <summary>The top (most-recently-opened, still-open) modal, or null. Prunes any
        /// destroyed entries (e.g. stale statics when "domain reload on play" is off) and any
        /// modal whose GameObject was deactivated out from under it — e.g. its parent frame was
        /// SetActive(false) by navigation, so Close()/OnDestroy never fired and it would otherwise
        /// stay a phantom "Top".</summary>
        public static FigForgeModal Top
        {
            get
            {
                for (int i = _stack.Count - 1; i >= 0; i--)
                {
                    var m = _stack[i];
                    if (m != null && m.gameObject.activeInHierarchy) return m;
                    _stack.RemoveAt(i);
                }
                return null;
            }
        }

        // Drop destroyed or deactivated entries so OpenCount/Top/CloseTop agree on what's
        // actually live and visible. (A modal hidden via a parent SetActive(false) never runs
        // Close(), so it lingers in _stack until pruned here.)
        static void Prune()
        {
            for (int i = _stack.Count - 1; i >= 0; i--)
            {
                var m = _stack[i];
                if (m == null || !m.gameObject.activeInHierarchy) _stack.RemoveAt(i);
            }
        }

        /// <summary>How many modals are currently open.</summary>
        public static int OpenCount
        {
            get { Prune(); return _stack.Count; }
        }

        /// <summary>Close just the top modal (LIFO). Returns true if one was closed.</summary>
        public static bool CloseTop()
        {
            var t = Top;
            if (t == null) return false;
            t.Close();
            return true;
        }

        public string Title
        {
            get => tmpTxt_title != null ? tmpTxt_title.text : null;
            set { if (tmpTxt_title != null) tmpTxt_title.text = value ?? ""; }
        }

        public string Body
        {
            get => tmpTxt_body != null ? tmpTxt_body.text : null;
            set { if (tmpTxt_body != null) tmpTxt_body.text = value ?? ""; }
        }

        public bool isVisible
        {
            get => gameObject.activeSelf;
            set { if (value) Open(); else Close(); }
        }

        void Awake()
        {
            BindDefaultButtons();
            ApplyButtonVisibility();
            if (!startOpen) gameObject.SetActive(false);
        }

        void Update()
        {
            // Only the top modal reacts to Escape, and only once per frame, so a single
            // press closes one modal (LIFO) rather than cascading through the stack.
            if (closeOnEscape && this == Top && _escapeFrame != Time.frameCount && EscapePressedThisFrame())
            {
                _escapeFrame = Time.frameCount;
                Close();
            }
        }

        void OnDestroy() => _stack.Remove(this);

        // Works under BOTH input backends. The legacy Input.GetKeyDown THROWS every frame
        // when the project's Active Input Handling is "Input System Package" (legacy off),
        // so each path is gated by Unity's built-in input defines.
        static bool EscapePressedThisFrame()
        {
#if ENABLE_INPUT_SYSTEM
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null && kb.escapeKey.wasPressedThisFrame) return true;
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(KeyCode.Escape)) return true;
#endif
            return false;
        }

#if UNITY_EDITOR
        // Live-preview the Show toggles in the Scene view when edited in the Inspector
        // (deferred — SetActive isn't allowed directly inside OnValidate).
        void OnValidate()
        {
            if (Application.isPlaying) return;
            UnityEditor.EditorApplication.delayCall += () => { if (this != null) ApplyButtonVisibility(); };
        }
#endif

        /// <summary>Apply the Show Primary/Secondary/Tertiary toggles to the built buttons.
        /// The action row's HorizontalLayoutGroup reflows automatically; absent buttons are
        /// skipped. Called on Awake and every Open; call it yourself after flipping a flag at runtime.</summary>
        public void ApplyButtonVisibility()
        {
            if (primaryButton != null) primaryButton.gameObject.SetActive(true);
            if (secondaryButton != null) secondaryButton.gameObject.SetActive(_buttons != ModalButtons.Primary);
            if (tertiaryButton != null) tertiaryButton.gameObject.SetActive(_buttons == ModalButtons.PrimarySecondaryTertiary);
            if (closeButton != null) closeButton.gameObject.SetActive(_showClose);
        }

        public void Open()
        {
            BindDefaultButtons();
            ApplyButtonVisibility();
            if (!gameObject.activeSelf) gameObject.SetActive(true);
            _stack.Remove(this);            // de-dup if re-opened
            _stack.Add(this);               // push: now the top
            // Render above earlier modals — but only within THIS modal's own parent. Sibling
            // order doesn't reorder across different parents/Canvases; correct cross-parent
            // stacking would need per-modal sorting (e.g. an overlay Canvas with sortingOrder,
            // like the toast host). The importer keeps stacked modals under one parent, so the
            // single-parent assumption holds for generated output.
            transform.SetAsLastSibling();
            onOpened.Invoke();
        }

        public void Open(ModalData data)
        {
            // Re-opening an already-open modal stacks its content (LIFO) instead of replacing.
            if (IsOpen && _current != null) _contentStack.Add(_current);
            _current = data;
            Apply(data);
            Open();
        }

        public void Close()
        {
            // Same modal opened more than once → peel back to the previous content (LIFO)
            // instead of dismissing. No cloning: one GameObject replays its stacked states.
            if (_contentStack.Count > 0)
            {
                _current = _contentStack[_contentStack.Count - 1];
                _contentStack.RemoveAt(_contentStack.Count - 1);
                Apply(_current);
                return;
            }
            _current = null;
            _stack.Remove(this);            // pop the global stack (no-op if not open)
            if (!gameObject.activeSelf) return;
            gameObject.SetActive(false);
            onClosed.Invoke();
        }

        /// <summary>Fully dismiss — drop any stacked content and close in one step,
        /// instead of peeling back level by level.</summary>
        public void Dismiss()
        {
            _contentStack.Clear();
            Close();
        }

        public void SetContent(string title, string body)
        {
            Title = title;
            Body = body;
        }

        public void Apply(ModalData data)
        {
            if (data == null) return;
            if (data.title != null) Title = data.title;
            if (data.body != null) Body = data.body;
            SetButtonLabel(primaryButton, data.primaryText);
            SetButtonLabel(secondaryButton, data.secondaryText);
            SetButtonLabel(tertiaryButton, data.tertiaryText);
            if (data.buttons.HasValue) _buttons = data.buttons.Value;
            if (data.showClose.HasValue) _showClose = data.showClose.Value;
            if (data.closeOnBackdrop.HasValue) closeOnBackdrop = data.closeOnBackdrop.Value;
            if (data.onPrimary != null) { var cb = data.onPrimary; BindPrimary(() => cb(this)); }
            if (data.onSecondary != null) { var cb = data.onSecondary; BindSecondary(() => cb(this)); }
            if (data.onTertiary != null) { var cb = data.onTertiary; BindTertiary(() => cb(this)); }
            ApplyButtonVisibility();
        }

        public void BindClose(Button button)
        {
            if (button == null) return;
            button.onClick.RemoveListener(Close);
            button.onClick.AddListener(Close);
        }

        public void BindPrimary(UnityAction action)
        {
            // Remove ONLY our own previously-installed closure, not Inspector/user listeners.
            if (_modalPrimary != null) onPrimary.RemoveListener(_modalPrimary);
            _modalPrimary = action;
            if (action != null) onPrimary.AddListener(action);
        }

        public void BindSecondary(UnityAction action)
        {
            if (_modalSecondary != null) onSecondary.RemoveListener(_modalSecondary);
            _modalSecondary = action;
            if (action != null) onSecondary.AddListener(action);
        }

        public void BindTertiary(UnityAction action)
        {
            if (_modalTertiary != null) onTertiary.RemoveListener(_modalTertiary);
            _modalTertiary = action;
            if (action != null) onTertiary.AddListener(action);
        }

        void BindDefaultButtons()
        {
            if (_bound) return;
            _bound = true;

            BindClose(closeButton);
            if (backdropButton != null)
            {
                backdropButton.onClick.RemoveListener(OnBackdropClicked);
                backdropButton.onClick.AddListener(OnBackdropClicked);
            }
            if (primaryButton != null)
            {
                primaryButton.onClick.RemoveListener(OnPrimaryClicked);
                primaryButton.onClick.AddListener(OnPrimaryClicked);
            }
            if (secondaryButton != null)
            {
                secondaryButton.onClick.RemoveListener(OnSecondaryClicked);
                secondaryButton.onClick.AddListener(OnSecondaryClicked);
            }
            if (tertiaryButton != null)
            {
                tertiaryButton.onClick.RemoveListener(OnTertiaryClicked);
                tertiaryButton.onClick.AddListener(OnTertiaryClicked);
            }
        }

        void OnBackdropClicked()
        {
            if (closeOnBackdrop) Close();
        }

        void OnPrimaryClicked()
        {
            onPrimary.Invoke();
        }

        void OnSecondaryClicked()
        {
            onSecondary.Invoke();
        }

        void OnTertiaryClicked()
        {
            onTertiary.Invoke();
        }

        static void SetButtonLabel(Button button, string label)
        {
            if (button == null || label == null) return;
            var fig = button as FigForgeButton;
            if (fig != null) fig.Label = label;
            else
            {
                var tmp = button.GetComponentInChildren<TMP_Text>(true);
                if (tmp != null) tmp.text = label;
            }
        }
    }
}
