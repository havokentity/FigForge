// =============================================================================
// FigForge — inspector for FigForgeBindings. The component is a GENERIC slot-holder
// (label / icon / control / progress / stepper / valueText / optionsTarget) the
// importer fills per control, so most slots are empty for any given control — e.g.
// a Button uses only `label` + `control`. The default inspector shows every empty
// slot, which reads as "many unbound fields" and confuses designers/devs. This
// inspector shows only the WIRED slots (plus a one-line explanation), and tucks the
// unused ones behind a foldout for the rare case you need to rewire by hand.
// =============================================================================

using UnityEditor;
using UnityEngine;

namespace FigForge
{
    [CustomEditor(typeof(FigForgeBindings))]
    [CanEditMultipleObjects]
    public class FigForgeBindingsInspector : Editor
    {
        // The object-reference slots, in declaration order (`signature` is HideInInspector).
        static readonly string[] Slots =
            { "label", "icon", "background", "control", "progress", "stepper", "valueText", "optionsTarget" };

        bool _showUnused;

        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox(
                "Wiring filled by the importer — how this control's text, icon and value get stamped. " +
                "You normally don't touch these.", MessageType.None);

            serializedObject.Update();

            int wired = 0;
            foreach (var slot in Slots)
            {
                var p = serializedObject.FindProperty(slot);
                if (p == null || p.hasMultipleDifferentValues || p.objectReferenceValue == null) continue;
                EditorGUILayout.PropertyField(p);
                wired++;
            }
            if (wired == 0 && targets.Length == 1)
                EditorGUILayout.LabelField("(no slots wired)", EditorStyles.miniLabel);

            EditorGUILayout.Space();
            _showUnused = EditorGUILayout.Foldout(_showUnused, "Unused slots", true);
            if (_showUnused)
            {
                EditorGUI.indentLevel++;
                foreach (var slot in Slots)
                {
                    var p = serializedObject.FindProperty(slot);
                    if (p == null) continue;
                    if (!p.hasMultipleDifferentValues && p.objectReferenceValue != null) continue; // already shown
                    EditorGUILayout.PropertyField(p);
                }
                EditorGUI.indentLevel--;
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
}
