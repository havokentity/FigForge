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
            EditorGUI.BeginChangeCheck();
            bool nextEnabled = EditorGUI.Toggle(toggleRect, enabled.boolValue);
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
                color.colorValue = EditorGUI.ColorField(colorRect, GUIContent.none, color.colorValue, false, true, false);
                EditorGUIUtility.labelWidth = oldLabelWidth;
                style.enumValueIndex = EditorGUI.Popup(styleRect, Mathf.Clamp(style.enumValueIndex, 0, 1), StyleLabels);
                int alignMode = AlignMode(align.enumValueIndex);
                alignMode = EditorGUI.Popup(alignRect, alignMode, AlignLabels);
                align.enumValueIndex = AlignEnumValue(alignMode);
                weight.floatValue = Mathf.Max(0f, EditorGUI.FloatField(weightRect, weight.floatValue));
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
