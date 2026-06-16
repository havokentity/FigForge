// =============================================================================
// FigForge — on-demand page capture. Renders a Screen Space - Camera page canvas
// through its own worldCamera into an offscreen RenderTexture, so the page
// compositor can blend Tier-2 layers against the REAL backdrop — foreign uGUI
// (TMP text, plain Images) included — GrabPass-free and URP-safe.
//
//   • Built-in:  targetTexture + Camera.Render() (classic synchronous capture).
//   • SRP (URP): RenderPipeline.SubmitRenderRequest(StandardRequest) — renders
//                the camera into the destination on demand without disturbing
//                its normal on-screen render this frame. Core-engine API
//                (2023.1+), no URP package reference needed. On older SRP
//                editors capture is unavailable and callers stay on the
//                per-graphic degrade path.
//
// A Screen Space - Camera canvas renders ONLY through its assigned camera, so
// the capture must go through that exact camera — no separate capture camera.
//
// The capture forces a full SolidColor clear: in the FigForge camera's
// overlay-over-scene config (clearFlags = Depth) a depth-only clear would leave
// garbage in the offscreen target, since the scene cameras don't render into
// it. Documented consequence: where a 3D scene shows through on screen, blends
// see the camera background colour instead — UI-only pages capture exactly.
// =============================================================================

using UnityEngine;
using UnityEngine.Rendering;

namespace FigForge
{
    internal static class FigForgePageCapture
    {
        // Hard re-entrancy guard. A capture (Camera.Render / SubmitRenderRequest)
        // re-fires willRenderCanvases; if anything reaches back here mid-capture, a
        // nested render request is rejected by SRP ("recursive rendering is not
        // supported"). The compositor already defers across instances, but this is the
        // last line of defense regardless of caller — callers treat the refusal as a
        // (recoverable) capture miss and retry next frame.
        static bool _capturing;

        // A page canvas the compositor can capture: Screen Space - Camera with a
        // camera assigned (resolved on the root canvas). Overlay has no camera to
        // render through — callers fall back to the per-graphic degrade path.
        public static bool CanCapture(Canvas canvas) => ResolveCamera(canvas) != null;

        public static Camera ResolveCamera(Canvas canvas)
        {
            if (canvas == null) return null;
            var root = canvas.rootCanvas != null ? canvas.rootCanvas : canvas;
            return root.renderMode == RenderMode.ScreenSpaceCamera ? root.worldCamera : null;
        }

        // Capture into a temporary RT sized to the camera's current pixel dimensions
        // (== live screen pixels, so the captured backdrop matches what sits beneath
        // the present quads 1:1). Caller must ReleaseTemporary. Returns null when
        // capture isn't possible (no camera, or SRP without render-request support).
        public static RenderTexture CaptureTemporary(Camera cam)
        {
            if (cam == null) return null;
            int width = Mathf.Clamp(cam.pixelWidth, 1, SystemInfo.maxTextureSize);
            int height = Mathf.Clamp(cam.pixelHeight, 1, SystemInfo.maxTextureSize);
            var rt = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
            rt.filterMode = FilterMode.Bilinear;
            rt.wrapMode = TextureWrapMode.Clamp;
            if (!Render(cam, rt))
            {
                RenderTexture.ReleaseTemporary(rt);
                return null;
            }
            return rt;
        }

        public static bool Render(Camera cam, RenderTexture target)
        {
            if (cam == null || target == null) return false;
            if (_capturing) return false; // refuse nested capture — SRP forbids it
            _capturing = true;
            var prevFlags = cam.clearFlags;
            if (prevFlags != CameraClearFlags.SolidColor && prevFlags != CameraClearFlags.Skybox)
                cam.clearFlags = CameraClearFlags.SolidColor;
            try
            {
                if (GraphicsSettings.currentRenderPipeline == null)
                    return RenderBuiltin(cam, target);
                return RenderScriptable(cam, target);
            }
            finally
            {
                cam.clearFlags = prevFlags;
                _capturing = false;
            }
        }

        static bool RenderBuiltin(Camera cam, RenderTexture target)
        {
            var prev = cam.targetTexture;
            cam.targetTexture = target;
            cam.Render();
            cam.targetTexture = prev;
            return true;
        }

        static bool RenderScriptable(Camera cam, RenderTexture target)
        {
#if UNITY_2023_1_OR_NEWER
            var request = new RenderPipeline.StandardRequest { destination = target };
            if (!RenderPipeline.SupportsRenderRequest(cam, request)) return false;
            RenderPipeline.SubmitRenderRequest(cam, request);
            return true;
#else
            return false; // pre-2023.1 SRP: no on-demand camera render API
#endif
        }
    }
}
