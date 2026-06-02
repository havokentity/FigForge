using UnityEditor;
using UnityEngine;

namespace FigForge
{
    [CustomPropertyDrawer(typeof(FigForgeFill))]
    public class FigForgeFillDrawer : PropertyDrawer
    {
        const float Gap = 2f;
        const float ToggleWidth = 18f;
        const float ModeWidth = 118f;
        static readonly GUIContent[] ModeLabels =
        {
            new GUIContent("Solid Paint"),
            new GUIContent("Linear Grad"),
            new GUIContent("Radial Grad"),
            new GUIContent("Angular Grad"),
            new GUIContent("Diamond Grad"),
        };

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            return EditorGUIUtility.singleLineHeight;
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            var kind = property.FindPropertyRelative("kind");
            var disabled = property.FindPropertyRelative("disabled");
            var color = property.FindPropertyRelative("color");
            var gradientKind = property.FindPropertyRelative("gradientKind");
            var gradient = property.FindPropertyRelative("gradient");

            var line = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);
            var labelRect = new Rect(line.x, line.y, EditorGUIUtility.labelWidth - ToggleWidth, line.height);
            var toggleRect = new Rect(labelRect.xMax, line.y, ToggleWidth, line.height);
            var controlRect = new Rect(line.x + EditorGUIUtility.labelWidth, line.y,
                line.width - EditorGUIUtility.labelWidth, line.height);
            float modeWidth = Mathf.Min(ModeWidth, Mathf.Max(56f, controlRect.width * 0.45f));
            var popupRect = new Rect(controlRect.xMax - modeWidth, controlRect.y, modeWidth, controlRect.height);
            var swatchRect = new Rect(controlRect.x, controlRect.y, Mathf.Max(32f, controlRect.width - modeWidth - Gap), controlRect.height);

            EditorGUI.LabelField(labelRect, label);
            bool fillEnabled = !disabled.boolValue;
            fillEnabled = EditorGUI.Toggle(toggleRect, fillEnabled);
            disabled.boolValue = !fillEnabled;

            using (new EditorGUI.DisabledScope(!fillEnabled))
            {
                float oldLabelWidth = EditorGUIUtility.labelWidth;
                EditorGUIUtility.labelWidth = 0f;
                if (kind.enumValueIndex == (int)FigForgeFillKind.Gradient)
                {
                    if (gradient.gradientValue == null)
                        gradient.gradientValue = DefaultGradient(color.colorValue);
                    gradient.gradientValue = EditorGUI.GradientField(swatchRect, gradient.gradientValue);
                }
                else
                {
                    color.colorValue = EditorGUI.ColorField(swatchRect, GUIContent.none, color.colorValue, false, true, false);
                }
                EditorGUIUtility.labelWidth = oldLabelWidth;
            }

            int mode = ModeIndex(kind, gradientKind);
            using (new EditorGUI.DisabledScope(!fillEnabled))
            {
                EditorGUI.BeginChangeCheck();
                mode = EditorGUI.Popup(popupRect, mode, ModeLabels);
                if (EditorGUI.EndChangeCheck())
                {
                    ApplyMode(mode, kind, gradientKind);
                    if (mode > 0 && gradient.gradientValue == null)
                        gradient.gradientValue = DefaultGradient(color.colorValue);
                }
            }

            EditorGUI.EndProperty();
        }

        static int ModeIndex(SerializedProperty kind, SerializedProperty gradientKind)
        {
            if (kind.enumValueIndex != (int)FigForgeFillKind.Gradient) return 0;
            return Mathf.Clamp(gradientKind.enumValueIndex + 1, 1, ModeLabels.Length - 1);
        }

        static void ApplyMode(int mode, SerializedProperty kind, SerializedProperty gradientKind)
        {
            if (mode <= 0)
            {
                kind.enumValueIndex = (int)FigForgeFillKind.Solid;
                gradientKind.enumValueIndex = (int)FigForgeGradientKind.Linear;
            }
            else
            {
                kind.enumValueIndex = (int)FigForgeFillKind.Gradient;
                gradientKind.enumValueIndex = mode - 1;
            }
        }

        static Gradient DefaultGradient(Color color)
        {
            var g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(new Color(color.r, color.g, color.b, 1f), 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(color.a, 0f), new GradientAlphaKey(1f, 1f) });
            return g;
        }
    }
}
