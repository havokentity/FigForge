// =============================================================================
// FigForge — CanonicalLibrary. Maps a (kind, reference name) to a reusable Unity
// prefab. A Figma layer named `Btn_Save_PrimaryButton` (kind=button,
// ref=PrimaryButton) instantiates the matching prefab instead of being rebuilt
// from a PNG/text — so canonical UI (buttons, toggles, inputs, dropdowns,
// sliders) is defined once and reused across every imported page.
// =============================================================================

using System.Collections.Generic;
using UnityEngine;

namespace FigForge
{
    public enum CanonicalKind { Button, Toggle, Input, Dropdown, Slider, Radio, List }

    [CreateAssetMenu(fileName = "FigForgeCanonicalLibrary", menuName = "FigForge/Canonical Library")]
    public class CanonicalLibrary : ScriptableObject
    {
        [System.Serializable]
        public class Entry
        {
            public CanonicalKind kind = CanonicalKind.Button;
            [Tooltip("Reference name as used in Figma layer names (the trailing token).")]
            public string referenceName;
            [Tooltip("Importer-managed visual definition signature for generated prefabs.")]
            public string signature;
            [Tooltip("Prefab instantiated for this (kind, reference). A FigForgeBindings on it gets auto-filled.")]
            public GameObject prefab;
        }

        public List<Entry> entries = new List<Entry>();

        public GameObject Resolve(string kind, string referenceName)
        {
            if (!TryParseKind(kind, out var k)) return null;
            foreach (var e in entries)
                if (e != null && e.kind == k && e.referenceName == referenceName)
                    return e.prefab;
            return null;
        }

        public Entry ResolveEntry(string kind, string referenceName)
        {
            if (!TryParseKind(kind, out var k)) return null;
            foreach (var e in entries)
                if (e != null && e.kind == k && e.referenceName == referenceName)
                    return e;
            return null;
        }

        public Entry ResolveSignature(string kind, string signature)
        {
            if (string.IsNullOrEmpty(signature)) return null;
            if (!TryParseKind(kind, out var k)) return null;
            foreach (var e in entries)
                if (e != null && e.kind == k && e.signature == signature && e.prefab != null)
                    return e;
            return null;
        }

        public bool HasAny => entries != null && entries.Count > 0;

        public static bool TryParseKind(string s, out CanonicalKind kind)
        {
            switch ((s ?? "").ToLowerInvariant())
            {
                case "button": kind = CanonicalKind.Button; return true;
                case "toggle": kind = CanonicalKind.Toggle; return true;
                case "radio": kind = CanonicalKind.Radio; return true;
                case "input": kind = CanonicalKind.Input; return true;
                case "dropdown": kind = CanonicalKind.Dropdown; return true;
                case "slider": kind = CanonicalKind.Slider; return true;
                case "list": kind = CanonicalKind.List; return true;
                default: kind = CanonicalKind.Button; return false;
            }
        }
    }
}
