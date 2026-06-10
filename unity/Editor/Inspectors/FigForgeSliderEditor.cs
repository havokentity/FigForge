// =============================================================================
// FigForge — inspector for FigForgeSlider. Mirrors FigForgeToggleEditor: keeps
// the stock Slider UI (which uGUI registers for child classes) and surfaces the
// added `tmpTxt_label` reference the inherited editor would hide.
// =============================================================================

using UnityEditor;
using UnityEditor.UI;

namespace FigForge
{
    [CustomEditor(typeof(FigForgeSlider))]
    [CanEditMultipleObjects]
    public class FigForgeSliderEditor : SliderEditor
    {
        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI(); // the normal Slider inspector (value/direction/fill/handle/etc.)
            EditorGUILayout.Space();
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("tmpTxt_label"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("tmpTxt_value"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Slots"));
            serializedObject.ApplyModifiedProperties();
        }
    }
}
