// =============================================================================
// FigForge — Stepper control. Owns a numeric TMP input plus minus/plus buttons.
// =============================================================================

using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FigForge
{
    [AddComponentMenu("FigForge/Controls/Stepper")]
    [DisallowMultipleComponent]
    public class FigForgeStepper : MonoBehaviour
    {
        public TMP_InputField input;
        public Button minusButton;
        public Button plusButton;

        public float minValue = 0f;
        public float maxValue = 100f;
        public float step = 1f;

        [SerializeField] float m_Value;

        public float Value
        {
            get => m_Value;
            set => SetValue(value, true);
        }

        public bool isVisible
        {
            get => gameObject.activeSelf;
            set => gameObject.SetActive(value);
        }

        void OnEnable()
        {
            if (minusButton != null) minusButton.onClick.AddListener(Decrement);
            if (plusButton != null) plusButton.onClick.AddListener(Increment);
            if (input != null) input.onEndEdit.AddListener(OnInputEndEdit);
            RefreshText();
        }

        void OnDisable()
        {
            if (minusButton != null) minusButton.onClick.RemoveListener(Decrement);
            if (plusButton != null) plusButton.onClick.RemoveListener(Increment);
            if (input != null) input.onEndEdit.RemoveListener(OnInputEndEdit);
        }

#if UNITY_EDITOR
        void OnValidate()
        {
            if (maxValue < minValue) maxValue = minValue;
            if (step <= 0f) step = 1f;
            SetValue(m_Value, false);
        }
#endif

        public void SetValueWithoutNotify(float value) => SetValue(value, false);

        public void SetValueFromString(string value, bool sendCallback = false)
        {
            if (FigForgeInputField.TryGetNumericValue(value, out var parsed))
                SetValue(parsed, sendCallback);
            else
                RefreshText();
        }

        void Increment() => SetValue(m_Value + Mathf.Max(0.0001f, step), true);
        void Decrement() => SetValue(m_Value - Mathf.Max(0.0001f, step), true);

        void OnInputEndEdit(string text) => SetValueFromString(text, true);

        void SetValue(float value, bool sendCallback)
        {
            float clamped = Mathf.Clamp(value, minValue, maxValue);
            if (step > 0f)
                clamped = minValue + Mathf.Round((clamped - minValue) / step) * step;
            clamped = Mathf.Clamp(clamped, minValue, maxValue);
            bool changed = !Mathf.Approximately(m_Value, clamped);
            m_Value = clamped;
            RefreshText();
            if (changed && sendCallback)
            {
                // Reserved for future event surface; input text already reflects value.
            }
        }

        void RefreshText()
        {
            if (input == null) return;
            input.SetTextWithoutNotify(Format(m_Value));
        }

        string Format(float value)
        {
            bool wholeStep = Mathf.Approximately(step, Mathf.Round(step));
            return value.ToString(wholeStep ? "0" : "0.###", CultureInfo.InvariantCulture);
        }
    }
}
