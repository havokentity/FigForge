// =============================================================================
// FigForge - Toast/Notification runtime. `Toasts.Success("Saved")` resolves an
// active FigForgeToastHost, spawns a toast item, styles it by severity, and
// auto-dismisses it after the configured duration.
// =============================================================================

using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace FigForge
{
    public enum ToastSeverity { Info, Success, Warning, Error }
    public enum ToastPosition { TopRight, TopLeft, BottomRight, BottomLeft, TopCenter, BottomCenter }

    [System.Serializable]
    public class ToastData
    {
        public string title;
        public string body;
        public ToastSeverity severity = ToastSeverity.Info;
        public ToastPosition? position;
        public float duration = -1f;
    }

    [DisallowMultipleComponent]
    [AddComponentMenu("FigForge/Controls/Toast Host")]
    public class FigForgeToastHost : MonoBehaviour
    {
        public RectTransform container;
        public FigForgeToastItem toastPrefab;
        public ToastPosition position = ToastPosition.TopRight;
        public float defaultDuration = 4f;
        public int maxVisible = 5;
        public float spacing = 8f;

        readonly List<FigForgeToastItem> _items = new List<FigForgeToastItem>();

        void Awake()
        {
            EnsureContainer();
            ApplyPosition(position);
        }

        public FigForgeToastItem Show(string title, string body = null, ToastSeverity severity = ToastSeverity.Info, float duration = -1f)
        {
            return Show(new ToastData { title = title, body = body, severity = severity, duration = duration });
        }

        public FigForgeToastItem Show(ToastData data)
        {
            if (data == null) data = new ToastData();
            EnsureContainer();
            ApplyPosition(data.position ?? position);

            while (_items.Count >= Mathf.Max(1, maxVisible))
                Dismiss(_items[0]);

            var item = CreateItem();
            item.transform.SetParent(container, false);
            item.Configure(data, this, data.duration >= 0f ? data.duration : defaultDuration);
            _items.Add(item);
            return item;
        }

        public FigForgeToastItem Info(string title, string body = null, float duration = -1f)
            => Show(title, body, ToastSeverity.Info, duration);

        public FigForgeToastItem Success(string title, string body = null, float duration = -1f)
            => Show(title, body, ToastSeverity.Success, duration);

        public FigForgeToastItem Warning(string title, string body = null, float duration = -1f)
            => Show(title, body, ToastSeverity.Warning, duration);

        public FigForgeToastItem Error(string title, string body = null, float duration = -1f)
            => Show(title, body, ToastSeverity.Error, duration);

        public void Dismiss(FigForgeToastItem item)
        {
            if (item == null) return;
            _items.Remove(item);
            if (Application.isPlaying) Destroy(item.gameObject);
            else DestroyImmediate(item.gameObject);
        }

        public void Clear()
        {
            for (int i = _items.Count - 1; i >= 0; i--)
                Dismiss(_items[i]);
            _items.Clear();
        }

        public void ApplyPosition(ToastPosition next)
        {
            position = next;
            EnsureContainer();
            if (container == null) return;

            var rt = container;
            Vector2 anchor;
            TextAnchor childAlign;
            bool top = next == ToastPosition.TopLeft || next == ToastPosition.TopCenter || next == ToastPosition.TopRight;
            switch (next)
            {
                case ToastPosition.TopLeft:
                    anchor = new Vector2(0f, 1f); childAlign = TextAnchor.UpperLeft; break;
                case ToastPosition.TopCenter:
                    anchor = new Vector2(0.5f, 1f); childAlign = TextAnchor.UpperCenter; break;
                case ToastPosition.BottomLeft:
                    anchor = new Vector2(0f, 0f); childAlign = TextAnchor.LowerLeft; break;
                case ToastPosition.BottomCenter:
                    anchor = new Vector2(0.5f, 0f); childAlign = TextAnchor.LowerCenter; break;
                case ToastPosition.BottomRight:
                    anchor = new Vector2(1f, 0f); childAlign = TextAnchor.LowerRight; break;
                default:
                    anchor = new Vector2(1f, 1f); childAlign = TextAnchor.UpperRight; break;
            }

            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = anchor;
            rt.anchoredPosition = new Vector2(anchor.x < 0.5f ? 24f : anchor.x > 0.5f ? -24f : 0f, top ? -24f : 24f);

            var layout = container.GetComponent<VerticalLayoutGroup>() ?? container.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = spacing;
            layout.childAlignment = childAlign;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
            layout.reverseArrangement = !top;

            var fitter = container.GetComponent<ContentSizeFitter>() ?? container.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        }

        void EnsureContainer()
        {
            if (container != null) return;
            var go = new GameObject("Toast Container", typeof(RectTransform));
            go.transform.SetParent(transform, false);
            container = go.GetComponent<RectTransform>();
        }

        FigForgeToastItem CreateItem()
        {
            var item = toastPrefab != null ? Instantiate(toastPrefab) : FigForgeToastItem.CreateDefault();
            item.gameObject.SetActive(true);
            return item;
        }
    }

    [DisallowMultipleComponent]
    [AddComponentMenu("FigForge/Controls/Toast Item")]
    public class FigForgeToastItem : MonoBehaviour
    {
        public TMP_Text tmpTxt_title;
        public TMP_Text tmpTxt_body;
        public Graphic background;
        public Graphic accent;
        public Button closeButton;
        public CanvasGroup canvasGroup;

        FigForgeToastHost _host;
        Coroutine _dismiss;

        public void Configure(ToastData data, FigForgeToastHost host, float duration)
        {
            _host = host;
            if (canvasGroup == null) canvasGroup = gameObject.GetComponent<CanvasGroup>() ?? gameObject.AddComponent<CanvasGroup>();
            if (tmpTxt_title != null) tmpTxt_title.text = data.title ?? "";
            if (tmpTxt_body != null)
            {
                tmpTxt_body.text = data.body ?? "";
                tmpTxt_body.gameObject.SetActive(!string.IsNullOrEmpty(data.body));
            }
            ApplySeverity(data.severity);
            if (closeButton != null)
            {
                closeButton.onClick.RemoveListener(Dismiss);
                closeButton.onClick.AddListener(Dismiss);
            }
            if (_dismiss != null) StopCoroutine(_dismiss);
            if (duration > 0f) _dismiss = StartCoroutine(AutoDismiss(duration));
        }

        public void Dismiss()
        {
            if (_host != null) _host.Dismiss(this);
            else if (Application.isPlaying) Destroy(gameObject);
            else DestroyImmediate(gameObject);
        }

        IEnumerator AutoDismiss(float duration)
        {
            yield return new WaitForSeconds(duration);
            Dismiss();
        }

        public void ApplySeverity(ToastSeverity severity)
        {
            var color = SeverityColor(severity);
            var baseBg = new Color(0.07f, 0.075f, 0.09f, 0.96f);
            if (accent != null) accent.color = color;
            if (background != null)
            {
                // The accent strip carries the severity colour. With no accent graphic on
                // the prefab, fall back to a subtle severity tint of the dark background so
                // the severity is still conveyed (instead of every toast looking identical).
                background.color = accent != null ? baseBg : Color.Lerp(baseBg, color, 0.12f);
            }
        }

        public static FigForgeToastItem CreateDefault()
        {
            var root = new GameObject("Toast", typeof(RectTransform), typeof(CanvasGroup), typeof(Image));
            var rt = root.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(320f, 88f);

            var bg = root.GetComponent<Image>();
            bg.color = new Color(0.07f, 0.075f, 0.09f, 0.96f);

            var layout = root.AddComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(0, 12, 12, 12);
            layout.spacing = 12f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;

            var accentGo = new GameObject("Accent", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
            accentGo.transform.SetParent(root.transform, false);
            var accentLayout = accentGo.GetComponent<LayoutElement>();
            accentLayout.preferredWidth = 4f;
            accentLayout.minWidth = 4f;
            var accent = accentGo.GetComponent<Image>();

            var contentGo = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(LayoutElement));
            contentGo.transform.SetParent(root.transform, false);
            var contentLayout = contentGo.GetComponent<VerticalLayoutGroup>();
            contentLayout.spacing = 2f;
            contentLayout.childControlHeight = true;
            contentLayout.childControlWidth = true;
            contentLayout.childForceExpandHeight = false;
            var contentLe = contentGo.GetComponent<LayoutElement>();
            contentLe.preferredWidth = 256f;
            contentLe.flexibleWidth = 1f;

            var title = AddText(contentGo.transform, "Title", 15f, FontStyles.Bold, Color.white);
            var body = AddText(contentGo.transform, "Body", 13f, FontStyles.Normal, new Color(0.82f, 0.84f, 0.88f, 1f));

            var closeGo = new GameObject("Close", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
            closeGo.transform.SetParent(root.transform, false);
            closeGo.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.001f);
            var closeLe = closeGo.GetComponent<LayoutElement>();
            closeLe.preferredWidth = 24f;
            closeLe.preferredHeight = 24f;
            var closeLabel = AddText(closeGo.transform, "Label", 18f, FontStyles.Normal, new Color(0.82f, 0.84f, 0.88f, 1f));
            closeLabel.text = "x";
            closeLabel.alignment = TextAlignmentOptions.Center;
            Stretch(closeLabel.rectTransform);

            var item = root.AddComponent<FigForgeToastItem>();
            item.tmpTxt_title = title;
            item.tmpTxt_body = body;
            item.background = bg;
            item.accent = accent;
            item.closeButton = closeGo.GetComponent<Button>();
            item.canvasGroup = root.GetComponent<CanvasGroup>();
            return item;
        }

        static TextMeshProUGUI AddText(Transform parent, string name, float size, FontStyles style, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            var tmp = go.GetComponent<TextMeshProUGUI>();
            tmp.text = "";
            tmp.fontSize = size;
            tmp.fontStyle = style;
            tmp.color = color;
            tmp.raycastTarget = false;
            tmp.textWrappingMode = TextWrappingModes.Normal;
            tmp.overflowMode = TextOverflowModes.Ellipsis;
            return tmp;
        }

        static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        static Color SeverityColor(ToastSeverity severity)
        {
            switch (severity)
            {
                case ToastSeverity.Success: return new Color(0.18f, 0.78f, 0.42f, 1f);
                case ToastSeverity.Warning: return new Color(1f, 0.68f, 0.22f, 1f);
                case ToastSeverity.Error: return new Color(0.95f, 0.22f, 0.22f, 1f);
                default: return new Color(0.32f, 0.62f, 1f, 1f);
            }
        }
    }

    public static class Toasts
    {
        static FigForgeToastHost _host;

        public static FigForgeToastHost Host
        {
            get
            {
                if (_host == null) _host = ResolveHost();
                return _host;
            }
            set => _host = value;
        }

        public static FigForgeToastItem Show(string title, string body = null, ToastSeverity severity = ToastSeverity.Info, float duration = -1f)
            => Host.Show(title, body, severity, duration);

        public static FigForgeToastItem Show(ToastData data)
            => Host.Show(data);

        public static FigForgeToastItem Info(string title, string body = null, float duration = -1f)
            => Host.Info(title, body, duration);

        public static FigForgeToastItem Success(string title, string body = null, float duration = -1f)
            => Host.Success(title, body, duration);

        public static FigForgeToastItem Warning(string title, string body = null, float duration = -1f)
            => Host.Warning(title, body, duration);

        public static FigForgeToastItem Error(string title, string body = null, float duration = -1f)
            => Host.Error(title, body, duration);

        public static void Clear()
            => Host.Clear();

        static FigForgeToastHost ResolveHost()
        {
#if UNITY_2023_1_OR_NEWER
            var existing = Object.FindFirstObjectByType<FigForgeToastHost>(FindObjectsInactive.Include);
#else
            var existing = Object.FindObjectOfType<FigForgeToastHost>(true);
#endif
            if (existing != null) return existing;

            EnsureEventSystem();

            // Toasts belong to the scene's FigForge canvas: the host lives under
            // it (as a top-sorted nested canvas) so it shares the canvas' scaler
            // and is torn down with the scene. Nothing persists across loads — a
            // different scene with its own FigForge canvas gets its own host.
            var figForgeCanvas = FindFigForgeCanvas();
            if (figForgeCanvas != null)
            {
                var hostGo = new GameObject("FigForge Toasts", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster));
                var rt = hostGo.GetComponent<RectTransform>();
                rt.SetParent(figForgeCanvas.transform, false);
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
                var nested = hostGo.GetComponent<Canvas>();
                nested.overrideSorting = true;
                nested.sortingOrder = 32760;
                return hostGo.AddComponent<FigForgeToastHost>();
            }

            // No FigForge canvas in the scene — fall back to a scene-local overlay
            // canvas (still scene-scoped, never persisted).
            var canvasGo = new GameObject("FigForge Toasts", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32760;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            return canvasGo.AddComponent<FigForgeToastHost>();
        }

        static Canvas FindFigForgeCanvas()
        {
#if UNITY_2023_1_OR_NEWER
            var helper = Object.FindFirstObjectByType<FigForgeCanvasHelper>(FindObjectsInactive.Include);
#else
            var helper = Object.FindObjectOfType<FigForgeCanvasHelper>(true);
#endif
            if (helper == null) return null;
            var canvas = helper.GetComponentInParent<Canvas>();
            if (canvas == null) return null;
            return canvas.rootCanvas != null ? canvas.rootCanvas : canvas;
        }

        static void EnsureEventSystem()
        {
#if UNITY_2023_1_OR_NEWER
            if (Object.FindFirstObjectByType<EventSystem>() != null) return;
#else
            if (Object.FindObjectOfType<EventSystem>() != null) return;
#endif
            // Scene-scoped: do NOT persist across scene loads, so each scene
            // manages its own EventSystem alongside its FigForge canvas.
            new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
        }
    }
}
