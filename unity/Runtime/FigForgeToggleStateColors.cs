// =============================================================================
// FigForge — swaps a dropdown/toggle row's rounded fill for pointer states and
// its checked/selected state. Used by TMP_Dropdown item templates so the current
// option can have a distinct fill from hover and press.
// =============================================================================

using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace FigForge
{
    [AddComponentMenu("FigForge/Toggle State Colors")]
    public class FigForgeToggleStateColors : MonoBehaviour,
        IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler
    {
        public FigForgeRoundedRect target;
        public FigForgeLayeredRect layeredTarget;
        public Toggle toggle;
        public FigForgeFill normal = FigForgeFill.Solid(Color.white);
        public FigForgeFill highlighted = FigForgeFill.Solid(Color.white);
        public FigForgeFill pressed = FigForgeFill.Solid(Color.white);
        public FigForgeFill selected = FigForgeFill.Solid(Color.white);
        public bool hasSelected;
        public bool useShapeStyles;
        public FigForgeShapeStyle normalShape;
        public FigForgeShapeStyle highlightedShape;
        public FigForgeShapeStyle pressedShape;
        public FigForgeShapeStyle selectedShape;

        bool _over, _down;

        void Awake()
        {
            if (target == null) target = GetComponent<FigForgeRoundedRect>();
            if (target == null) target = GetComponentInChildren<FigForgeRoundedRect>(true);
            if (layeredTarget == null) layeredTarget = GetComponent<FigForgeLayeredRect>();
            if (layeredTarget == null) layeredTarget = GetComponentInChildren<FigForgeLayeredRect>(true);
            Apply();
        }

        void OnEnable()
        {
            if (target == null) target = GetComponent<FigForgeRoundedRect>();
            if (target == null) target = GetComponentInChildren<FigForgeRoundedRect>(true);
            if (layeredTarget == null) layeredTarget = GetComponent<FigForgeLayeredRect>();
            if (layeredTarget == null) layeredTarget = GetComponentInChildren<FigForgeLayeredRect>(true);
            if (toggle != null) toggle.onValueChanged.AddListener(OnToggleValueChanged);
            Apply();
        }

        void OnDisable()
        {
            if (toggle != null) toggle.onValueChanged.RemoveListener(OnToggleValueChanged);
            _over = false;
            _down = false;
            Apply(); // restore the resting fill so a disabled row isn't left on hover/press
        }

        public void OnPointerEnter(PointerEventData e) { _over = true; Apply(); }
        public void OnPointerExit(PointerEventData e) { _over = false; _down = false; Apply(); }
        public void OnPointerDown(PointerEventData e) { _down = true; Apply(); }
        public void OnPointerUp(PointerEventData e) { _down = false; Apply(); }

        void OnToggleValueChanged(bool _) => Apply();

        public void ApplyListEdgeCorners(int index, int count)
        {
            if (!useShapeStyles) return;
            normalShape.corners = EdgeCorners(normalShape.corners, index, count);
            highlightedShape.corners = EdgeCorners(highlightedShape.corners, index, count);
            pressedShape.corners = EdgeCorners(pressedShape.corners, index, count);
            selectedShape.corners = EdgeCorners(selectedShape.corners, index, count);
            Apply();
        }

        static Vector4 EdgeCorners(Vector4 source, int index, int count)
        {
            float r = Mathf.Max(source.x, source.y, source.z, source.w);
            if (r <= 0.001f) return Vector4.zero;
            if (count <= 1) return new Vector4(r, r, r, r);
            if (index <= 0) return new Vector4(r, r, 0f, 0f);          // tl,tr,br,bl
            if (index >= count - 1) return new Vector4(0f, 0f, r, r);
            return Vector4.zero;
        }

        void Apply()
        {
            if (target == null && layeredTarget == null) return;
            if (useShapeStyles)
            {
                var style = _down ? pressedShape
                    : _over ? highlightedShape
                    : (hasSelected && toggle != null && toggle.isOn) ? selectedShape
                    : normalShape;
                if (layeredTarget != null) layeredTarget.SetStyle(style);
                else target.SetStyle(style);
            }
            else
            {
                var fill = _down ? pressed
                    : _over ? highlighted
                    : (hasSelected && toggle != null && toggle.isOn) ? selected
                    : normal;
                if (layeredTarget != null) layeredTarget.SetPrimaryFill(fill);
                else target.SetFill(fill);
            }
        }
    }
}
