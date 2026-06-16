// =============================================================================
// FigForge - Numeric text label helper. Attach to a TMP_Text label when a plain
// label should behave like a numeric field for button/event-driven increments.
// It shares FigForgeInputField's parser, so unit-suffixed values like "45deg",
// "45°", "12px", and "80%" keep their surrounding text.
// =============================================================================

using TMPro;
using UnityEngine;

namespace FigForge
{
    [AddComponentMenu("FigForge/Controls/Numeric Text")]
    [DisallowMultipleComponent]
    public class FigForgeNumericText : MonoBehaviour
    {
        [Tooltip("Label to edit. If empty, the TMP_Text on this GameObject is used.")]
        public TMP_Text tmpTxt_target;

        [Tooltip("Default amount used by IncrementValue() and DecrementValue().")]
        public float incrementStep = 1f;

        public string Text
        {
            get => Target != null ? Target.text : null;
            set { if (Target != null) Target.text = value; }
        }

        TMP_Text Target
        {
            get
            {
                if (tmpTxt_target == null) tmpTxt_target = GetComponent<TMP_Text>();
                return tmpTxt_target;
            }
        }

        public bool TryGetNumericValue(out float value)
        {
            var target = Target;
            if (target != null) return FigForgeInputField.TryGetNumericValue(target.text, out value);
            value = 0f;
            return false;
        }

        public bool IncrementValue() => IncrementValue(incrementStep);

        public bool DecrementValue() => IncrementValue(-incrementStep);

        public void Increment() => IncrementValue(incrementStep);

        public void Decrement() => IncrementValue(-incrementStep);

        public void IncrementBy(float delta) => IncrementValue(delta);

        public void DecrementBy(float amount) => IncrementValue(-amount);

        public bool IncrementValue(float delta)
        {
            var target = Target;
            if (target == null) return false;
            if (!FigForgeInputField.TryIncrementText(target.text, delta, out var result)) return false;
            target.text = result;
            return true;
        }

        public bool SetNumericValue(float value)
        {
            var target = Target;
            if (target == null) return false;
            if (!FigForgeInputField.TrySetNumericText(target.text, value, out var result)) return false;
            target.text = result;
            return true;
        }
    }

    public static class FigForgeTextNumericExtensions
    {
        public static bool TryGetNumericValue(this TMP_Text target, out float value)
        {
            if (target != null) return FigForgeInputField.TryGetNumericValue(target.text, out value);
            value = 0f;
            return false;
        }

        public static bool IncrementValue(this TMP_Text target, float delta)
        {
            if (target == null) return false;
            if (!FigForgeInputField.TryIncrementText(target.text, delta, out var result)) return false;
            target.text = result;
            return true;
        }

        public static bool DecrementValue(this TMP_Text target, float amount)
        {
            return target.IncrementValue(-amount);
        }

        public static bool SetNumericValue(this TMP_Text target, float value)
        {
            if (target == null) return false;
            if (!FigForgeInputField.TrySetNumericText(target.text, value, out var result)) return false;
            target.text = result;
            return true;
        }
    }
}
