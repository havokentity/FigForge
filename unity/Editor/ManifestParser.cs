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
        // Manifest wire-format versions this importer understands — counterpart
        // constant: plugin/src/types.ts MANIFEST_VERSION. Keep the two in sync.
        //   "1.0" — legacy exports (live-import assets as number[] JSON)
        //   "2.0" — base64 live-import assets + canonicalSchema in the manifest
        // An unknown (or missing) version means the plugin and this importer are
        // from different generations: the contract has dozens of optional fields,
        // so a mismatched pair degrades silently field-by-field. Fail loudly
        // instead (LogError + abort, the importer's standard fatal-error surface).
        static readonly string[] SupportedManifestVersions = { "1.0", "2.0" };

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
                if (System.Array.IndexOf(SupportedManifestVersions, manifest.version) < 0)
                {
                    Debug.LogError(
                        $"[FigForge] manifest version '{(string.IsNullOrEmpty(manifest.version) ? "(missing)" : manifest.version)}' " +
                        $"is not supported by this importer (supported: {string.Join(", ", SupportedManifestVersions)}) — import aborted.\n" +
                        "One side is stale: if the manifest is NEWER, update the FigForge Unity importer; " +
                        "if it is OLDER (or has no version), re-export with a current FigForge plugin.\n" +
                        manifestPath);
                    return null;
                }
                // Canonical-control capture generation: a mismatch is NOT fatal
                // (plain elements import fine) but canonical controls may degrade —
                // say so prominently with both numbers so the stale side is obvious.
                // 0 = pre-2.0 manifest that didn't carry the field; nothing to compare.
                if (manifest.canonicalSchema != 0 && manifest.canonicalSchema != HierarchyBuilder.CanonicalSchema)
                    Debug.LogWarning(
                        $"[FigForge] canonical schema mismatch: manifest (plugin) = {manifest.canonicalSchema}, " +
                        $"importer (HierarchyBuilder.CanonicalSchema) = {HierarchyBuilder.CanonicalSchema}. " +
                        "Canonical controls (buttons/toggles/lists/…) may degrade — update the older side.\n" +
                        manifestPath);
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

        public static ProjectData LoadProject(string projectJsonPath)
        {
            if (!File.Exists(projectJsonPath)) return null;
            try
            {
                var p = JsonConvert.DeserializeObject<ProjectData>(File.ReadAllText(projectJsonPath));
                if (p == null || p.screens == null) return null;
                if (p.schema != "figforge/project")
                    Debug.LogWarning($"[FigForge] unexpected project schema '{p.schema}'.");
                return p;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[FigForge] failed to parse project.json: {e.Message}");
                return null;
            }
        }
    }
}
