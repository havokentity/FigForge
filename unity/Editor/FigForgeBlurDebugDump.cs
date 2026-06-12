// TEMPORARY diagnostic — dumps blur/compositor intermediates for layers whose
// name contains "frame" so the white-silhouette artifact can be localized:
// cached (post-layer-blur) premultiplied surface, unpremultiplied view,
// premult-invariant overshoot map, and the compositor's blended target.
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace FigForge
{
    static class FigForgeBlurDebugDump
    {
        // Reproduce the user's screenshot conditions: big layer blur on both blobs.
        [MenuItem("FigForge/Debug/Set Frames Layer Blur 30")]
        static void SetBigBlur()
        {
            var rects = Object.FindObjectsByType<FigForgeLayeredRect>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            foreach (var r in rects)
            {
                if (!r.name.ToLowerInvariant().Contains("frame")) continue;
                var fills = new List<FigForgeFill>(r.Fills);
                var strokes = new List<FigForgeStrokeLayer>(r.Strokes);
                var effects = new List<FigForgeEffectLayer>(r.Effects);
                bool had = false;
                for (int i = 0; i < effects.Count; i++)
                {
                    if (effects[i].kind != FigForgeEffectKind.LayerBlur) continue;
                    var e = effects[i];
                    e.blur = 30f; e.endBlur = 30f;
                    effects[i] = e;
                    had = true;
                }
                if (!had) effects.Add(FigForgeEffectLayer.LayerBlur(30f));
                r.ConfigureLayers(fills, strokes, effects, r.CompositorShapeCorners);
                Debug.Log("[FigForge] big blur set on " + r.name + " -> " + r.DebugSummary, r);
            }
        }

        // ---- Live-rebuild dump: PNGs taken from INSIDE FigForgePageCompositor.Rebuild,
        // so organic (willRenderCanvases-dispatched) rebuilds can be inspected. Auto-
        // disarms after MaxDumpFiles so play mode doesn't flood the disk.
        const int MaxDumpFiles = 36;
        static int _dumpCount;
        static int _seq;

        [MenuItem("FigForge/Debug/Arm Rebuild Dump")]
        static void ArmRebuildDump()
        {
            string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "FFBlurDump", "rebuild"));
            Directory.CreateDirectory(dir);
            foreach (var f in Directory.GetFiles(dir)) File.Delete(f);
            string logPath = Path.Combine(dir, "rebuild_log.txt");
            _dumpCount = 0;
            _seq = 0;

            FigForgePageCompositor.DebugRebuildLog = msg =>
            {
                File.AppendAllText(logPath, "frame=" + Time.frameCount + " seq=" + _seq + " " + msg + "\n");
            };
            FigForgePageCompositor.DebugRebuildSink = (label, rt) =>
            {
                if (rt == null || _dumpCount >= MaxDumpFiles) return;
                _seq++;
                _dumpCount++;
                string path = Path.Combine(dir, _seq.ToString("D3") + "_f" + Time.frameCount + "_" + Sanitize(label) + ".png");
                WriteRtPng(rt, path);
                File.AppendAllText(logPath, "frame=" + Time.frameCount + " seq=" + _seq + " queued " + Path.GetFileName(path) + "\n");
                if (_dumpCount >= MaxDumpFiles) DisarmRebuildDump();
            };
            Debug.Log("[FigForge] rebuild dump ARMED -> " + dir);
        }

        [MenuItem("FigForge/Debug/Disarm Rebuild Dump")]
        static void DisarmRebuildDump()
        {
            FigForgePageCompositor.DebugRebuildSink = null;
            FigForgePageCompositor.DebugRebuildLog = null;
            Debug.Log("[FigForge] rebuild dump disarmed (" + _dumpCount + " files)");
        }

        static void WriteRtPng(RenderTexture rt, string path)
        {
            RequestRtPixels(rt, "debug rebuild dump", pixels =>
            {
                for (int i = 0; i < pixels.colors.Length; i++) pixels.colors[i].a = 255;
                var tex = new Texture2D(pixels.width, pixels.height, TextureFormat.RGBA32, false);
                tex.SetPixels32(pixels.colors);
                tex.Apply(false);
                File.WriteAllBytes(path, tex.EncodeToPNG());
                Object.DestroyImmediate(tex);
            });
        }

        // Disable the marquee/animated objects so the page stops re-marking the
        // compositor dirty every frame — lets a single organic rebuild FREEZE.
        [MenuItem("FigForge/Debug/Toggle MarkDirty Trace")]
        static void ToggleMarkDirtyTrace()
        {
            FigForgePageCompositor.DebugTraceMarkDirty = !FigForgePageCompositor.DebugTraceMarkDirty;
            Debug.Log("[FigForge] MarkDirty trace " + (FigForgePageCompositor.DebugTraceMarkDirty ? "ON" : "OFF"));
        }

        [MenuItem("FigForge/Debug/Freeze Page Animations")]
        static void FreezeAnimations()
        {
            int n = 0;
            foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                // quack_quak: marquee anim. (The lists' scrollbar fade used to dirty
                // the compositor every frame via SetPrimaryFill; fixed by the
                // value-equality early-out, so the lists can stay enabled.)
                if (go.name != "quack_quak") continue;
                go.SetActive(false);
                n++;
            }
            Debug.Log("[FigForge] froze " + n + " animated object(s)");
        }

        [MenuItem("FigForge/Debug/Set Frames Layer Blur 12")]
        static void SetBlur12() { SetFramesBlur(12f); }

        static void SetFramesBlur(float radius)
        {
            var rects = Object.FindObjectsByType<FigForgeLayeredRect>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            foreach (var r in rects)
            {
                if (!r.name.ToLowerInvariant().Contains("frame")) continue;
                var fills = new List<FigForgeFill>(r.Fills);
                var strokes = new List<FigForgeStrokeLayer>(r.Strokes);
                var effects = new List<FigForgeEffectLayer>(r.Effects);
                bool had = false;
                for (int i = 0; i < effects.Count; i++)
                {
                    if (effects[i].kind != FigForgeEffectKind.LayerBlur) continue;
                    var e = effects[i];
                    e.blur = radius; e.endBlur = radius;
                    effects[i] = e;
                    had = true;
                }
                if (!had) effects.Add(FigForgeEffectLayer.LayerBlur(radius));
                r.ConfigureLayers(fills, strokes, effects, r.CompositorShapeCorners);
                Debug.Log("[FigForge] blur " + radius + " set on " + r.name + " -> " + r.DebugSummary, r);
            }
        }

        // Managed-side present-binding check: the material's mainTexture should be
        // the compositor's CURRENT blended target for every compositor-owned layer.
        [MenuItem("FigForge/Debug/Report Present Bindings")]
        static void ReportBindings()
        {
            var rects = Object.FindObjectsByType<FigForgeLayeredRect>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            foreach (var r in rects)
            {
                var comp = r.GetComponentInParent<FigForgePageCompositor>();
                var blended = comp != null ? comp.GetBlendedSurface(r) : null;
                var mat = r.materialForRendering;
                var matTex = mat != null ? mat.mainTexture : null;
                var surface = r.GetCompositorSurface();
                Debug.Log("[FigForge] bind " + r.name
                    + " blended=" + (blended != null ? blended.GetInstanceID() + ":" + blended.width + "x" + blended.height : "null")
                    + " matTex=" + (matTex != null ? matTex.GetInstanceID() + ":" + matTex.width + "x" + matTex.height : "null")
                    + " surface=" + (surface != null ? surface.GetInstanceID() + ":" + surface.width + "x" + surface.height : "null")
                    + " ok=" + (blended != null && matTex == blended), r);
            }
        }

        [MenuItem("FigForge/Debug/Dump Blur Intermediates")]
        static void Dump()
        {
            string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "FFBlurDump"));
            Directory.CreateDirectory(dir);
            var sb = new StringBuilder();
            sb.AppendLine("colorSpace=" + QualitySettings.activeColorSpace);

            var rects = Object.FindObjectsByType<FigForgeLayeredRect>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            sb.AppendLine("layeredRects=" + rects.Length);

            Canvas.ForceUpdateCanvases();

            foreach (var r in rects)
            {
                if (!r.name.ToLowerInvariant().Contains("frame")) continue;
                var comp = r.GetComponentInParent<FigForgePageCompositor>();
                if (comp != null) { comp.MarkDirty(); }
            }
            Canvas.ForceUpdateCanvases();

            foreach (var r in rects)
            {
                if (!r.name.ToLowerInvariant().Contains("frame")) continue;
                var surface = r.GetCompositorSurface();
                string baseName = Sanitize(r.name) + "_" + r.GetInstanceID();
                if (surface == null) { sb.AppendLine(r.name + ": surface=null"); continue; }
                sb.AppendLine(r.name + ": surface=" + surface.width + "x" + surface.height + " sRGB=" + surface.sRGB);
                DumpRt(surface, Path.Combine(dir, baseName + "_surface"), sb, r.name + ".surface");
                var comp = r.GetComponentInParent<FigForgePageCompositor>();
                var blended = comp != null ? comp.GetBlendedSurface(r) : null;
                if (blended != null) DumpRt(blended, Path.Combine(dir, baseName + "_blended"), sb, r.name + ".blended");
                else sb.AppendLine(r.name + ": blended=null comp=" + (comp != null));
            }

            File.WriteAllText(Path.Combine(dir, "stats.txt"), sb.ToString());
            Debug.Log("[FigForge] Blur dump -> " + dir + "\n" + sb);
        }

        // The compositor's exact per-layer capture: everything at-or-above the layer
        // in paint order culled, rendered through the page camera.
        [MenuItem("FigForge/Debug/Dump Per-Layer Captures")]
        static void DumpCaptures()
        {
            string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "FFBlurDump"));
            Directory.CreateDirectory(dir);
            var sb = new StringBuilder();

            var rects = Object.FindObjectsByType<FigForgeLayeredRect>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            Canvas.ForceUpdateCanvases();
            foreach (var r in rects)
            {
                if (!r.name.ToLowerInvariant().Contains("frame")) continue;
                var canvas = r.GetComponentInParent<Canvas>();
                var cam = canvas != null ? canvas.worldCamera : null;
                if (cam == null) { sb.AppendLine(r.name + ": no camera"); continue; }

                // replicate CullAtOrAbove
                var graphics = new List<UnityEngine.UI.Graphic>();
                var root = canvas.rootCanvas != null ? canvas.rootCanvas : canvas;
                root.GetComponentsInChildren(false, graphics);
                var culled = new List<CanvasRenderer>();
                foreach (var g in graphics)
                {
                    if (g == null) continue;
                    if (Compare(g.transform, r.transform) < 0) continue;
                    var cr = g.canvasRenderer;
                    if (cr == null || cr.cull) continue;
                    cr.cull = true;
                    culled.Add(cr);
                }
                var capture = FigForgePageCapture.CaptureTemporary(cam);
                foreach (var cr in culled) cr.cull = false;
                if (capture == null) { sb.AppendLine(r.name + ": capture null"); continue; }
                DumpRt(capture, Path.Combine(dir, Sanitize(r.name) + "_capture"), sb, r.name + ".capture");
                RenderTexture.ReleaseTemporary(capture);

                // replicate BackdropRect
                var rt = r.CompositorRectTransform;
                var rect = rt.rect;
                float pad = r.CompositorPad;
                Vector2 mn = new Vector2(float.MaxValue, float.MaxValue), mx = new Vector2(float.MinValue, float.MinValue);
                for (int i = 0; i < 4; i++)
                {
                    var local = new Vector3(
                        (i == 0 || i == 1) ? rect.xMin - pad : rect.xMax + pad,
                        (i == 0 || i == 3) ? rect.yMin - pad : rect.yMax + pad, 0f);
                    Vector2 sp = RectTransformUtility.WorldToScreenPoint(cam, rt.TransformPoint(local));
                    mn = Vector2.Min(mn, sp); mx = Vector2.Max(mx, sp);
                }
                var surface = r.GetCompositorSurface();
                sb.AppendLine(r.name + ": pad=" + pad + " rect=" + rect
                    + " backdropRect=(" + mn.x.ToString("F1") + "," + mn.y.ToString("F1") + ","
                    + (mx.x - mn.x).ToString("F1") + "," + (mx.y - mn.y).ToString("F1") + ")"
                    + " surface=" + (surface != null ? surface.width + "x" + surface.height : "null")
                    + " camPixel=" + cam.pixelWidth + "x" + cam.pixelHeight);
            }
            File.WriteAllText(Path.Combine(dir, "captures.txt"), sb.ToString());
            Debug.Log("[FigForge] capture dump -> " + dir + "\n" + sb);
        }

        static int Compare(Transform a, Transform b)
        {
            if (a == b) return 0;
            var pa = PathOf(a); var pb = PathOf(b);
            int n = Mathf.Min(pa.Count, pb.Count);
            for (int i = 0; i < n; i++) { int d = pa[i] - pb[i]; if (d != 0) return d; }
            return pa.Count - pb.Count;
        }

        static List<int> PathOf(Transform t)
        {
            var p = new List<int>();
            while (t != null) { p.Add(t.GetSiblingIndex()); t = t.parent; }
            p.Reverse();
            return p;
        }

        static string Sanitize(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }

        static void DumpRt(RenderTexture rt, string basePath, StringBuilder sb, string label)
        {
            sb.AppendLine(label + ": queued async GPU readback " + rt.width + "x" + rt.height);
            RequestRtPixels(rt, label, pixels =>
            {
                var px = pixels.colors;
                bool srgb = pixels.srgb && QualitySettings.activeColorSpace == ColorSpace.Linear;
                var view = new Color32[px.Length];
                var unpre = new Color32[px.Length];
                var over = new Color32[px.Length];
                int overshoot = 0; float maxRatio = 0f; long covered = 0;
                for (int i = 0; i < px.Length; i++)
                {
                    var p = px[i];
                    view[i] = new Color32(p.r, p.g, p.b, 255);
                    float a = p.a / 255f;
                    if (p.a > 0)
                    {
                        covered++;
                        // decode stored bytes to linear premult, compare against alpha
                        float lr = Lin(p.r / 255f, srgb), lg = Lin(p.g / 255f, srgb), lb = Lin(p.b / 255f, srgb);
                        float mx = Mathf.Max(lr, Mathf.Max(lg, lb));
                        float ratio = mx / a;
                        if (ratio > 1.05f) { overshoot++; if (ratio > maxRatio) maxRatio = ratio; }
                        unpre[i] = new Color32(Enc(lr / a, srgb), Enc(lg / a, srgb), Enc(lb / a, srgb), 255);
                        over[i] = ratio > 1.05f ? new Color32(255, 0, 0, 255) : new Color32(0, 0, 0, 255);
                    }
                    else
                    {
                        unpre[i] = new Color32(255, 0, 255, 255);
                        over[i] = new Color32(0, 0, 0, 255);
                    }
                }
                var tex = new Texture2D(pixels.width, pixels.height, TextureFormat.RGBA32, false);
                Write(basePath + "_premul.png", tex, view);
                Write(basePath + "_unpre.png", tex, unpre);
                Write(basePath + "_overshoot.png", tex, over);
                Object.DestroyImmediate(tex);

                string stats = label + ": " + pixels.width + "x" + pixels.height + " covered=" + covered + "/" + px.Length
                    + " overshoot(>1.05)=" + overshoot + " maxRatio=" + maxRatio.ToString("F2");
                File.WriteAllText(basePath + "_stats.txt", stats + "\n");
            });
        }

        static float Lin(float v, bool srgb) => srgb ? Mathf.GammaToLinearSpace(v) : v;
        static byte Enc(float v, bool srgb)
        {
            v = srgb ? Mathf.LinearToGammaSpace(Mathf.Clamp01(v)) : Mathf.Clamp01(v);
            return (byte)Mathf.RoundToInt(v * 255f);
        }

        static void Write(string path, Texture2D tex, Color32[] px)
        {
            tex.SetPixels32(px);
            tex.Apply(false);
            File.WriteAllBytes(path, tex.EncodeToPNG());
        }

        readonly struct RtPixels
        {
            public readonly int width;
            public readonly int height;
            public readonly bool srgb;
            public readonly Color32[] colors;

            public RtPixels(int width, int height, bool srgb, Color32[] colors)
            {
                this.width = width;
                this.height = height;
                this.srgb = srgb;
                this.colors = colors;
            }
        }

        static void RequestRtPixels(RenderTexture rt, string label, System.Action<RtPixels> onReady)
        {
            if (rt == null || onReady == null) return;

            var copy = RenderTexture.GetTemporary(rt.width, rt.height, 0, RenderTextureFormat.ARGB32);
            copy.filterMode = rt.filterMode;
            copy.wrapMode = TextureWrapMode.Clamp;
            Graphics.Blit(rt, copy);

            int width = copy.width;
            int height = copy.height;
            bool srgb = rt.sRGB;
            AsyncGPUReadback.Request(copy, 0, TextureFormat.RGBA32, request =>
            {
                try
                {
                    if (request.hasError)
                    {
                        Debug.LogWarning("[FigForge] Async GPU readback failed for " + label);
                        return;
                    }

                    var data = request.GetData<Color32>();
                    var colors = new Color32[data.Length];
                    data.CopyTo(colors);
                    onReady(new RtPixels(width, height, srgb, colors));
                }
                finally
                {
                    RenderTexture.ReleaseTemporary(copy);
                }
            });
        }
    }
}
