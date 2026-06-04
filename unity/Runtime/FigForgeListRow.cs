// =============================================================================
// FigForge — a single rich List row. Recolours its background graphic per
// interaction state (Regular / Rollover / Pressed / Selected) and reports clicks
// to the owning FigForgeList so the list keeps a single-selected row. The row
// visuals are a clone of the captured Item subtree (icon/title/subtitle/etc.);
// this component only drives the state colour + selection, mirroring how
// FigForgeButtonStateColors drives a button.
// =============================================================================

using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace FigForge
{
    [AddComponentMenu("FigForge/List Row")]
    public class FigForgeListRow : MonoBehaviour,
        IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler, IPointerClickHandler
    {
        public FigForgeList owner;
        public int index;
        public FigForgeFill regular = FigForgeFill.Solid(Color.white);
        public FigForgeFill rollover = FigForgeFill.Solid(Color.white);
        public FigForgeFill pressed = FigForgeFill.Solid(Color.white);
        public FigForgeFill selected = FigForgeFill.Solid(Color.white);
        public bool hasRollover, hasPressed, hasSelected;

        FigForgeRoundedRect _rr;
        FigForgeLayeredRect _layered;
        Graphic _plain; // flat (non-SDF) row background — tinted via Graphic.color
        bool _over, _down;
        public bool IsSelected { get; private set; }

        // Bind the background graphic to recolour (the row's 'Regular' layer). The
        // layer is an SDF rect when rounded/bordered, else a flat Image we tint.
        public void Bind(Graphic bg)
        {
            _rr = bg as FigForgeRoundedRect;
            _layered = bg as FigForgeLayeredRect;
            _plain = (_rr == null && _layered == null) ? bg : null;
            Apply();
        }

        void OnEnable() { Apply(); }

        public void OnPointerEnter(PointerEventData e) { _over = true; Apply(); }
        public void OnPointerExit(PointerEventData e) { _over = false; _down = false; Apply(); }
        public void OnPointerDown(PointerEventData e) { _down = true; Apply(); }
        public void OnPointerUp(PointerEventData e) { _down = false; Apply(); }
        public void OnPointerClick(PointerEventData e) { if (owner != null) owner.Select(index); }

        public void SetSelected(bool s) { IsSelected = s; Apply(); }

        void Apply()
        {
            FigForgeFill f = (IsSelected && hasSelected) ? selected
                : (_down && hasPressed) ? pressed
                : (_over && hasRollover) ? rollover
                : regular;
            if (_layered != null) _layered.SetPrimaryFill(f);
            else if (_rr != null) _rr.SetFill(f);
            else if (_plain != null) _plain.color = f.color; // flat bg: tint to the state's solid colour
        }
    }
}
