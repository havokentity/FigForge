// =============================================================================
// FigForge — compositor source contract. Any graphic that renders itself to an
// offscreen surface so the PageCompositor can blend it (advanced/destination-
// reading Figma blend modes) implements this. Shared by FigForgeLayeredRect and
// FigForgeVectorGraphic so both ride the same compositor pipeline.
//
// `isActiveAndEnabled` and `transform` are satisfied implicitly by any Component.
// =============================================================================

using UnityEngine;

namespace FigForge
{
    public interface IFigForgeCompositorSource
    {
        bool RequiresPageCompositor { get; }      // BlendTier == 2 (needs destination read)
        bool isActiveAndEnabled { get; }
        Transform transform { get; }
        FigForgeBlendMode CompositorBlendMode { get; }
        float CompositorOpacity { get; }          // appearance opacity, applied at composite time
        float CompositorPad { get; }              // px margin baked into the surface around the rect
        RectTransform CompositorRectTransform { get; }
        RenderTexture GetCompositorSurface();     // the flattened, premultiplied layer surface
    }
}
