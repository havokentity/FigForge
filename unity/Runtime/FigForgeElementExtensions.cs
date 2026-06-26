// =============================================================================
// FigForge — element extensions. Show/hide ANY imported element through code —
// canonical controls and plain elements (Image panels, TMP texts, empty
// RectTransform containers) alike:
//
//     UIFrames.LaunchPage.Title.SetVisible(false);
//     if (UIFrames.LaunchPage.MySlider.GetVisible()) { ... }
//
// Backed by GameObject.SetActive, so a hidden element stops rendering, receiving
// input, and contributing to layout. The FigForge control classes' `isVisible`
// property is sugar over the same mechanism.
// =============================================================================

using UnityEngine;

namespace FigForge
{
    public static class FigForgeElementExtensions
    {
        /// <summary>Show or hide an imported element (drives GameObject.SetActive).</summary>
        public static void SetVisible(this Component element, bool visible)
        {
            if (element != null && element.gameObject.activeSelf != visible)
                element.gameObject.SetActive(visible);
        }

        /// <summary>Whether the element is shown — GameObject.activeSelf: the element's
        /// OWN flag, regardless of whether an ancestor currently hides it (use
        /// gameObject.activeInHierarchy for the on-screen answer).</summary>
        public static bool GetVisible(this Component element)
            => element != null && element.gameObject.activeSelf;
    }
}
