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

            // 9-slice border per asset filename. Explicit nineSlice wins; otherwise
            // we auto-detect a border from the PNG's alpha for slice CANDIDATES —
            // rounded/bordered panels (style.cornerRadius>0) and canonical button
            // state sprites — so they scale without smearing their corners. Icons /
            // plain fills aren't candidates, so they stay Simple.
            var borders = new Dictionary<string, NineSlice>();
            var candidates = new HashSet<string>();
            foreach (var e in manifest.elements)
            {
                if (!string.IsNullOrEmpty(e.asset))
                {
                    if (e.nineSlice != null && !borders.ContainsKey(e.asset)) borders[e.asset] = e.nineSlice;
                    else if (e.style != null && e.style.cornerRadius > 0f) candidates.Add(e.asset);
                }
                if (e.canonical != null && e.canonical.states != null)
                {
                    AddState(candidates, e.canonical.states.normal);
                    AddState(candidates, e.canonical.states.highlighted);
                    AddState(candidates, e.canonical.states.pressed);
                }
            }
            foreach (var file in candidates)
            {
                if (borders.ContainsKey(file)) continue;
                var ns = DetectBorder(Path.Combine(sourceDir, file));
                if (ns != null) borders[file] = ns;
            }

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

            // Two-phase: apply importer settings for EVERY sprite inside one batch
            // (SaveAndReimport is deferred — StopAssetEditing flushes them as a single
            // import pass instead of N one-by-one reimports), THEN load the results.
            // Loading inside the batch would return the pre-settings import (not yet
            // a Sprite), so the load loop must stay outside.
            AssetDatabase.StartAssetEditing();
            try
            {
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
                }
            }
            finally { AssetDatabase.StopAssetEditing(); }

            foreach (var asset in manifest.assets)
            {
                var sprite = AssetDatabase.LoadAssetAtPath<Sprite>($"{targetFolder}/{asset.file}");
                if (sprite != null) result[asset.file] = sprite;
            }

            return result;
        }

        static void AddState(HashSet<string> set, string file)
        {
            if (!string.IsNullOrEmpty(file)) set.Add(file);
        }

        // Detect a 9-slice border from a PNG's alpha: the corner inset on each side
        // (e.g. a rounded rect's radius). Returns null when the shape isn't a clean
        // stretchable panel (an edge is fully transparent) or needs no slicing.
        static NineSlice DetectBorder(string pngPath)
        {
            if (!File.Exists(pngPath)) return null;
            Texture2D tex = null;
            try
            {
                tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!tex.LoadImage(File.ReadAllBytes(pngPath))) return null;
                int w = tex.width, h = tex.height;
                if (w < 6 || h < 6) return null;
                var px = tex.GetPixels32();
                const byte T = 128;
                bool Op(int x, int y) => px[y * w + x].a >= T;

                int Lt = FirstX(Op, w, h - 1), Rt = LastX(Op, w, h - 1);   // top row
                int Lb = FirstX(Op, w, 0), Rb = LastX(Op, w, 0);           // bottom row
                int Bl = FirstY(Op, h, 0), Tl = LastY(Op, h, 0);           // left col
                int Br = FirstY(Op, h, w - 1), Tr = LastY(Op, h, w - 1);   // right col
                if (Lt < 0 || Rt < 0 || Lb < 0 || Rb < 0 || Bl < 0 || Tl < 0 || Br < 0 || Tr < 0)
                    return null; // an edge is fully transparent → not a simple panel

                int left = Mathf.Clamp(Mathf.Max(Lt, Lb), 0, w / 2 - 1);
                int right = Mathf.Clamp((w - 1) - Mathf.Min(Rt, Rb), 0, w / 2 - 1);
                int bottom = Mathf.Clamp(Mathf.Max(Bl, Br), 0, h / 2 - 1);
                int top = Mathf.Clamp((h - 1) - Mathf.Min(Tl, Tr), 0, h / 2 - 1);
                if (left + right + top + bottom == 0) return null; // square → no slicing needed
                return new NineSlice { left = left, right = right, top = top, bottom = bottom };
            }
            catch { return null; }
            finally { if (tex != null) Object.DestroyImmediate(tex); }
        }

        static int FirstX(System.Func<int, int, bool> op, int w, int y) { for (int x = 0; x < w; x++) if (op(x, y)) return x; return -1; }
        static int LastX(System.Func<int, int, bool> op, int w, int y) { for (int x = w - 1; x >= 0; x--) if (op(x, y)) return x; return -1; }
        static int FirstY(System.Func<int, int, bool> op, int h, int x) { for (int y = 0; y < h; y++) if (op(x, y)) return y; return -1; }
        static int LastY(System.Func<int, int, bool> op, int h, int x) { for (int y = h - 1; y >= 0; y--) if (op(x, y)) return y; return -1; }

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
