// =============================================================================
// FigForge — copies exported PNGs into the project, configures them as sprites,
// and returns a filename → Sprite map for the hierarchy builder.
// =============================================================================

using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace FigForge
{
    public class TextureImportSettings
    {
        public int maxSize = 2048;
        public bool autoMaxSize = true;
        public TextureImporterCompression compression = TextureImporterCompression.Compressed;
        public bool mipmaps = false;
        public FilterMode filterMode = FilterMode.Bilinear;
        public bool disableMipForUI = true;
    }

    public static class TextureImportHelper
    {
        /// <summary>
        /// Copy every PNG referenced by the manifest from <paramref name="sourceDir"/>
        /// into <paramref name="targetFolder"/> (Assets-relative), import as sprites,
        /// and return a map keyed by the manifest filename.
        /// </summary>
        public static Dictionary<string, Sprite> Import(
            Manifest manifest, string sourceDir, string targetFolder, TextureImportSettings settings)
        {
            var result = new Dictionary<string, Sprite>();
            if (manifest.assets == null || manifest.assets.Count == 0) return result;

            EnsureFolder(targetFolder);

            // nineSlice border per asset filename (from elements that reference it).
            var borders = new Dictionary<string, NineSlice>();
            foreach (var e in manifest.elements)
                if (!string.IsNullOrEmpty(e.asset) && e.nineSlice != null && !borders.ContainsKey(e.asset))
                    borders[e.asset] = e.nineSlice;

            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var asset in manifest.assets)
                {
                    var src = Path.Combine(sourceDir, asset.file);
                    if (!File.Exists(src)) { Debug.LogWarning($"[FigForge] missing PNG: {asset.file}"); continue; }
                    var dst = $"{targetFolder}/{asset.file}";
                    File.Copy(src, dst, true);
                }
            }
            finally { AssetDatabase.StopAssetEditing(); }

            AssetDatabase.Refresh();

            foreach (var asset in manifest.assets)
            {
                var dst = $"{targetFolder}/{asset.file}";
                var importer = AssetImporter.GetAtPath(dst) as TextureImporter;
                if (importer == null) continue;

                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.alphaIsTransparency = true;
                importer.mipmapEnabled = settings.mipmaps && !settings.disableMipForUI;
                importer.filterMode = settings.filterMode;
                importer.textureCompression = settings.compression;
                importer.maxTextureSize = settings.autoMaxSize ? AutoMax(dst) : settings.maxSize;

                if (borders.TryGetValue(asset.file, out var b))
                    importer.spriteBorder = new Vector4(b.left, b.bottom, b.right, b.top);

                importer.SaveAndReimport();

                var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(dst);
                if (sprite != null) result[asset.file] = sprite;
            }

            return result;
        }

        static int AutoMax(string assetPath)
        {
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
            int dim = tex != null ? Mathf.Max(tex.width, tex.height) : 1024;
            int p = 32;
            while (p < dim && p < 8192) p *= 2;
            return p;
        }

        public static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            var parts = folder.Split('/');
            string cur = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = $"{cur}/{parts[i]}";
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(cur, parts[i]);
                cur = next;
            }
        }
    }
}
