// =============================================================================
// FigForge — canvas/frame SceneView authoring helpers.
// =============================================================================

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace FigForge
{
    [CustomEditor(typeof(FigForgeCanvasHelper))]
    public class FigForgeCanvasHelperEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            var helper = (FigForgeCanvasHelper)target;
            var manager = helper.GetComponent<FrameManager>();
            if (manager != null)
            {
                EditorGUI.BeginChangeCheck();
                int columns = Mathf.Clamp(EditorGUILayout.IntField("Editor grid columns", manager.editorColumns), 1, 50);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(manager, "FigForge Editor Columns");
                    manager.editorColumns = columns;
                    FigForgeImporterWindow.SetEditorColumnsPref(columns);
                    EditorUtility.SetDirty(manager);
                    FigForgeFrameSceneTools.ArrangeRootFrames(helper);
                }
            }

            var oldColor = GUI.backgroundColor;
            GUI.backgroundColor = FigForgeFrameSceneTools.Orange;
            if (GUILayout.Button("Arrange Frames", GUILayout.Height(34)))
                FigForgeFrameSceneTools.ArrangeRootFrames(helper);
            GUI.backgroundColor = oldColor;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Root Frames", EditorStyles.boldLabel);

            var frames = FigForgeFrameSceneTools.RootFrames(helper);
            if (frames.Count == 0)
            {
                EditorGUILayout.HelpBox("No FigForge root frames found on this canvas.", MessageType.Info);
                return;
            }

            GUI.backgroundColor = FigForgeFrameSceneTools.Orange;
            for (int i = 0; i < frames.Count; i++)
            {
                var frame = frames[i];
                if (frame == null) continue;
                if (GUILayout.Button(frame.ScreenKey, GUILayout.Height(28)))
                    FigForgeFrameSceneTools.FocusFrame(frame);
            }
            GUI.backgroundColor = oldColor;
        }
    }

    [CustomEditor(typeof(FrameManager))]
    internal class FrameManagerEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            var manager = (FrameManager)target;
            int beforeColumns = manager != null ? manager.editorColumns : 5;

            DrawDefaultInspector();

            if (manager == null) return;
            int afterColumns = Mathf.Clamp(manager.editorColumns, 1, 50);
            if (afterColumns != manager.editorColumns)
            {
                Undo.RecordObject(manager, "FigForge Editor Columns");
                manager.editorColumns = afterColumns;
                EditorUtility.SetDirty(manager);
            }

            if (afterColumns == beforeColumns) return;
            FigForgeImporterWindow.SetEditorColumnsPref(afterColumns);
            var helper = FigForgeFrameSceneTools.EnsureHelper(manager);
            if (helper != null)
                FigForgeFrameSceneTools.ArrangeRootFrames(helper);
        }
    }

    internal static class FigForgeFrameSceneTools
    {
        internal static readonly Color Orange = new Color(1f, 0.48f, 0.05f);

        internal static List<FigForgeFrame> RootFrames(FigForgeCanvasHelper helper)
        {
            var result = new List<FigForgeFrame>();
            if (helper == null) return result;

            var manager = helper.GetComponent<FrameManager>();
            if (manager != null && manager.screens != null && manager.screens.Count > 0)
            {
                for (int i = 0; i < manager.screens.Count; i++)
                    if (manager.screens[i] != null)
                        result.Add(manager.screens[i]);
                return result;
            }

            var canvas = helper.GetComponent<Canvas>();
            if (canvas == null) return result;
            var frames = canvas.GetComponentsInChildren<FigForgeFrame>(true);
            for (int i = 0; i < frames.Length; i++)
            {
                var frame = frames[i];
                if (frame != null && frame.transform.parent == canvas.transform)
                    result.Add(frame);
            }
            return result;
        }

        internal static FigForgeCanvasHelper EnsureHelper(FrameManager manager)
        {
            if (manager == null) return null;
            var helper = manager.GetComponent<FigForgeCanvasHelper>();
            if (helper != null) return helper;
            if (manager.GetComponent<Canvas>() == null) return null;
            helper = Undo.AddComponent<FigForgeCanvasHelper>(manager.gameObject);
            EditorUtility.SetDirty(manager.gameObject);
            return helper;
        }

        internal static void ArrangeRootFrames(FigForgeCanvasHelper helper, bool recordUndo = true)
        {
            if (helper == null) return;

            var frames = RootFrames(helper);
            if (frames.Count == 0) return;

            int columns = FigForgeImporterWindow.EditorColumnsPref;
            var manager = helper.GetComponent<FrameManager>();
            if (manager != null)
                columns = Mathf.Clamp(manager.editorColumns, 1, 50);

            var arrangeable = new List<RectTransform>();
            for (int i = 0; i < frames.Count; i++)
            {
                var frame = frames[i];
                if (frame == null || frame.usesShell) continue;
                var rt = frame.GetComponent<RectTransform>();
                if (rt != null) arrangeable.Add(rt);
            }
            if (arrangeable.Count == 0) return;

            float cellWidth = 1f;
            float cellHeight = 1f;
            for (int i = 0; i < arrangeable.Count; i++)
            {
                var rt = arrangeable[i];
                Vector2 size = CurrentSize(rt);
                cellWidth = Mathf.Max(cellWidth, size.x);
                cellHeight = Mathf.Max(cellHeight, size.y);
            }

            const float gap = 80f;
            for (int i = 0; i < arrangeable.Count; i++)
            {
                var rt = arrangeable[i];
                if (recordUndo) Undo.RecordObject(rt, "Arrange FigForge Frames");

                Vector2 size = CurrentSize(rt);
                int col = i % columns;
                int row = i / columns;
                rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
                rt.pivot = new Vector2(0f, 1f);
                rt.sizeDelta = size;
                rt.anchoredPosition = new Vector2(col * (cellWidth + gap), -row * (cellHeight + gap));
                EditorUtility.SetDirty(rt);
                EditorUtility.SetDirty(rt.gameObject);
            }

            if (manager != null) EditorUtility.SetDirty(manager);
            RefreshEditorLayout(helper, arrangeable);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
                UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene());
        }

        static void RefreshEditorLayout(FigForgeCanvasHelper helper, List<RectTransform> roots)
        {
            ForceLayoutAndGraphics(helper, roots);
            EditorApplication.QueuePlayerLoopUpdate();
            SceneView.RepaintAll();
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();

            EditorApplication.delayCall += () =>
            {
                if (helper == null) return;
                ForceLayoutAndGraphics(helper, roots);
                EditorApplication.QueuePlayerLoopUpdate();
                SceneView.RepaintAll();
                UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
            };
        }

        static void ForceLayoutAndGraphics(FigForgeCanvasHelper helper, List<RectTransform> roots)
        {
            if (helper == null) return;

            var helperRt = helper.GetComponent<RectTransform>();
            if (helperRt != null)
                LayoutRebuilder.ForceRebuildLayoutImmediate(helperRt);

            for (int i = 0; i < roots.Count; i++)
            {
                var rt = roots[i];
                if (rt == null) continue;
                LayoutRebuilder.MarkLayoutForRebuild(rt);
                LayoutRebuilder.ForceRebuildLayoutImmediate(rt);

                var graphics = rt.GetComponentsInChildren<Graphic>(true);
                for (int g = 0; g < graphics.Length; g++)
                {
                    var graphic = graphics[g];
                    if (graphic == null) continue;
                    graphic.SetVerticesDirty();
                    graphic.SetMaterialDirty();
                    EditorUtility.SetDirty(graphic);
                }

                var compositors = rt.GetComponentsInChildren<FigForgePageCompositor>(true);
                for (int c = 0; c < compositors.Length; c++)
                    if (compositors[c] != null)
                        compositors[c].MarkDirty();
            }

            Canvas.ForceUpdateCanvases();
        }

        static Vector2 CurrentSize(RectTransform rt)
        {
            var rect = rt.rect;
            float width = Mathf.Max(1f, rect.width);
            float height = Mathf.Max(1f, rect.height);
            if (width <= 1f && rt.sizeDelta.x > 1f) width = rt.sizeDelta.x;
            if (height <= 1f && rt.sizeDelta.y > 1f) height = rt.sizeDelta.y;
            return new Vector2(width, height);
        }

        internal static void FocusFrame(FigForgeFrame frame)
        {
            if (frame == null || !TryFrameBounds(frame, out var bounds, out _)) return;
            Selection.activeGameObject = frame.gameObject;
            EditorGUIUtility.PingObject(frame.gameObject);

            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null && SceneView.sceneViews.Count > 0)
                sceneView = SceneView.sceneViews[0] as SceneView;
            if (sceneView == null) return;

            bounds.Expand(Mathf.Max(bounds.size.x, bounds.size.y) * 0.08f);
            sceneView.Frame(bounds, false);
            sceneView.Repaint();
        }

        static bool TryFrameBounds(FigForgeFrame frame, out Bounds bounds, out Vector3 topCenter)
        {
            bounds = default;
            topCenter = default;
            var rt = frame != null ? frame.GetComponent<RectTransform>() : null;
            if (rt == null) return false;

            var corners = new Vector3[4];
            rt.GetWorldCorners(corners);
            bounds = new Bounds(corners[0], Vector3.zero);
            for (int i = 1; i < corners.Length; i++)
                bounds.Encapsulate(corners[i]);
            topCenter = (corners[1] + corners[2]) * 0.5f;
            return true;
        }
    }
}
