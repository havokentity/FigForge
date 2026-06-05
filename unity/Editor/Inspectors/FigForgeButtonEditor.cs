// =============================================================================
// FigForge — inspector for FigForgeButton. uGUI's ButtonEditor is registered for
// child classes ([CustomEditor(typeof(Button), true)]), so without this a
// FigForgeButton renders as a plain Button and its added `label` ref is hidden.
// This passthrough keeps the full Button UI and surfaces our extra field.
// =============================================================================

using UnityEditor;
using UnityEditor.UI;

namespace FigForge
{
    [CustomEditor(typeof(FigForgeButton))]
    [CanEditMultipleObjects]
    public class FigForgeButtonEditor : ButtonEditor
    {
        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI(); // the normal Button inspector (interactable/transition/nav/onClick)
            EditorGUILayout.Space();
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("tmpTxt_label"));
            serializedObject.ApplyModifiedProperties();
        }
    }
}
