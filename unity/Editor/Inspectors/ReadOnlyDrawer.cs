// =============================================================================
// FigForge — drawer for [ReadOnly]. Renders the field disabled (greyed) in the
// Inspector. The value is still serialized and code-settable; only manual editing
// is blocked.
// =============================================================================

using UnityEditor;
using UnityEngine;

namespace FigForge
{
    [CustomPropertyDrawer(typeof(ReadOnlyAttribute))]
    public class ReadOnlyDrawer : PropertyDrawer
    {
        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
            => EditorGUI.GetPropertyHeight(property, label, true);

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            using (new EditorGUI.DisabledScope(true))
                EditorGUI.PropertyField(position, property, label, true);
        }
    }
}
