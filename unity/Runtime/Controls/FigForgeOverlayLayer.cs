// =============================================================================
// FigForge — marks a global overlay layer (a role=overlay frame, e.g. the dialog
// layer). The frame is authored in its own grid cell like any screen, but unlike a
// screen it isn't registered with the FrameManager, so nothing snaps it to the
// viewport. At play-time this fills the parent canvas itself — the same effect
// FrameManager.FillParent gives screens — so its dialogs cover the whole screen
// regardless of where the editor spread left the layer.
// =============================================================================

using UnityEngine;

namespace FigForge
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(RectTransform))]
    public class FigForgeOverlayLayer : MonoBehaviour
    {
        void Awake()
        {
            var rt = (RectTransform)transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            rt.localScale = Vector3.one;
        }
    }
}
