using UnityEditor;
using UnityEngine;

namespace FigForge
{
    [CustomPropertyDrawer(typeof(FigForgeStrokeLayer))]
    public class FigForgeStrokeLayerDrawer : PropertyDrawer
    {
        const float Gap = 2f;
        const float ToggleWidth = 18f;
        const float StyleWidth = 104f;
        const float AlignWidth = 82f;
        const float WeightWidth = 64f;
        const float DashFieldWidth = 74f;

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
            var style = property.FindPropertyRelative("style");
            int lines = style != null && style.enumValueIndex == (int)FigForgeStrokeStyle.Dashed ? 3 : 2;
            return EditorGUIUtility.singleLineHeight * lines + Gap * (lines - 1);
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            var enabled = property.FindPropertyRelative("enabled");
            var style = property.FindPropertyRelative("style");
            var paint = property.FindPropertyRelative("paint");
            var weight = property.FindPropertyRelative("weight");
            var align = property.FindPropertyRelative("align");
            var dash = property.FindPropertyRelative("dash");
            var gap = property.FindPropertyRelative("gap");

            var row = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);
            var labelRect = new Rect(row.x, row.y, EditorGUIUtility.labelWidth - ToggleWidth, row.height);
            var toggleRect = new Rect(labelRect.xMax, row.y, ToggleWidth, row.height);
            var controlRect = new Rect(row.x + EditorGUIUtility.labelWidth, row.y,
                row.width - EditorGUIUtility.labelWidth, row.height);

            EditorGUI.LabelField(labelRect, label);
            EditorGUI.BeginChangeCheck();
            bool nextEnabled = EditorGUI.Toggle(toggleRect, enabled.boolValue);
            if (EditorGUI.EndChangeCheck())
            {
                enabled.boolValue = nextEnabled;
                if (nextEnabled && weight.floatValue <= 0.001f) weight.floatValue = 1f;
            }

            using (new EditorGUI.DisabledScope(!enabled.boolValue))
            {
                float weightWidth = Mathf.Min(WeightWidth, Mathf.Max(42f, controlRect.width * 0.22f));
                float alignWidth = Mathf.Min(AlignWidth, Mathf.Max(58f, controlRect.width * 0.3f));
                float styleWidth = Mathf.Min(StyleWidth, Mathf.Max(58f, controlRect.width - alignWidth - weightWidth - 2f * Gap));
                var styleRect = new Rect(controlRect.x, controlRect.y, styleWidth, controlRect.height);
                var alignRect = new Rect(styleRect.xMax + Gap, controlRect.y, alignWidth, controlRect.height);
                var weightRect = new Rect(alignRect.xMax + Gap, controlRect.y, weightWidth, controlRect.height);

                style.enumValueIndex = EditorGUI.Popup(styleRect, Mathf.Clamp(style.enumValueIndex, 0, 1), StyleLabels);
                int alignMode = AlignMode(align.enumValueIndex);
                alignMode = EditorGUI.Popup(alignRect, alignMode, AlignLabels);
                align.enumValueIndex = AlignEnumValue(alignMode);
                weight.floatValue = Mathf.Max(0f, EditorGUI.FloatField(weightRect, weight.floatValue));

                var paintRect = new Rect(position.x, row.yMax + Gap, position.width, EditorGUIUtility.singleLineHeight);
                EditorGUI.PropertyField(paintRect, paint, new GUIContent("Paint"), false);

                if (style.enumValueIndex == (int)FigForgeStrokeStyle.Dashed)
                {
                    var dashRect = new Rect(position.x + EditorGUIUtility.labelWidth, paintRect.yMax + Gap,
                        Mathf.Min(DashFieldWidth, Mathf.Max(48f, position.width - EditorGUIUtility.labelWidth)), EditorGUIUtility.singleLineHeight);
                    var gapRect = new Rect(dashRect.xMax + Gap, dashRect.y,
                        Mathf.Min(DashFieldWidth, Mathf.Max(48f, position.xMax - dashRect.xMax - Gap)), dashRect.height);
                    var dashLabel = new Rect(position.x, dashRect.y, EditorGUIUtility.labelWidth - ToggleWidth, dashRect.height);
                    EditorGUI.LabelField(dashLabel, "Dash");
                    dash.floatValue = Mathf.Max(0f, EditorGUI.FloatField(dashRect, dash.floatValue));
                    EditorGUI.LabelField(new Rect(gapRect.x, gapRect.y, 28f, gapRect.height), "Gap");
                    gap.floatValue = Mathf.Max(0f, EditorGUI.FloatField(new Rect(gapRect.x + 30f, gapRect.y, Mathf.Max(28f, gapRect.width - 30f), gapRect.height), gap.floatValue));
                }
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
