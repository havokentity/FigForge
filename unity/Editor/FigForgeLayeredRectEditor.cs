using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace FigForge
{
    [CustomEditor(typeof(FigForgeLayeredRect))]
    public class FigForgeLayeredRectEditor : UnityEditor.Editor
    {
        SerializedProperty script;
        SerializedProperty fills;
        SerializedProperty strokes;
        SerializedProperty effects;
        SerializedProperty appearanceOpacity;
        SerializedProperty blendMode;
        SerializedProperty compositorMode;
        SerializedProperty recipe;
        SerializedProperty corners;
        ReorderableList fillsList;
        ReorderableList strokesList;
        ReorderableList effectsList;

        void OnEnable()
        {
            script = serializedObject.FindProperty("m_Script");
            fills = serializedObject.FindProperty("fills");
            strokes = serializedObject.FindProperty("strokes");
            effects = serializedObject.FindProperty("effects");
            appearanceOpacity = serializedObject.FindProperty("appearanceOpacity");
            blendMode = serializedObject.FindProperty("blendMode");
            compositorMode = serializedObject.FindProperty("compositorMode");
            recipe = serializedObject.FindProperty("recipe");
            corners = serializedObject.FindProperty("corners");
            fillsList = MakeList(fills, "Fills", "Fill");
            strokesList = MakeList(strokes, "Strokes", "Stroke");
            effectsList = MakeList(effects, "Effects", "Effect");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            using (new EditorGUI.DisabledScope(true))
            {
                if (script != null) EditorGUILayout.PropertyField(script);
            }
            EditorGUILayout.PropertyField(corners);
            EditorGUILayout.LabelField("Appearance", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(appearanceOpacity, new GUIContent("Opacity"));
            EditorGUILayout.PropertyField(blendMode, new GUIContent("Blend Mode"));
            EditorGUILayout.PropertyField(compositorMode, new GUIContent("Compositor"));

            DrawList(fillsList, fills);
            DrawList(strokesList, strokes);
            DrawList(effectsList, effects);

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Debug", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.PropertyField(recipe);
            }

            serializedObject.ApplyModifiedProperties();
        }

        static ReorderableList MakeList(SerializedProperty property, string title, string itemLabel)
        {
            var list = new ReorderableList(property.serializedObject, property, true, true, true, true);
            list.drawHeaderCallback = rect => EditorGUI.LabelField(rect, title);
            list.elementHeightCallback = index =>
            {
                var element = property.GetArrayElementAtIndex(index);
                return EditorGUI.GetPropertyHeight(element, true) + 4f;
            };
            list.drawElementCallback = (rect, index, active, focused) =>
            {
                var element = property.GetArrayElementAtIndex(index);
                rect.y += 2f;
                rect.height = EditorGUI.GetPropertyHeight(element, true);
                EditorGUI.PropertyField(rect, element, new GUIContent($"{itemLabel} {index + 1}"), true);
            };
            if (itemLabel == "Effect")
            {
                list.onCanAddCallback = _ => property.arraySize < 9;
                list.onAddCallback = _ =>
                {
                    if (property.arraySize >= 9) return;
                    int index = property.arraySize;
                    property.arraySize++;
                    SeedEffect(property.GetArrayElementAtIndex(index));
                    property.serializedObject.ApplyModifiedProperties();
                };
            }
            return list;
        }

        static void SeedEffect(SerializedProperty element)
        {
            element.FindPropertyRelative("enabled").boolValue = true;
            element.FindPropertyRelative("kind").enumValueIndex = (int)FigForgeEffectKind.DropShadow;
            element.FindPropertyRelative("color").colorValue = new Color(0f, 0f, 0f, 0.25f);
            element.FindPropertyRelative("offset").vector2Value = new Vector2(0f, 4f);
            element.FindPropertyRelative("blur").floatValue = 8f;
            element.FindPropertyRelative("spread").floatValue = 0f;
            element.FindPropertyRelative("blurMode").enumValueIndex = (int)FigForgeLayerBlurMode.Uniform;
            element.FindPropertyRelative("endBlur").floatValue = 8f;
        }

        static void DrawList(ReorderableList list, SerializedProperty property)
        {
            if (property == null)
            {
                EditorGUILayout.HelpBox("Serialized layer data is missing.", MessageType.Warning);
                return;
            }

            list.DoLayoutList();
        }
    }
}
