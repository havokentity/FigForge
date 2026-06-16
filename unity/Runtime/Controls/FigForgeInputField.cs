// =============================================================================
// FigForge — Input field control. A standard TMP_InputField (text, placeholder,
// caret, validation all inherited) under FigForge ownership. `placeholderText`
// surfaces the placeholder as a TMP_Text for convenient read/skin; `text` is the
// inherited value. Numeric helpers understand unit-suffixed values ("45°",
// "12px", "80%") and preserve the surrounding text when incrementing.
// =============================================================================

using System.Globalization;
using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;

namespace FigForge
{
    [AddComponentMenu("FigForge/Controls/Input Field")]
    [DisallowMultipleComponent]
    public class FigForgeInputField : TMP_InputField
    {
        static readonly Regex NumberPattern = new Regex(
            @"[-+]?(?:(?:\d{1,3}(?:,\d{3})+|\d+)(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?",
            RegexOptions.CultureInvariant);

        struct NumericText
        {
            public string prefix;
            public string suffix;
            public float value;
            public int decimalPlaces;
        }

        [Tooltip("Default amount used by IncrementValue() and DecrementValue(). Negative values are allowed, but usually use DecrementValue for clarity.")]
        public float incrementStep = 1f;

        /// <summary>The placeholder as a TMP_Text (TMP_InputField.placeholder typed). Null if not a TMP_Text.</summary>
        public TMP_Text tmpTxt_placeholder => placeholder as TMP_Text;

        /// <summary>The placeholder text — `input.Placeholder = "Email"`. The field's value is the
        /// inherited `text`. No-op if the placeholder isn't a TMP_Text.</summary>
        public string Placeholder
        {
            get => tmpTxt_placeholder != null ? tmpTxt_placeholder.text : null;
            set { var t = tmpTxt_placeholder; if (t != null) t.text = value; }
        }

        /// <summary>Show/hide the whole control — `input.isVisible = false`. Drives
        /// GameObject.SetActive, so a hidden control stops rendering, receiving input,
        /// and contributing to layout.</summary>
        public bool isVisible
        {
            get => gameObject.activeSelf;
            set => gameObject.SetActive(value);
        }

        /// <summary>Parse the first number in the field text. Accepts unit/prefix text like
        /// "45°", "12 px", "$10.50", or "opacity 80%".</summary>
        public bool TryGetNumericValue(out float value)
        {
            return TryGetNumericValue(text, out value);
        }

        /// <summary>Parse the first number in arbitrary text, allowing prefixes/suffixes and units.</summary>
        public static bool TryGetNumericValue(string rawText, out float value)
        {
            if (TryParseNumericText(rawText, out var parsed))
            {
                value = parsed.value;
                return true;
            }
            value = 0f;
            return false;
        }

        /// <summary>Increment the first number in arbitrary text and return the rewritten string.</summary>
        public static bool TryIncrementText(string rawText, float delta, out string result)
        {
            if (!TryParseNumericText(rawText, out var parsed))
            {
                result = rawText;
                return false;
            }
            result = BuildNumericText(parsed, parsed.value + delta, DecimalPlaces(delta));
            return true;
        }

        /// <summary>Replace the first number in arbitrary text and return the rewritten string.</summary>
        public static bool TrySetNumericText(string rawText, float value, out string result)
        {
            if (!TryParseNumericText(rawText, out var parsed))
            {
                result = rawText;
                return false;
            }
            result = BuildNumericText(parsed, value, DecimalPlaces(value));
            return true;
        }

        /// <summary>Increment the first number in the field by <see cref="incrementStep"/>.
        /// Surrounding symbols/units are preserved.</summary>
        public bool IncrementValue() => IncrementValue(incrementStep);

        /// <summary>Decrement the first number in the field by <see cref="incrementStep"/>.
        /// Surrounding symbols/units are preserved.</summary>
        public bool DecrementValue() => IncrementValue(-incrementStep);

        /// <summary>UnityEvent-friendly wrapper for incrementing by <see cref="incrementStep"/>.</summary>
        public void Increment() => IncrementValue(incrementStep);

        /// <summary>UnityEvent-friendly wrapper for decrementing by <see cref="incrementStep"/>.</summary>
        public void Decrement() => IncrementValue(-incrementStep);

        /// <summary>UnityEvent-friendly wrapper for incrementing by an Inspector-supplied amount.</summary>
        public void IncrementBy(float delta) => IncrementValue(delta);

        /// <summary>UnityEvent-friendly wrapper for decrementing by an Inspector-supplied amount.</summary>
        public void DecrementBy(float amount) => IncrementValue(-amount);

        /// <summary>Increment the first number in the field by <paramref name="delta"/>.
        /// Use a negative delta to decrement. Returns false if no number is present.</summary>
        public bool IncrementValue(float delta) => IncrementValue(delta, true);

        /// <summary>Increment without invoking TMP_InputField.onValueChanged.</summary>
        public bool IncrementValueWithoutNotify(float delta) => IncrementValue(delta, false);

        /// <summary>Replace the first number while preserving surrounding symbols/units.</summary>
        public bool SetNumericValue(float value, bool sendCallback = true)
        {
            if (!TrySetNumericText(text, value, out var newText)) return false;
            ApplyText(newText, sendCallback);
            return true;
        }

        bool IncrementValue(float delta, bool sendCallback)
        {
            if (!TryIncrementText(text, delta, out var newText)) return false;
            ApplyText(newText, sendCallback);
            return true;
        }

        void ApplyText(string newText, bool sendCallback)
        {
            if (sendCallback) text = newText;
            else SetTextWithoutNotify(newText);
        }

        static string BuildNumericText(NumericText parsed, float value, int additionalDecimalPlaces)
        {
            int decimals = Mathf.Max(parsed.decimalPlaces, additionalDecimalPlaces);
            return parsed.prefix + FormatNumber(value, decimals) + parsed.suffix;
        }

        static bool TryParseNumericText(string rawText, out NumericText parsed)
        {
            parsed = default;
            if (string.IsNullOrEmpty(rawText)) return false;

            var match = NumberPattern.Match(rawText);
            if (!match.Success) return false;

            string numberText = match.Value;
            string parseText = numberText.Replace(",", "");
            if (!float.TryParse(parseText, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                return false;

            parsed.prefix = rawText.Substring(0, match.Index);
            parsed.suffix = rawText.Substring(match.Index + match.Length);
            parsed.value = value;
            parsed.decimalPlaces = DecimalPlaces(numberText);
            return true;
        }

        static int DecimalPlaces(string numberText)
        {
            int dot = numberText.IndexOf('.');
            if (dot < 0) return 0;
            int end = numberText.IndexOfAny(new[] { 'e', 'E' }, dot + 1);
            if (end < 0) end = numberText.Length;
            return Mathf.Clamp(end - dot - 1, 0, 7);
        }

        static int DecimalPlaces(float value)
        {
            string formatted = Mathf.Abs(value).ToString("0.#######", CultureInfo.InvariantCulture);
            int dot = formatted.IndexOf('.');
            return dot < 0 ? 0 : formatted.Length - dot - 1;
        }

        static string FormatNumber(float value, int decimalPlaces)
        {
            decimalPlaces = Mathf.Clamp(decimalPlaces, 0, 7);
            string format = decimalPlaces <= 0 ? "0" : "F" + decimalPlaces;
            return value.ToString(format, CultureInfo.InvariantCulture);
        }

        // --- Behaviour hooks (shared, lazily allocated; see FigForgePointerEvents).
        // onSelected/onDeselected map to the field gaining/losing edit focus.
        readonly FigForgePointerEvents _events = new FigForgePointerEvents();

        /// <summary>Pointer moved onto the field. Hover behaviour hook; the visual is captured-state driven.</summary>
        public UnityEvent onPointerEnter => _events.onPointerEnter;
        /// <summary>Pointer left the field.</summary>
        public UnityEvent onPointerExit => _events.onPointerExit;
        /// <summary>Pointer pressed down on the field (fires before caret placement).</summary>
        public UnityEvent onPointerDown => _events.onPointerDown;
        /// <summary>Pointer released over the field.</summary>
        public UnityEvent onPointerUp => _events.onPointerUp;
        /// <summary>Field gained focus / began editing (pointer, keyboard, or gamepad nav).</summary>
        public UnityEvent onSelected => _events.onSelected;
        /// <summary>Field lost focus / ended editing.</summary>
        public UnityEvent onDeselected => _events.onDeselected;

        public override void OnPointerEnter(PointerEventData eventData) { base.OnPointerEnter(eventData); _events.RaiseEnter(); }
        public override void OnPointerExit(PointerEventData eventData) { base.OnPointerExit(eventData); _events.RaiseExit(); }
        public override void OnPointerDown(PointerEventData eventData) { base.OnPointerDown(eventData); _events.RaiseDown(); }
        public override void OnPointerUp(PointerEventData eventData) { base.OnPointerUp(eventData); _events.RaiseUp(); }
        public override void OnSelect(BaseEventData eventData) { base.OnSelect(eventData); _events.RaiseSelected(); }
        public override void OnDeselect(BaseEventData eventData) { base.OnDeselect(eventData); _events.RaiseDeselected(); }
    }
}
