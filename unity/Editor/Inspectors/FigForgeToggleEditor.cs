// =============================================================================
// FigForge — inspector for FigForgeToggle. Mirrors FigForgeButtonEditor: keeps
// the stock Toggle UI (which uGUI registers for child classes) and surfaces the
// added `label` + `checkmark` references the inherited editor would hide.
// =============================================================================

using UnityEditor;
using UnityEditor.UI;

namespace FigForge
{
    [CustomEditor(typeof(FigForgeToggle))]
    [CanEditMultipleObjects]
    public class FigForgeToggleEditor : ToggleEditor
    {
        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI(); // the normal Toggle inspector (isOn/graphic/group/etc.)
            EditorGUILayout.Space();
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("tmpTxt_label"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("checkmark"));
            serializedObject.ApplyModifiedProperties();
        }
    }
}
