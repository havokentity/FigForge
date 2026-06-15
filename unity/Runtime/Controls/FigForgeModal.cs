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
        public bool? closeOnBackdrop;
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
        public Button closeButton;
        public Button backdropButton;

        [Header("Behavior")]
        public bool startOpen;
        public bool closeOnBackdrop = true;
        public bool closeOnEscape = true;

        public UnityEvent onOpened = new UnityEvent();
        public UnityEvent onClosed = new UnityEvent();
        public UnityEvent onPrimary = new UnityEvent();
        public UnityEvent onSecondary = new UnityEvent();

        bool _bound;

        public bool IsOpen => gameObject.activeSelf;

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
            if (!startOpen) gameObject.SetActive(false);
        }

        void Update()
        {
            if (closeOnEscape && Input.GetKeyDown(KeyCode.Escape)) Close();
        }

        public void Open()
        {
            BindDefaultButtons();
            if (!gameObject.activeSelf) gameObject.SetActive(true);
            onOpened.Invoke();
        }

        public void Open(ModalData data)
        {
            Apply(data);
            Open();
        }

        public void Close()
        {
            if (!gameObject.activeSelf) return;
            gameObject.SetActive(false);
            onClosed.Invoke();
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
            if (data.closeOnBackdrop.HasValue) closeOnBackdrop = data.closeOnBackdrop.Value;
        }

        public void BindClose(Button button)
        {
            if (button == null) return;
            button.onClick.RemoveListener(Close);
            button.onClick.AddListener(Close);
        }

        public void BindPrimary(UnityAction action)
        {
            onPrimary.RemoveAllListeners();
            if (action != null) onPrimary.AddListener(action);
        }

        public void BindSecondary(UnityAction action)
        {
            onSecondary.RemoveAllListeners();
            if (action != null) onSecondary.AddListener(action);
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
