// =============================================================================
// FigForge — lightweight component for imported structural Figma containers.
//
// Root screens use FigForgeFrame and are managed by FrameManager. Nested Figma
// FRAME/GROUP nodes use this component: they are addressable elements with
// visibility helpers, but they are not navigable pages.
// =============================================================================

using System;
using UnityEngine;

namespace FigForge
{
    public enum FigForgeContainerType
    {
        Unknown = 0,
        Frame = 1,
        Group = 2,
        // Append new Figma structural container types here; never reorder/remove.
    }

    [DisallowMultipleComponent]
    public class FigForgeFrameElement : MonoBehaviour
    {
        [SerializeField, ReadOnly, Tooltip("Original Figma container type as an enum for code/Inspector use.")]
        FigForgeContainerType figmaType = FigForgeContainerType.Unknown;

        [SerializeField, ReadOnly, Tooltip("Original Figma container type as a stable string key for future-proof serialization.")]
        string figmaTypeKey = "";

        [Tooltip("Full name of the importer-generated FigForgeFrameElement subclass for this group " +
                 "(e.g. FigForge.Generated.LaunchPage_HeaderGroup). The editor swaps this plain base " +
                 "for that subclass + wires its typed child refs once it compiles. Empty = no generated group.")]
        [HideInInspector]
        public string generatedType;

        public FigForgeContainerType FigmaType => figmaType;
        public string FigmaTypeKey => figmaTypeKey;
        public RectTransform RectTransform => transform as RectTransform;
        public GameObject GameObject => gameObject;

        public bool isVisible
        {
            get => gameObject.activeSelf;
            set => SetVisible(value);
        }

        public void SetVisible(bool visible)
        {
            if (gameObject.activeSelf != visible)
                gameObject.SetActive(visible);
        }

        public bool GetVisible() => gameObject.activeSelf;

        // Populate this group's generated [SerializeField] child refs from the frame's
        // element registry — which lives on an ancestor (the page GameObject), shared by
        // every group on the page. Overridden by the generated subclass; no-op on the base.
        public virtual void __WireGroup(FigForgeScreen reg) { }

        // Lazy, null-safe resolve used by generated child accessors: returns the wired ref,
        // or looks it up from the ancestor registry on first access if it isn't wired yet.
        protected internal T __Get<T>(ref T field, string name) where T : Component
        {
            if (field == null)
            {
                var reg = GetComponentInParent<FigForgeScreen>(true);
                if (reg != null) field = reg.Get<T>(name);
            }
            return field;
        }

        public void ConfigureType(string typeKey)
        {
            figmaTypeKey = NormalizeKey(typeKey);
            figmaType = ParseType(figmaTypeKey);
        }

        static FigForgeContainerType ParseType(string typeKey)
        {
            if (string.IsNullOrEmpty(typeKey)) return FigForgeContainerType.Unknown;
            if (Enum.TryParse(typeKey, ignoreCase: true, out FigForgeContainerType parsed)
                && Enum.IsDefined(typeof(FigForgeContainerType), parsed))
                return parsed;
            return FigForgeContainerType.Unknown;
        }

        static string NormalizeKey(string typeKey)
            => string.IsNullOrEmpty(typeKey) ? "" : typeKey.Trim().ToLowerInvariant();
    }
}
