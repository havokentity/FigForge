// =============================================================================
// FigForge — import a FigForge export .zip directly (no manual unzip).
//
// A FigForge export bundle is a flat zip: manifest.json + the PNG assets at the
// root. This extracts it into an Assets-relative folder, then the normal build
// pipeline (ManifestParser → TextureImportHelper → HierarchyBuilder) takes over.
//
// Uses System.IO.Compression.ZipArchive directly (stream-per-entry) so it does
// not depend on the System.IO.Compression.FileSystem extension assembly.
// =============================================================================

using System;
using System.IO;
using System.IO.Compression;
using UnityEditor;
using UnityEngine;

namespace FigForge
{
    public static class ZipImporter
    {
        /// <summary>
        /// Extract a FigForge export zip into <paramref name="destAssetsFolder"/>
        /// (an "Assets/…"-relative path). Returns the extracted manifest's
        /// Assets-relative path, or null if the zip isn't a valid FigForge export.
        /// </summary>
        public static string ExtractToAssets(string zipPath, string destAssetsFolder)
        {
            if (string.IsNullOrEmpty(zipPath) || !File.Exists(zipPath))
            {
                Debug.LogError($"[FigForge] zip not found: {zipPath}");
                return null;
            }

            try
            {
                TextureImportHelper.EnsureFolder(destAssetsFolder);
                string projectRoot = Directory.GetParent(Application.dataPath).FullName;
                string destAbs = Path.Combine(projectRoot, destAssetsFolder.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(destAbs);

                bool hasManifest = false;
                int files = 0;

                using (var fs = File.OpenRead(zipPath))
                using (var archive = new ZipArchive(fs, ZipArchiveMode.Read))
                {
                    foreach (var entry in archive.Entries)
                    {
                        // Flatten: export zips are flat, so take only the file
                        // name. This also neutralises any zip-slip "../" entries.
                        var name = Path.GetFileName(entry.FullName);
                        if (string.IsNullOrEmpty(name)) continue; // directory entry

                        var outPath = Path.Combine(destAbs, name);
                        using (var es = entry.Open())
                        using (var os = File.Create(outPath))
                            es.CopyTo(os);

                        files++;
                        if (name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
                            hasManifest = true;
                    }
                }

                if (!hasManifest)
                {
                    Debug.LogError("[FigForge] zip has no manifest.json — not a FigForge export.");
                    return null;
                }

                AssetDatabase.Refresh();
                Debug.Log($"[FigForge] extracted {files} file(s) → {destAssetsFolder}");
                return $"{destAssetsFolder}/manifest.json";
            }
            catch (Exception e)
            {
                Debug.LogError($"[FigForge] failed to extract zip: {e.Message}");
                return null;
            }
        }
    }
}
