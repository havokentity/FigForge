// =============================================================================
// FigForge — Switch control. A standard Toggle with iOS-style track/thumb visuals:
// the on tint is a Fill graphic and the Thumb slides between the track ends.
// =============================================================================

using UnityEngine;
using UnityEngine.UI;

namespace FigForge
{
    [AddComponentMenu("FigForge/Controls/Switch")]
    [DisallowMultipleComponent]
    public class FigForgeSwitch : FigForgeToggle
    {
        [Tooltip("Graphic shown when the switch is on.")]
        public Graphic fillGraphic;

        [Tooltip("The sliding thumb RectTransform.")]
        public RectTransform thumb;

        Coroutine m_Anim;

        protected override void OnEnable()
        {
            base.OnEnable();
            onValueChanged.AddListener(OnSwitchValueChanged);
            Sync(false);
        }

        protected override void OnDisable()
        {
            onValueChanged.RemoveListener(OnSwitchValueChanged);
            if (m_Anim != null) StopCoroutine(m_Anim);
            m_Anim = null;
            base.OnDisable();
        }

#if UNITY_EDITOR
        protected override void OnValidate()
        {
            base.OnValidate();
            Sync(false);
        }
#endif

        void OnSwitchValueChanged(bool _) => Sync(Application.isPlaying);

        void Sync(bool animate)
        {
            if (fillGraphic != null) fillGraphic.gameObject.SetActive(isOn);
            if (thumb == null) return;

            float target = isOn ? 1f : 0f;
            if (!animate)
            {
                SetThumb(target);
                return;
            }

            if (m_Anim != null) StopCoroutine(m_Anim);
            m_Anim = StartCoroutine(AnimateThumb(thumb.anchorMin.x, target));
        }

        System.Collections.IEnumerator AnimateThumb(float from, float to)
        {
            const float duration = 0.12f;
            float t = 0f;
            while (t < duration)
            {
                t += Time.unscaledDeltaTime;
                float u = Mathf.Clamp01(t / duration);
                u = 1f - (1f - u) * (1f - u);
                SetThumb(Mathf.Lerp(from, to, u));
                yield return null;
            }
            SetThumb(to);
            m_Anim = null;
        }

        void SetThumb(float x)
        {
            thumb.anchorMin = new Vector2(x, thumb.anchorMin.y);
            thumb.anchorMax = new Vector2(x, thumb.anchorMax.y);
            thumb.anchoredPosition = Vector2.zero;
        }
    }
}
