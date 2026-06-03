// =============================================================================
// FigForge — shows/hides a composite Checkmark subtree for a Toggle.
//
// UGUI Toggle.graphic cross-fades a single Graphic's CanvasRenderer alpha, which
// cannot show/hide a multi-node Checkmark subtree (the full Figma checkmark, with
// nested shapes/vectors/masks). This component drives the whole subtree's active
// state from the toggle's on/off value instead — mirroring the SetActive approach
// used by FigForgeButtonStateObjects. It reads the live Toggle.isOn, so it stays in
// sync with FigForgeBindings.Apply (which sets isOn post-instantiate) and with a
// ToggleGroup's mutual-exclusion (which fires onValueChanged on the losing toggle).
// =============================================================================

using UnityEngine;
using UnityEngine.UI;

namespace FigForge
{
    [AddComponentMenu("FigForge/Toggle Graphic Object")]
    public class FigForgeToggleGraphicObject : MonoBehaviour
    {
        public Toggle toggle;
        public GameObject graphicRoot; // the Checkmark subtree container, shown only when on

        void OnEnable() { Hook(); Apply(); }
        void OnDisable() { Unhook(); }

        void Hook()
        {
            if (toggle == null) return;
            toggle.onValueChanged.RemoveListener(OnChanged); // avoid double-subscribe on re-enable
            toggle.onValueChanged.AddListener(OnChanged);
        }

        void Unhook()
        {
            if (toggle != null) toggle.onValueChanged.RemoveListener(OnChanged);
        }

        void OnChanged(bool _) { Apply(); }

        public void Apply()
        {
            if (graphicRoot == null) return;
            bool on = toggle != null && toggle.isOn;
            if (graphicRoot.activeSelf != on) graphicRoot.SetActive(on);
        }
    }
}
