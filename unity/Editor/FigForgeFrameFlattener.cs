// =============================================================================
// FigForge — frame flattener (EDITOR ONLY).
//
// Problem: in edit mode every imported page (FigForgeFrame) is rolled out as a
// visible sibling, so the Scene view renders AND the FigForge components run for
// the WHOLE design at once — thousands of GameObjects. Play mode is smooth
// because FrameManager shows one frame and deactivates the rest. (Proven: with
// all-but-one frame deactivated, the editor is buttery.)
//
// Fix: keep the whole-design overview, but make every IDLE frame cheap. Each idle
// frame is FROZEN — its subtree is SetActive(false) (one flag flip, propagated by
// Unity; kills both its render and its willRenderCanvases CPU) and a baked
// snapshot is drawn IN ITS PLACE as a Scene-view OVERLAY (no GameObject — a true
// imposter, so the Hierarchy stays one-entry-per-frame, no proxy clutter). The
// frame you select into is THAWED (live, fully interactive); selection drives it.
// Clicking an imposter in the Scene view thaws that frame.
//
// Scope & safety:
//   • Editor-only (this assembly). Runtime / builds are untouched.
//   • No serialized artifacts. Frozen state is transient session state.
//   • Thaw-all is forced before save, before play, and before domain reload, so
//     the saved scene and every build are fully live (no inactive frames left).
//
// Known tradeoffs (by design):
//   • A frozen frame is a static snapshot (Scene-view only) until you click into
//     it; the Game view shows the live frame as always.
//   • Freezing deactivates frames, which marks the scene dirty (the asterisk
//     stays while flattened). Never data loss — save thaws first.
//   • One snapshot RT per frozen frame (clamped to MaxSnapshotDim).
//
// Toggle: Tools ▸ FigForge ▸ Frame Flatten ▸ Enabled. Default OFF.
// =============================================================================

using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FigForge
{
    [InitializeOnLoad]
    internal static class FigForgeFrameFlattener
    {
        const string EnabledPref = "FigForge.FrameFlatten.Enabled";
        const string MenuPath = "Tools/FigForge/Frame Flatten/Enabled";
        const string ThawMenuPath = "Tools/FigForge/Frame Flatten/Thaw All Now";
        // Long-edge clamp for snapshots: rollout frames are shown small, so full
        // screen-res snapshots would waste memory for no visible gain.
        const int MaxSnapshotDim = 1024;

        sealed class Entry
        {
            public RenderTexture rt;
            public bool frozen;
        }

        static readonly Dictionary<FigForgeFrame, Entry> _entries = new Dictionary<FigForgeFrame, Entry>();
        static FigForgeFrame _liveFrame;     // currently thawed/focused frame (null = none)
        static bool _liveFrameDirty;         // content changed while live → re-bake on freeze
        static bool _busy;                   // re-entrancy guard around our own scene edits
        static readonly Vector3[] _corners = new Vector3[4];

        static bool Enabled
        {
            get => EditorPrefs.GetBool(EnabledPref, false);
            set => EditorPrefs.SetBool(EnabledPref, value);
        }

        static FigForgeFrameFlattener()
        {
            Selection.selectionChanged += OnSelectionChanged;
            EditorApplication.hierarchyChanged += OnHierarchyChanged;
            Undo.undoRedoPerformed += OnUndoRedo;
            EditorSceneManager.sceneSaving += OnSceneSaving;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            AssemblyReloadEvents.beforeAssemblyReload += Cleanup;
            SceneView.duringSceneGui += OnSceneGUI;
            if (Enabled) EditorApplication.delayCall += SafeReconcile;
        }

        // ---- menu --------------------------------------------------------------

        [MenuItem(MenuPath)]
        static void ToggleEnabled()
        {
            Enabled = !Enabled;
            if (Enabled) SafeReconcile();
            else Cleanup();
        }

        [MenuItem(MenuPath, true)]
        static bool ToggleEnabledValidate()
        {
            Menu.SetChecked(MenuPath, Enabled);
            return !EditorApplication.isPlayingOrWillChangePlaymode;
        }

        [MenuItem(ThawMenuPath)]
        static void ThawAllNow() => Cleanup();

        // ---- imposter overlay (Scene view) -------------------------------------

        // Draw each frozen frame's snapshot where the frame sits, following pan/zoom,
        // and thaw it on click. No GameObject involved — the Hierarchy never doubles.
        static void OnSceneGUI(SceneView sv)
        {
            if (!Enabled || _entries.Count == 0) return;
            var e = Event.current;
            Handles.BeginGUI();
            foreach (var kv in _entries)
            {
                var f = kv.Key; var entry = kv.Value;
                if (f == null || entry == null || !entry.frozen || entry.rt == null) continue;
                var frt = f.transform as RectTransform;
                if (frt == null) continue;
                if (!TryGuiRect(frt, out var r)) continue;

                // The captured RT already lands top-down in GUI space here — draw as-is
                // (an explicit V-flip rendered the imposters upside down).
                GUI.DrawTexture(r, entry.rt);

                // A plain left-click on the imposter thaws (and selects) the frame —
                // modifier clicks/drags pass through so Scene navigation is untouched.
                if (e.type == EventType.MouseDown && e.button == 0 && e.modifiers == EventModifiers.None
                    && r.Contains(e.mousePosition))
                {
                    ThawAndSelect(f);
                    e.Use();
                }
            }
            Handles.EndGUI();
        }

        static bool TryGuiRect(RectTransform rt, out Rect r)
        {
            r = default;
            rt.GetWorldCorners(_corners); // valid on an inactive RectTransform (last layout)
            Vector2 min = new Vector2(float.MaxValue, float.MaxValue);
            Vector2 max = new Vector2(float.MinValue, float.MinValue);
            for (int i = 0; i < 4; i++)
            {
                Vector2 g = HandleUtility.WorldToGUIPoint(_corners[i]);
                min = Vector2.Min(min, g);
                max = Vector2.Max(max, g);
            }
            if (max.x - min.x < 1f || max.y - min.y < 1f) return false;
            r = Rect.MinMaxRect(min.x, min.y, max.x, max.y);
            return true;
        }

        static void ThawAndSelect(FigForgeFrame f)
        {
            _busy = true;
            try { ThawFrame(f); _liveFrame = f; _liveFrameDirty = false; }
            finally { _busy = false; }
            Selection.activeGameObject = f.gameObject; // fires OnSelectionChanged → freezes the rest
            SceneView.RepaintAll();
        }

        // ---- event handlers ----------------------------------------------------

        static void OnSelectionChanged()
        {
            if (!Enabled || _busy || EditorApplication.isPlayingOrWillChangePlaymode) return;
            SafeReconcile();
        }

        static void OnHierarchyChanged()
        {
            if (_busy || !Enabled) return;
            if (_liveFrame != null) _liveFrameDirty = true;
        }

        static void OnUndoRedo()
        {
            if (_busy || !Enabled) return;
            if (_liveFrame != null) _liveFrameDirty = true;
        }

        static void OnSceneSaving(UnityEngine.SceneManagement.Scene scene, string path)
        {
            // Leave the SAVED scene fully live. The next selection change re-freezes
            // idle frames (cheap — snapshots are reused).
            if (_entries.Count == 0) return;
            ThawAllKeepingSnapshots();
        }

        static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode) Cleanup();
            else if (state == PlayModeStateChange.EnteredEditMode && Enabled)
                EditorApplication.delayCall += SafeReconcile;
        }

        // ---- core --------------------------------------------------------------

        static void SafeReconcile()
        {
            try { Reconcile(); }
            catch (System.Exception e)
            {
                Debug.LogWarning("[FigForge] Frame flatten reconcile failed (feature left enabled): " + e);
            }
        }

        // The frame holding the active selection is LIVE; every other active frame is FROZEN.
        static void Reconcile()
        {
            if (!Enabled || EditorApplication.isPlayingOrWillChangePlaymode) return;

            var focus = ResolveFocusedFrame(Selection.activeGameObject);
            // No frame focused — a FRESH IMPORT (nothing / the canvas selected), a
            // just-opened scene, or a click on empty space. Leave every frame as the
            // importer/scene left it (live) instead of freezing them all: an import
            // must open ACTIVE, not with the whole canvas deactivated. Freezing is
            // opt-in — it kicks in only once you actually focus a frame to edit it,
            // and stays sticky after (deselecting doesn't thaw the rest).
            if (focus == null) return;

            _busy = true;
            try
            {
                var frames = Object.FindObjectsByType<FigForgeFrame>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                for (int i = 0; i < frames.Length; i++)
                {
                    var f = frames[i];
                    if (f == null) continue;
                    if (f == focus) ThawFrame(f);
                    else FreezeFrame(f);
                }
                _liveFrame = focus;
                _liveFrameDirty = false;
            }
            finally { _busy = false; }

            PruneDeadEntries();
            SceneView.RepaintAll();
        }

        static FigForgeFrame ResolveFocusedFrame(GameObject go)
        {
            return go != null ? go.GetComponentInParent<FigForgeFrame>() : null;
        }

        static void FreezeFrame(FigForgeFrame f)
        {
            if (f == null || !f.gameObject.activeSelf) return; // inactive → already free

            var entry = GetEntry(f);
            bool needBake = entry.rt == null || (f == _liveFrame && _liveFrameDirty);
            if (needBake)
            {
                var baked = BakeFrame(f);
                if (baked == null) return; // couldn't capture → leave it live, retry later
                if (entry.rt != null) ReleaseRT(entry.rt);
                entry.rt = baked;
            }
            f.gameObject.SetActive(false);
            entry.frozen = true;
        }

        static void ThawFrame(FigForgeFrame f)
        {
            if (f == null) return;
            if (!f.gameObject.activeSelf) f.gameObject.SetActive(true);
            if (_entries.TryGetValue(f, out var e) && e != null) e.frozen = false;
        }

        // Reactivate every frame, keep snapshots/RTs for reuse.
        static void ThawAllKeepingSnapshots()
        {
            _busy = true;
            try
            {
                foreach (var kv in _entries)
                {
                    var f = kv.Key; var e = kv.Value;
                    if (f != null && !f.gameObject.activeSelf) f.gameObject.SetActive(true);
                    if (e != null) e.frozen = false;
                }
                _liveFrame = null;
                _liveFrameDirty = false;
            }
            finally { _busy = false; }
            SceneView.RepaintAll();
        }

        // Full teardown: reactivate frames, release RTs, forget state.
        static void Cleanup()
        {
            _busy = true;
            try
            {
                foreach (var kv in _entries)
                {
                    var f = kv.Key; var e = kv.Value;
                    if (f != null && !f.gameObject.activeSelf) f.gameObject.SetActive(true);
                    if (e != null && e.rt != null) ReleaseRT(e.rt);
                }
            }
            finally
            {
                _entries.Clear();
                _liveFrame = null;
                _liveFrameDirty = false;
                _busy = false;
            }
            SceneView.RepaintAll();
        }

        // ---- snapshot capture --------------------------------------------------

        // Render one frame in isolation into an RT. Rollout frames sit OUTSIDE the
        // canvas camera's view, so (like the page compositor's editor capture) the
        // frame is briefly moved to the camera origin, everything else is culled,
        // the camera is captured, and the frame's screen rect is cropped out.
        static RenderTexture BakeFrame(FigForgeFrame f)
        {
            var canvas = f.GetComponentInParent<Canvas>();
            var cam = FigForgePageCapture.ResolveCamera(canvas);
            if (cam == null) return null;                 // Overlay / no camera → can't flatten
            var frt = f.transform as RectTransform;
            if (frt == null) return null;

            var root = canvas.rootCanvas != null ? canvas.rootCanvas : canvas;
            var savedPos = frt.anchoredPosition3D;
            var culled = new List<CanvasRenderer>();
            RenderTexture full = null;
            try
            {
                frt.anchoredPosition3D = new Vector3(0f, 0f, savedPos.z);
                Canvas.ForceUpdateCanvases();

                var crs = root.GetComponentsInChildren<CanvasRenderer>(false);
                for (int i = 0; i < crs.Length; i++)
                {
                    var cr = crs[i];
                    if (cr == null || cr.cull) continue;
                    if (cr.transform.IsChildOf(frt)) continue; // keep this frame's own renderers
                    cr.cull = true;
                    culled.Add(cr);
                }

                full = FigForgePageCapture.CaptureTemporary(cam);
                if (full == null) return null;

                Rect sr = FrameScreenRect(frt, cam, full.width, full.height);
                if (sr.width < 1f || sr.height < 1f) return null;

                float longEdge = Mathf.Max(sr.width, sr.height);
                float k = longEdge > MaxSnapshotDim ? MaxSnapshotDim / longEdge : 1f;
                int tw = Mathf.Clamp(Mathf.RoundToInt(sr.width * k), 1, MaxSnapshotDim);
                int th = Mathf.Clamp(Mathf.RoundToInt(sr.height * k), 1, MaxSnapshotDim);

                var dst = new RenderTexture(tw, th, 0, RenderTextureFormat.ARGB32)
                {
                    name = "FigForgeFrozen_" + f.name,
                    hideFlags = HideFlags.DontSave,
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                };
                dst.Create();
                // Crop the frame's screen rect out of the full capture (UV-rect blit).
                var scale = new Vector2(sr.width / full.width, sr.height / full.height);
                var offset = new Vector2(sr.xMin / full.width, sr.yMin / full.height);
                Graphics.Blit(full, dst, scale, offset);
                return dst;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[FigForge] Frame flatten bake failed for '" + f.name + "': " + e.Message);
                return null;
            }
            finally
            {
                if (full != null) RenderTexture.ReleaseTemporary(full);
                for (int i = 0; i < culled.Count; i++) if (culled[i] != null) culled[i].cull = false;
                frt.anchoredPosition3D = savedPos;
                Canvas.ForceUpdateCanvases();
            }
        }

        static Rect FrameScreenRect(RectTransform rt, Camera cam, int w, int h)
        {
            rt.GetWorldCorners(_corners);
            Vector2 min = new Vector2(float.MaxValue, float.MaxValue);
            Vector2 max = new Vector2(float.MinValue, float.MinValue);
            for (int i = 0; i < 4; i++)
            {
                Vector2 s = RectTransformUtility.WorldToScreenPoint(cam, _corners[i]);
                min = Vector2.Min(min, s);
                max = Vector2.Max(max, s);
            }
            min.x = Mathf.Clamp(min.x, 0f, w); min.y = Mathf.Clamp(min.y, 0f, h);
            max.x = Mathf.Clamp(max.x, 0f, w); max.y = Mathf.Clamp(max.y, 0f, h);
            return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
        }

        // ---- bookkeeping -------------------------------------------------------

        static Entry GetEntry(FigForgeFrame f)
        {
            if (!_entries.TryGetValue(f, out var e) || e == null)
            {
                e = new Entry();
                _entries[f] = e;
            }
            return e;
        }

        static void PruneDeadEntries()
        {
            List<FigForgeFrame> dead = null;
            foreach (var kv in _entries)
            {
                if (kv.Key != null) continue;
                (dead ??= new List<FigForgeFrame>()).Add(kv.Key);
                if (kv.Value != null && kv.Value.rt != null) ReleaseRT(kv.Value.rt);
            }
            if (dead != null) foreach (var k in dead) _entries.Remove(k);
        }

        static void ReleaseRT(RenderTexture rt)
        {
            if (rt == null) return;
            if (RenderTexture.active == rt) RenderTexture.active = null;
            rt.Release();
            Object.DestroyImmediate(rt);
        }
    }
}
