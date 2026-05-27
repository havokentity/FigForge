// =============================================================================
// FigForge — manifest loading + lookup helpers.
// =============================================================================

using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace FigForge
{
    public static class ManifestParser
    {
        public static Manifest Load(string manifestPath)
        {
            if (!File.Exists(manifestPath))
            {
                Debug.LogError($"[FigForge] manifest not found: {manifestPath}");
                return null;
            }
            try
            {
                var json = File.ReadAllText(manifestPath);
                var manifest = JsonConvert.DeserializeObject<Manifest>(json);
                if (manifest == null || manifest.elements == null)
                {
                    Debug.LogError("[FigForge] manifest parsed to null / no elements.");
                    return null;
                }
                if (manifest.schema != "figforge/manifest")
                    Debug.LogWarning($"[FigForge] unexpected schema '{manifest.schema}'.");
                return manifest;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[FigForge] failed to parse manifest: {e.Message}");
                return null;
            }
        }

        public static Dictionary<string, ElementData> Index(Manifest manifest)
        {
            var map = new Dictionary<string, ElementData>();
            foreach (var e in manifest.elements)
                if (e.id != null) map[e.id] = e;
            return map;
        }

        public static List<ElementData> Roots(Manifest manifest)
        {
            var roots = new List<ElementData>();
            foreach (var e in manifest.elements)
                if (string.IsNullOrEmpty(e.parentId)) roots.Add(e);
            return roots;
        }
    }
}
