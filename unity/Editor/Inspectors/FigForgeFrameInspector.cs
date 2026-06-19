// =============================================================================
// FigForge — frame + group inspector.
//
// The element overview is driven straight off the GENERATED accessors: each frame
// (LaunchPage : FigForgeFrame) and each group (LaunchPage_HeaderGroup :
// FigForgeFrameElement) carries one [SerializeField] backing field per addressable
// child. We reflect those fields and draw them as a tree — a group field is typed
// as the group's own component, so we recurse into it. This makes the GENERATED
// accessor set the single source of truth (no re-deriving the codegen's scoping
// rule from the hierarchy), null refs show as "missing", and every leaf is a native
// object field (ping / drag / reassign for free).
//
// Two ref tools sit above the tree:
//   • Validate — list every accessor whose ref is null, split into "fixable" (the
//     element is still in the registry) vs "gone" (renamed/removed in Figma).
//   • Populate — re-run the generated __WireFrame/__WireGroup(reg) wiring (the exact
//     call the importer makes), scoped to this frame, this group, or the whole page.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace FigForge
{
    [CustomEditor(typeof(FigForgeFrame), true)]
    [CanEditMultipleObjects]
    public class FigForgeFrameInspector : Editor
    {
        static readonly Dictionary<int, bool> Expanded = new Dictionary<int, bool>();
        bool _showRaw;

        public override void OnInspectorGUI()
        {
            if (targets.Length > 1) { DrawDefaultInspector(); return; } // tree is single-target

            var frame = (FigForgeFrame)target;
            FigForgeRefTools.DrawFrameToolbar(frame);
            EditorGUILayout.Space();
            FigForgeAccessorTree.DrawSection(serializedObject, target, Expanded, "Frame Accessors",
                FigForgeRefTools.Hint(frame));

            EditorGUILayout.Space();
            _showRaw = EditorGUILayout.Foldout(_showRaw, "Raw serialized fields", true);
            if (_showRaw) DrawDefaultInspector();
        }
    }

    [CustomEditor(typeof(FigForgeFrameElement), true)]
    [CanEditMultipleObjects]
    public class FigForgeFrameElementInspector : Editor
    {
        static readonly Dictionary<int, bool> Expanded = new Dictionary<int, bool>();
        bool _showRaw;

        public override void OnInspectorGUI()
        {
            var el = (FigForgeFrameElement)target;
            // On a frame root this object also carries a FigForgeFrame, whose inspector already
            // draws the accessor tree — don't render a duplicate one here.
            if (targets.Length > 1 || el.GetComponent<FigForgeFrame>() != null) { DrawDefaultInspector(); return; }

            FigForgeRefTools.DrawGroupToolbar(el);
            EditorGUILayout.Space();
            FigForgeAccessorTree.DrawSection(serializedObject, target, Expanded, "Group Accessors",
                FigForgeRefTools.Hint(el));

            EditorGUILayout.Space();
            _showRaw = EditorGUILayout.Foldout(_showRaw, "Raw serialized fields", true);
            if (_showRaw) DrawDefaultInspector();
        }
    }

    // ── Tree of generated accessor refs, drawn from each component's [SerializeField]s ──
    static class FigForgeAccessorTree
    {
        // Accessor backing fields per concrete type, reflected once. The chain stops at the
        // FigForge base classes — their own fields (isShell, figmaType, …) aren't accessors.
        static readonly Dictionary<Type, FieldInfo[]> FieldCache = new Dictionary<Type, FieldInfo[]>();

        public static FieldInfo[] AccessorFields(Type t)
        {
            if (FieldCache.TryGetValue(t, out var cached)) return cached;
            var list = new List<FieldInfo>();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public
                                     | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (var cur = t; cur != null
                     && cur != typeof(FigForgeFrame) && cur != typeof(FigForgeFrameElement)
                     && cur != typeof(MonoBehaviour); cur = cur.BaseType)
            {
                foreach (var f in cur.GetFields(flags))
                {
                    if (!typeof(Component).IsAssignableFrom(f.FieldType)) continue;
                    if (!f.IsPublic && !f.IsDefined(typeof(SerializeField), false)) continue;
                    list.Add(f);
                }
            }
            var arr = list.ToArray();
            FieldCache[t] = arr;
            return arr;
        }

        public static void DrawSection(SerializedObject so, UnityEngine.Object target,
                                       Dictionary<int, bool> expanded, string title, string emptyHint)
        {
            var fields = AccessorFields(target.GetType());
            EditorGUILayout.LabelField($"{title} ({fields.Length})", EditorStyles.boldLabel);
            if (fields.Length == 0)
            {
                EditorGUILayout.HelpBox(emptyHint, MessageType.Info);
                return;
            }

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                so.Update();
                DrawFields(so, fields, expanded);
                so.ApplyModifiedProperties();
            }
        }

        // Backing field "_foo" → accessor key "foo". Strip exactly the ONE convention underscore
        // (FieldName = "_" + Key), NOT all of them: a Figma name sanitized to a leading-underscore
        // identifier (e.g. a text layer starting with a digit → "_100", backing field "__100")
        // keeps that underscore in its registry key. TrimStart('_') would drop it, so the registry
        // lookup misses and Validate reports "not fixable" even though Populate — which uses the
        // baked-in key — fixes it fine.
        public static string AccessorKey(string fieldName)
            => !string.IsNullOrEmpty(fieldName) && fieldName[0] == '_' ? fieldName.Substring(1) : fieldName;

        static void DrawFields(SerializedObject so, FieldInfo[] fields, Dictionary<int, bool> expanded)
        {
            foreach (var f in fields)
            {
                var prop = so.FindProperty(f.Name);
                if (prop == null) continue;
                string label = AccessorKey(f.Name);
                var refObj = prop.objectReferenceValue;
                bool isGroup = typeof(FigForgeFrameElement).IsAssignableFrom(f.FieldType);

                if (isGroup && refObj is Component groupComp)
                {
                    int id = refObj.GetInstanceID();
                    // Default collapsed: only an expanded group builds a child SerializedObject, so a
                    // big frame costs nothing until you drill in (this whole tree began as a perf fix).
                    bool stored = expanded.TryGetValue(id, out var e) && e;
                    bool ne = EditorGUILayout.Foldout(stored, label, true);
                    if (ne != stored) expanded[id] = ne;
                    if (ne)
                    {
                        EditorGUI.indentLevel++;
                        var childSo = new SerializedObject(groupComp);
                        childSo.Update();
                        DrawFields(childSo, AccessorFields(groupComp.GetType()), expanded);
                        childSo.ApplyModifiedProperties();
                        EditorGUI.indentLevel--;
                    }
                }
                else
                {
                    // Leaf control, or a group whose ref went null — native, editable object field.
                    var content = new GUIContent(refObj == null ? label + "   —   missing" : label);
                    EditorGUILayout.PropertyField(prop, content);
                }
            }
        }
    }

    // ── Validate / Populate, reusing the generated wiring (__WireFrame / __WireGroup) ──
    static class FigForgeRefTools
    {
        struct MissingRef { public string path; public bool fixable; } // fixable = still in the registry

        public static string Hint(FigForgeFrame f)
            => f.GetType() == typeof(FigForgeFrame)
                ? "No generated accessors yet — Forge this frame, then let scripts compile so it upgrades to its typed component."
                : "This frame exposes no accessors.";

        public static string Hint(FigForgeFrameElement e)
            => e.GetType() == typeof(FigForgeFrameElement)
                ? "No generated accessors yet — this group upgrades to its typed component once scripts compile."
                : "This group exposes no accessors.";

        public static void DrawFrameToolbar(FigForgeFrame frame)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Validate Refs"))
                {
                    var reg = frame.GetComponent<FigForgeScreen>();
                    if (Report(Validate(frame, frame.name, reg), frame.name)) PopulateFrame(frame);
                }
                if (GUILayout.Button("Populate: This Frame")) Done(PopulateFrame(frame));
                if (GUILayout.Button("Populate: Whole Page")) Done(PopulatePage(frame));
            }
        }

        public static void DrawGroupToolbar(FigForgeFrameElement el)
        {
            var reg = el.GetComponentInParent<FigForgeScreen>(true);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Validate Refs"))
                {
                    if (Report(Validate(el, el.name, reg), el.name)) PopulateElement(el, reg);
                }
                if (GUILayout.Button("Populate This Group")) Done(PopulateElement(el, reg));
            }
        }

        public static void DrawManagerToolbar(FrameManager manager)
        {
            EditorGUILayout.LabelField("FigForge Refs", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Validate Refs"))
                    ValidateManagerRefs(manager);
                if (GUILayout.Button("Populate Refs"))
                    PopulateManagerRefs(manager);
            }
        }

        // ---- scene-wide (importer window: validate / populate the whole page) ----
        public static void ValidateSceneRefs()
        {
            var frames = SceneFrames();
            var all = new List<MissingRef>();
            foreach (var f in frames)
                Collect(f, f.name, f.GetComponent<FigForgeScreen>(), all);
            if (Report(all, frames.Length + " frame(s) in scene")) PopulateSceneRefs();
        }

        public static void PopulateSceneRefs()
        {
            var frames = SceneFrames();
            int n = 0;
            foreach (var f in frames) n += PopulateFrame(f);
            EditorUtility.DisplayDialog("FigForge — Populate",
                $"Re-wired refs on {n} component(s) across {frames.Length} frame(s).", "OK");
        }

        public static void ValidateManagerRefs(FrameManager manager)
        {
            var frames = ManagerFrames(manager);
            var all = new List<MissingRef>();
            foreach (var f in frames)
                Collect(f, f.name, f.GetComponent<FigForgeScreen>(), all);
            if (Report(all, ManagerReportName(manager, frames.Length))) PopulateManagerRefs(manager);
        }

        public static void PopulateManagerRefs(FrameManager manager)
        {
            var frames = ManagerFrames(manager);
            int n = 0;
            foreach (var f in frames) n += PopulateFrame(f);
            EditorUtility.DisplayDialog("FigForge — Populate",
                $"Re-wired refs on {n} component(s) across {frames.Length} frame(s).", "OK");
        }

        static FigForgeFrame[] SceneFrames()
            => UnityEngine.Object.FindObjectsByType<FigForgeFrame>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        static FigForgeFrame[] ManagerFrames(FrameManager manager)
        {
            if (manager == null) return Array.Empty<FigForgeFrame>();
            var frames = new List<FigForgeFrame>();
            if (manager.screens != null)
                foreach (var f in manager.screens)
                    AddUnique(frames, f);
            foreach (var f in manager.GetComponentsInChildren<FigForgeFrame>(true))
                AddUnique(frames, f);
            return frames.ToArray();
        }

        static void AddUnique(List<FigForgeFrame> frames, FigForgeFrame frame)
        {
            if (frame != null && !frames.Contains(frame)) frames.Add(frame);
        }

        static string ManagerReportName(FrameManager manager, int frameCount)
            => manager != null ? manager.name + " (" + frameCount + " frame(s))" : frameCount + " frame(s)";

        // ---- populate (re-invoke generated wiring + persist) ----
        static int PopulateFrame(FigForgeFrame frame)
        {
            var reg = frame.GetComponent<FigForgeScreen>();
            if (reg == null) { Debug.LogWarning($"[FigForge] '{frame.name}' has no FigForgeScreen registry — Forge it first."); return 0; }
            int n = 0;
            if (frame.GetType() != typeof(FigForgeFrame))
            {
                Undo.RecordObject(frame, "FigForge Populate Refs");
                frame.__WireFrame(reg);
                EditorUtility.SetDirty(frame);
                n++;
            }
            foreach (var el in frame.GetComponentsInChildren<FigForgeFrameElement>(true))
                n += PopulateElement(el, reg, recordUndo: true);
            return n;
        }

        static int PopulatePage(FigForgeFrame frame)
        {
            int n = 0;
            foreach (var f in frame.transform.root.GetComponentsInChildren<FigForgeFrame>(true))
                n += PopulateFrame(f);
            return n;
        }

        static int PopulateElement(FigForgeFrameElement el, FigForgeScreen reg, bool recordUndo = true)
        {
            if (reg == null || el.GetType() == typeof(FigForgeFrameElement)) return 0; // base placeholder = nothing to wire
            if (recordUndo) Undo.RecordObject(el, "FigForge Populate Refs");
            el.__WireGroup(reg);
            EditorUtility.SetDirty(el);
            return 1;
        }

        // ---- validate (which accessor refs are null) ----
        static List<MissingRef> Validate(Component root, string rootName, FigForgeScreen reg)
        {
            var list = new List<MissingRef>();
            Collect(root, rootName, reg, list);
            return list;
        }

        static void Collect(Component comp, string path, FigForgeScreen reg, List<MissingRef> list)
        {
            foreach (var f in FigForgeAccessorTree.AccessorFields(comp.GetType()))
            {
                var val = f.GetValue(comp) as UnityEngine.Object; // UnityEngine.Object == null catches destroyed refs
                string key = FigForgeAccessorTree.AccessorKey(f.Name);
                if (val == null)
                    list.Add(new MissingRef { path = path + "." + key, fixable = reg != null && reg.Get(key) != null });
                else if (typeof(FigForgeFrameElement).IsAssignableFrom(f.FieldType) && val is Component child)
                    Collect(child, path + "." + key, reg, list); // descend into wired groups
            }
        }

        // ---- reporting ----
        static bool Report(List<MissingRef> missing, string name)
        {
            if (missing.Count == 0)
            {
                EditorUtility.DisplayDialog("FigForge — Validate", $"All accessor refs on “{name}” resolve. ✓", "OK");
                return false;
            }
            var sb = new StringBuilder();
            int fixable = 0;
            foreach (var m in missing)
            {
                sb.AppendLine((m.fixable ? "• [fixable] " : "• [gone]    ") + m.path);
                if (m.fixable) fixable++;
            }
            Debug.LogWarning($"[FigForge] “{name}” — {missing.Count} missing ref(s):\n{sb}");

            string msg = $"{missing.Count} missing ref(s) on “{name}”.\n\n"
                       + $"• {fixable} fixable by Populate (still in the registry)\n"
                       + $"• {missing.Count - fixable} gone from the registry (renamed/removed in Figma → re-Forge)\n\n"
                       + "Full list is in the Console.";
            if (fixable > 0)
                return EditorUtility.DisplayDialog("FigForge — Validate", msg, "Populate now", "Close");
            EditorUtility.DisplayDialog("FigForge — Validate", msg, "Close");
            return false;
        }

        static void Done(int n) => Debug.Log($"[FigForge] populated refs on {n} component(s).");
    }
}
