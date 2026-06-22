using UnityEditor;
using UnityEngine;

namespace FigForge
{
    [CustomPropertyDrawer(typeof(FigForgeStroke))]
    public class FigForgeStrokeDrawer : PropertyDrawer
    {
        const float Gap = 2f;
        const float ToggleWidth = 18f;
        const float StyleWidth = 104f;
        const float AlignWidth = 84f;
        const float WeightWidth = 74f;
        static readonly GUIContent[] StyleLabels =
        {
            new GUIContent("Solid Stroke"),
            new GUIContent("Dashed Stroke"),
        };

        static readonly GUIContent[] AlignLabels =
        {
            new GUIContent("Center"),
            new GUIContent("Inside"),
            new GUIContent("Outside"),
        };

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            return EditorGUIUtility.singleLineHeight;
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            var enabled = property.FindPropertyRelative("enabled");
            var style = property.FindPropertyRelative("style");
            var color = property.FindPropertyRelative("color");
            var weight = property.FindPropertyRelative("weight");
            var align = property.FindPropertyRelative("align");

            var line = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);
            var labelRect = new Rect(line.x, line.y, EditorGUIUtility.labelWidth - ToggleWidth, line.height);
            var toggleRect = new Rect(labelRect.xMax, line.y, ToggleWidth, line.height);
            var controlRect = new Rect(line.x + EditorGUIUtility.labelWidth, line.y,
                line.width - EditorGUIUtility.labelWidth, line.height);
            EditorGUI.LabelField(labelRect, label);
            EditorGUI.showMixedValue = enabled.hasMultipleDifferentValues;
            EditorGUI.BeginChangeCheck();
            bool nextEnabled = EditorGUI.Toggle(toggleRect, enabled.boolValue);
            EditorGUI.showMixedValue = false;
            if (EditorGUI.EndChangeCheck())
            {
                enabled.boolValue = nextEnabled;
                if (nextEnabled)
                {
                    if (weight.floatValue <= 0.001f) weight.floatValue = 1f;
                    if (color.colorValue.a <= 0.001f)
                    {
                        var c = color.colorValue;
                        if (c.maxColorComponent <= 0.001f) c = Color.black;
                        c.a = 1f;
                        color.colorValue = c;
                    }
                }
            }

            using (new EditorGUI.DisabledScope(!enabled.boolValue))
            {
                float weightWidth = Mathf.Min(WeightWidth, Mathf.Max(44f, controlRect.width * 0.24f));
                float alignWidth = Mathf.Min(AlignWidth, Mathf.Max(62f, controlRect.width * 0.26f));
                float styleWidth = Mathf.Min(StyleWidth, Mathf.Max(72f, controlRect.width * 0.32f));
                var colorRect = new Rect(controlRect.x, controlRect.y,
                    Mathf.Max(32f, controlRect.width - styleWidth - alignWidth - weightWidth - 3f * Gap), controlRect.height);
                var styleRect = new Rect(colorRect.xMax + Gap, controlRect.y, styleWidth, controlRect.height);
                var alignRect = new Rect(styleRect.xMax + Gap, controlRect.y, alignWidth, controlRect.height);
                var weightRect = new Rect(alignRect.xMax + Gap, controlRect.y, weightWidth, controlRect.height);
                float oldLabelWidth = EditorGUIUtility.labelWidth;
                EditorGUIUtility.labelWidth = 0f;
                EditorGUI.showMixedValue = color.hasMultipleDifferentValues;
                EditorGUI.BeginChangeCheck();
                var nextColor = EditorGUI.ColorField(colorRect, GUIContent.none, color.colorValue, false, true, false);
                if (EditorGUI.EndChangeCheck())
                    color.colorValue = nextColor;
                EditorGUI.showMixedValue = false;
                EditorGUIUtility.labelWidth = oldLabelWidth;

                EditorGUI.showMixedValue = style.hasMultipleDifferentValues;
                EditorGUI.BeginChangeCheck();
                int nextStyle = EditorGUI.Popup(styleRect, Mathf.Clamp(style.enumValueIndex, 0, 1), StyleLabels);
                if (EditorGUI.EndChangeCheck())
                    style.enumValueIndex = nextStyle;
                EditorGUI.showMixedValue = false;

                int alignMode = AlignMode(align.enumValueIndex);
                EditorGUI.showMixedValue = align.hasMultipleDifferentValues;
                EditorGUI.BeginChangeCheck();
                alignMode = EditorGUI.Popup(alignRect, alignMode, AlignLabels);
                if (EditorGUI.EndChangeCheck())
                    align.enumValueIndex = AlignEnumValue(alignMode);
                EditorGUI.showMixedValue = false;

                EditorGUI.showMixedValue = weight.hasMultipleDifferentValues;
                EditorGUI.BeginChangeCheck();
                float nextWeight = Mathf.Max(0f, EditorGUI.FloatField(weightRect, weight.floatValue));
                if (EditorGUI.EndChangeCheck())
                    weight.floatValue = nextWeight;
                EditorGUI.showMixedValue = false;
            }

            EditorGUI.EndProperty();
        }

        static int AlignMode(int enumValue)
        {
            switch ((FigForgeStrokeAlign)enumValue)
            {
                case FigForgeStrokeAlign.Center: return 0;
                case FigForgeStrokeAlign.Outside: return 2;
                default: return 1;
            }
        }

        static int AlignEnumValue(int mode)
        {
            switch (mode)
            {
                case 0: return (int)FigForgeStrokeAlign.Center;
                case 2: return (int)FigForgeStrokeAlign.Outside;
                default: return (int)FigForgeStrokeAlign.Inside;
            }
        }
    }
}
