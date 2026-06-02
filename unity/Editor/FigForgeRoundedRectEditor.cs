using UnityEditor;

namespace FigForge
{
    [CustomEditor(typeof(FigForgeRoundedRect))]
    public class FigForgeRoundedRectEditor : Editor
    {
        SerializedProperty script;

        void OnEnable()
        {
            script = serializedObject.FindProperty("m_Script");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            using (new EditorGUI.DisabledScope(true))
            {
                if (script != null) EditorGUILayout.PropertyField(script);
            }

            var iterator = serializedObject.GetIterator();
            bool enterChildren = true;
            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (iterator.propertyPath == "m_Script") continue;
                EditorGUILayout.PropertyField(iterator, true);
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
}
