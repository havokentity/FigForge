using UnityEditor;
using UnityEngine;

namespace FigForge
{
    [CustomPropertyDrawer(typeof(FigForgeEffectLayer))]
    public class FigForgeEffectLayerDrawer : PropertyDrawer
    {
        const float Gap = 2f;
        const float ToggleWidth = 18f;
        const float KindWidth = 112f;

        static readonly GUIContent[] KindLabels =
        {
            new GUIContent("Drop Shadow"),
            new GUIContent("Inner Shadow"),
            new GUIContent("Layer Blur"),
        };

        static readonly GUIContent[] BlurModeLabels =
        {
            new GUIContent("Uniform"),
            new GUIContent("Progressive"),
        };

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
            => EditorGUIUtility.singleLineHeight * 3f + Gap * 2f;

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            var enabled = property.FindPropertyRelative("enabled");
            var kind = property.FindPropertyRelative("kind");
            var color = property.FindPropertyRelative("color");
            var offset = property.FindPropertyRelative("offset");
            var blur = property.FindPropertyRelative("blur");
            var spread = property.FindPropertyRelative("spread");
            var blurMode = property.FindPropertyRelative("blurMode");
            var endBlur = property.FindPropertyRelative("endBlur");

            var row = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);
            var labelRect = new Rect(row.x, row.y, EditorGUIUtility.labelWidth - ToggleWidth, row.height);
            var toggleRect = new Rect(labelRect.xMax, row.y, ToggleWidth, row.height);
            var controlRect = new Rect(row.x + EditorGUIUtility.labelWidth, row.y,
                row.width - EditorGUIUtility.labelWidth, row.height);
            var kindRect = new Rect(controlRect.x, controlRect.y, Mathf.Min(KindWidth, controlRect.width * 0.45f), controlRect.height);
            var colorRect = new Rect(kindRect.xMax + Gap, controlRect.y,
                Mathf.Max(32f, controlRect.xMax - kindRect.xMax - Gap), controlRect.height);

            EditorGUI.LabelField(labelRect, label);
            EditorGUI.showMixedValue = enabled.hasMultipleDifferentValues;
            EditorGUI.BeginChangeCheck();
            bool nextEnabled = EditorGUI.Toggle(toggleRect, enabled.boolValue);
            EditorGUI.showMixedValue = false;
            if (EditorGUI.EndChangeCheck())
            {
                enabled.boolValue = nextEnabled;
                if (nextEnabled) SeedVisibleEffect(kind, color, offset, blur, endBlur);
            }

            using (new EditorGUI.DisabledScope(!enabled.boolValue))
            {
                EditorGUI.showMixedValue = kind.hasMultipleDifferentValues;
                EditorGUI.BeginChangeCheck();
                int nextKind = EditorGUI.Popup(kindRect, Mathf.Clamp(kind.enumValueIndex, 0, KindLabels.Length - 1), KindLabels);
                if (EditorGUI.EndChangeCheck())
                {
                    kind.enumValueIndex = nextKind;
                    SeedVisibleEffect(kind, color, offset, blur, endBlur);
                }
                EditorGUI.showMixedValue = false;

                var row2 = new Rect(position.x, row.yMax + Gap, position.width, row.height);
                var row3 = new Rect(position.x, row2.yMax + Gap, position.width, row.height);
                if ((FigForgeEffectKind)kind.enumValueIndex == FigForgeEffectKind.LayerBlur)
                {
                    var modeRect = new Rect(colorRect.x, colorRect.y, colorRect.width, colorRect.height);
                    EditorGUI.showMixedValue = blurMode.hasMultipleDifferentValues;
                    EditorGUI.BeginChangeCheck();
                    int nextBlurMode = EditorGUI.Popup(modeRect, Mathf.Clamp(blurMode.enumValueIndex, 0, 1), BlurModeLabels);
                    if (EditorGUI.EndChangeCheck())
                        blurMode.enumValueIndex = nextBlurMode;
                    EditorGUI.showMixedValue = false;
                    if ((FigForgeLayerBlurMode)blurMode.enumValueIndex == FigForgeLayerBlurMode.Progressive)
                    {
                        EditorGUI.PropertyField(row2, blur, new GUIContent("Start"));
                        EditorGUI.PropertyField(row3, endBlur, new GUIContent("End"));
                    }
                    else
                    {
                        EditorGUI.PropertyField(row2, blur, new GUIContent("Blur"));
                    }
                }
                else
                {
                    float oldLabelWidth = EditorGUIUtility.labelWidth;
                    EditorGUIUtility.labelWidth = 0f;
                    EditorGUI.showMixedValue = color.hasMultipleDifferentValues;
                    EditorGUI.BeginChangeCheck();
                    var nextColor = EditorGUI.ColorField(colorRect, GUIContent.none, color.colorValue, false, true, true);
                    if (EditorGUI.EndChangeCheck())
                        color.colorValue = nextColor;
                    EditorGUI.showMixedValue = false;
                    EditorGUIUtility.labelWidth = oldLabelWidth;

                    EditorGUI.PropertyField(row2, offset, new GUIContent("Offset"));
                    float half = (row3.width - EditorGUIUtility.labelWidth - Gap) * 0.5f;
                    var blurRect = new Rect(row3.x, row3.y, EditorGUIUtility.labelWidth + half, row3.height);
                    var spreadRect = new Rect(blurRect.xMax + Gap, row3.y, half, row3.height);
                    EditorGUI.PropertyField(blurRect, blur, new GUIContent("Blur"));
                    EditorGUIUtility.labelWidth = 48f;
                    EditorGUI.PropertyField(spreadRect, spread, new GUIContent("Spread"));
                    EditorGUIUtility.labelWidth = oldLabelWidth;
                }
            }

            EditorGUI.EndProperty();
        }

        static void SeedVisibleEffect(SerializedProperty kind, SerializedProperty color, SerializedProperty offset,
                                      SerializedProperty blur, SerializedProperty endBlur)
        {
            if ((FigForgeEffectKind)kind.enumValueIndex == FigForgeEffectKind.LayerBlur)
            {
                if (blur.floatValue <= 0.001f) blur.floatValue = 8f;
                if (endBlur.floatValue <= 0.001f) endBlur.floatValue = blur.floatValue;
                return;
            }

            if (color.colorValue.a <= 0.001f) color.colorValue = new Color(0f, 0f, 0f, 0.25f);
            if (offset.vector2Value == Vector2.zero) offset.vector2Value = new Vector2(0f, 4f);
            if (blur.floatValue <= 0.001f) blur.floatValue = 8f;
            if (endBlur.floatValue <= 0.001f) endBlur.floatValue = blur.floatValue;
        }
    }
}
