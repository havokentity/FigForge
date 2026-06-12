// =============================================================================
// FigForge — auto font import. Resolves a Figma (family, style) to a
// TMP_FontAsset, generating one on first sight from the best-matching font file:
// an existing TMP asset, then a .ttf/.otf in the project, then bundled Inter
// fonts, then a file in the OS font folders (copied in). Generated assets are
// dynamic SDF (glyphs bake on demand) and named after their source file so the
// real weight is visible (faux bold/italic only applies when the chosen file
// isn't that weight).
//
// Matching is token-based and tiered so "Arial" doesn't grab "Arial Unicode" and
// a Bold request prefers "Arial Bold" over "Arial Bold Italic".
//
// The Figma plugin can't ship arbitrary font binaries (Figma exposes font
// *names*, not the file), so non-bundled families must already exist on this
// machine; if they don't we fall back to TMP's default and log exactly what to
// add.
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using TMPro;
using UnityEditor;
using UnityEngine;

namespace FigForge
{
    public static class FontAutoImporter
    {
        const string FontFolder = "Assets/FigForge/Fonts";
        public const float DefaultFontFaceDilate = 0.15f;
        static readonly Dictionary<string, TMP_FontAsset> _cache = new Dictionary<string, TMP_FontAsset>();
        public static float FaceDilate { get; set; } = DefaultFontFaceDilate;
        static readonly HashSet<string> Weights = new HashSet<string>(new[]
            { "thin", "extralight", "ultralight", "light", "medium", "semibold", "demibold",
              "bold", "extrabold", "black", "heavy", "italic", "oblique" });

        public static void ClearCache() => _cache.Clear();

        /// <summary>Resolve a (family, style) to a TMP_FontAsset, generating one if
        /// needed. Returns null (→ TMP default) when no font file can be found.</summary>
        public static TMP_FontAsset Resolve(string family, string style, Action<string> log)
        {
            if (string.IsNullOrEmpty(family)) return null;
            string key = Norm(family) + "|" + Norm(style);
            if (_cache.TryGetValue(key, out var hit)) { ApplyFigForgeFontDefaults(hit); return hit; }

            var existing = BestExisting(family, style);
            TMP_FontAsset asset;
            if (existing.tier == 0) asset = existing.asset;       // already have the exact face
            else asset = Generate(family, style, log) ?? existing.asset;

            ApplyFigForgeFontDefaults(asset);
            if (asset == null)
                log?.Invoke($"font '{family} {style}' not found — install it or drop a .ttf/.otf in {FontFolder}/ and re-import (using TMP default)");
            _cache[key] = asset;
            return asset;
        }

        // ---- existing TMP assets ----------------------------------------------
        static (TMP_FontAsset asset, int tier) BestExisting(string family, string style)
        {
            (TMP_FontAsset asset, int tier) best = (null, int.MaxValue);
            foreach (var g in AssetDatabase.FindAssets("t:TMP_FontAsset"))
            {
                var a = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(AssetDatabase.GUIDToAssetPath(g));
                if (a == null) continue;
                int t = Tier(a.name, family, style);
                if (t >= 0 && t < best.tier) best = (a, t);
            }
            return best;
        }

        // ---- generation --------------------------------------------------------
        static TMP_FontAsset Generate(string family, string style, Action<string> log)
        {
            // Never let a font-generation hiccup abort the whole build — fall back
            // to the TMP default instead.
            try
            {
                string src = FindFontFile(family, style); // project-relative .ttf/.otf (OS file copied in)
                if (src == null) return null;

                string outPath = $"{FontFolder}/{Safe(Path.GetFileNameWithoutExtension(src))} SDF.asset";
                var prior = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(outPath);
                if (prior != null) { ApplyFigForgeFontDefaults(prior); return prior; } // reuse

                var font = AssetDatabase.LoadAssetAtPath<Font>(src);
                if (font == null) return null;
                var tmp = TMP_FontAsset.CreateFontAsset(font); // dynamic SDF, on-demand atlas
                if (tmp == null) return null;

                TextureImportHelper.EnsureFolder(FontFolder); // src may be a project/package font → folder not yet created
                AssetDatabase.CreateAsset(tmp, outPath);
                tmp.name = Path.GetFileNameWithoutExtension(outPath);
                if (tmp.material != null) { tmp.material.name = tmp.name + " Material"; AssetDatabase.AddObjectToAsset(tmp.material, tmp); }
                if (tmp.atlasTextures != null)
                    foreach (var tex in tmp.atlasTextures) if (tex != null) AssetDatabase.AddObjectToAsset(tex, tmp);
                ApplyFigForgeFontDefaults(tmp);
                EnsurePopularGlyphs(tmp, log);
                AssetDatabase.SaveAssets();
                AssetDatabase.ImportAsset(outPath);
                log?.Invoke($"auto-imported font '{family} {style}' → {outPath} (source: {src})");
                return AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(outPath);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[FigForge] font auto-import failed for '{family} {style}': {e}");
                log?.Invoke($"font auto-import failed for '{family} {style}': {e.Message} — using default");
                return null;
            }
        }

        public static void EnsurePopularGlyphs(TMP_FontAsset asset, Action<string> log)
        {
            EnsurePopularGlyphs(asset, log, true);
        }

        static PopularGlyphBakeResult EnsurePopularGlyphs(TMP_FontAsset asset, Action<string> log, bool logMissing)
        {
            var result = new PopularGlyphBakeResult();
            if (asset == null) return result;

            asset.ReadFontAssetDefinition();
            result.AlreadyPresent = CountPresentPopularGlyphs(asset);
            if (asset.HasCharacters(PopularGlyphs.All))
            {
                result.AlreadyComplete = true;
                return result;
            }

            int beforePresent = result.AlreadyPresent;
            int beforeAtlasPages = asset.atlasTextures != null ? asset.atlasTextures.Count(t => t != null) : 0;
            bool enabledMultiAtlas = !asset.isMultiAtlasTexturesEnabled;

            // The curated set can overflow a single default TMP atlas; dynamic assets
            // still need extra pages to survive a project reload.
            asset.isMultiAtlasTexturesEnabled = true;
            asset.TryAddCharacters(PopularGlyphs.All, out _);
            asset.ReadFontAssetDefinition();

            int afterPresent = CountPresentPopularGlyphs(asset);
            result.Added = Math.Max(0, afterPresent - beforePresent);
            // TMP can report the full request when there were no new glyphs to add;
            // the post-bake character table is the reliable designer-facing list.
            result.Missing = MissingPopularGlyphs(asset);
            result.EmbeddedAtlasPages = EmbedMissingAtlasTextures(asset);

            int afterAtlasPages = asset.atlasTextures != null ? asset.atlasTextures.Count(t => t != null) : 0;
            result.Changed = result.Added > 0 || result.EmbeddedAtlasPages > 0 || enabledMultiAtlas || afterAtlasPages != beforeAtlasPages;

            if (logMissing && !string.IsNullOrEmpty(result.Missing))
                log?.Invoke($"font '{asset.name}' is missing popular glyphs in its source font: {FormatGlyphList(result.Missing)}");

            if (result.Changed)
            {
                ApplyFigForgeFontDefaults(asset);
                EditorUtility.SetDirty(asset);
                if (asset.material != null) EditorUtility.SetDirty(asset.material);
            }

            return result;
        }

        [MenuItem("Window/FigForge/Bake Popular Glyphs")]
        static void BakePopularGlyphsMenu()
        {
            TextureImportHelper.EnsureFolder(FontFolder);

            int touched = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:TMP_FontAsset", new[] { FontFolder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.StartsWith(FontFolder + "/", StringComparison.Ordinal)) continue;

                var asset = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
                if (asset == null) continue;

                var result = EnsurePopularGlyphs(asset, null, false);
                int missingCount = string.IsNullOrEmpty(result.Missing) ? 0 : result.Missing.Length;

                if (result.Changed)
                {
                    touched++;
                    Debug.Log($"[FigForge] Baked popular glyphs for {path}: added {result.Added}, already present {result.AlreadyPresent}, missing in source font {missingCount}{FormatMissingSuffix(result.Missing)}", asset);
                }
                else if (result.AlreadyComplete)
                {
                    Debug.Log($"[FigForge] Popular glyphs already baked for {path}: already present {result.AlreadyPresent}, missing in source font 0", asset);
                }
                else
                {
                    Debug.Log($"[FigForge] Popular glyphs unchanged for {path}: added 0, already present {result.AlreadyPresent}, missing in source font {missingCount}{FormatMissingSuffix(result.Missing)}", asset);
                }
            }

            if (touched > 0)
                AssetDatabase.SaveAssets();

            Debug.Log($"[FigForge] Popular glyph bake complete: updated {touched} generated font asset(s) under {FontFolder}.");
        }

        struct PopularGlyphBakeResult
        {
            public int AlreadyPresent;
            public int Added;
            public string Missing;
            public int EmbeddedAtlasPages;
            public bool Changed;
            public bool AlreadyComplete;
        }

        static int CountPresentPopularGlyphs(TMP_FontAsset asset)
        {
            int count = 0;
            foreach (char c in PopularGlyphs.All)
                if (asset.characterLookupTable.ContainsKey(c))
                    count++;
            return count;
        }

        static string MissingPopularGlyphs(TMP_FontAsset asset)
        {
            var builder = new StringBuilder();
            foreach (char c in PopularGlyphs.All)
                if (!asset.characterLookupTable.ContainsKey(c))
                    builder.Append(c);
            return builder.ToString();
        }

        static int EmbedMissingAtlasTextures(TMP_FontAsset asset)
        {
            int embedded = 0;
            string assetPath = AssetDatabase.GetAssetPath(asset);
            if (string.IsNullOrEmpty(assetPath) || asset.atlasTextures == null) return embedded;

            foreach (var tex in asset.atlasTextures)
            {
                if (tex == null || AssetDatabase.GetAssetPath(tex) == assetPath) continue;
                AssetDatabase.AddObjectToAsset(tex, asset);
                embedded++;
            }

            return embedded;
        }

        static string FormatMissingSuffix(string missing) =>
            string.IsNullOrEmpty(missing) ? "" : $": {FormatGlyphList(missing)}";

        static string FormatGlyphList(string glyphs)
        {
            var parts = new List<string>();
            foreach (char c in glyphs)
                parts.Add($"{PrintableGlyph(c)} U+{(int)c:X4}");
            return string.Join(", ", parts);
        }

        static string PrintableGlyph(char c)
        {
            switch (c)
            {
                case ' ':
                    return "<space>";
                case '\u00A0':
                    return "<no-break space>";
                default:
                    return c.ToString();
            }
        }

        // Best .ttf/.otf for (family, style) across project + package + OS by
        // tier; copies an OS file into the project when it's the winner.
        static string FindFontFile(string family, string style)
        {
            var candidates = AssetDatabase.FindAssets("t:Font").Select(AssetDatabase.GUIDToAssetPath)
                .Where(IsFontFile).Concat(BundledInterFontFiles()).Concat(OsFontFiles())
                .Distinct(StringComparer.OrdinalIgnoreCase);
            string chosen = null; int bestTier = int.MaxValue; int bestSource = int.MaxValue;
            foreach (var p in candidates)
            {
                int t = Tier(Path.GetFileNameWithoutExtension(p), family, style);
                if (t < 0) continue;
                int source = SourceRank(p);
                if (t > bestTier || (t == bestTier && source >= bestSource)) continue;
                bestTier = t; bestSource = source; chosen = p;
                if (t == 0 && p.StartsWith("Assets/")) break; // can't beat an in-project exact match
            }
            if (chosen == null) return null;
            if (IsAssetDatabasePath(chosen)) return chosen;

            TextureImportHelper.EnsureFolder(FontFolder);
            string dest = $"{FontFolder}/{Path.GetFileName(chosen)}";
            try { File.Copy(chosen, ProjectAbs(dest), true); AssetDatabase.ImportAsset(dest); return dest; }
            catch { return null; }
        }

        static IEnumerable<string> BundledInterFontFiles()
        {
            const string dir = "Packages/com.figforge.unity-importer/Fonts/Inter";
            foreach (var style in new[]
            {
                "Thin", "ThinItalic", "ExtraLight", "ExtraLightItalic", "Light", "LightItalic",
                "Regular", "Italic", "Medium", "MediumItalic", "SemiBold", "SemiBoldItalic",
                "Bold", "BoldItalic", "ExtraBold", "ExtraBoldItalic", "Black", "BlackItalic"
            })
            {
                var path = $"{dir}/Inter-{style}.ttf";
                if (AssetDatabase.LoadAssetAtPath<Font>(path) != null) yield return path;
            }
        }

        static bool IsAssetDatabasePath(string p) =>
            p.StartsWith("Assets/", StringComparison.Ordinal) || p.StartsWith("Packages/", StringComparison.Ordinal);

        static int SourceRank(string p)
        {
            if (p.StartsWith("Assets/", StringComparison.Ordinal)) return 0;
            if (p.StartsWith("Packages/com.figforge.unity-importer/", StringComparison.Ordinal)) return 1;
            if (p.StartsWith("Packages/", StringComparison.Ordinal)) return 2;
            return 3;
        }

        // Lower = better; -1 = family doesn't match at all. Compares the token set
        // of a candidate name against the family + requested weight tokens.
        static int Tier(string candidateName, string family, string style)
        {
            var ft = Tokens(candidateName);
            var fam = Tokens(family);
            if (fam.Count == 0 || !fam.IsSubsetOf(ft)) return -1;

            var extras = new HashSet<string>(ft); extras.ExceptWith(fam); extras.Remove("regular");
            var desired = Tokens(style); desired.Remove("regular");
            var foreign = new HashSet<string>(extras); foreign.ExceptWith(desired);

            int tier;
            if (extras.SetEquals(desired)) tier = 0;                                   // exact family+weight
            else if (desired.Count > 0 && desired.IsSubsetOf(extras) && foreign.Count == 0) tier = 1;
            else if (desired.Count > 0 && desired.IsSubsetOf(extras)) tier = 2;         // weight + extra (e.g. italic)
            else if (desired.Count == 0 && !extras.Any(Weights.Contains)) tier = 3;     // regular family variant (Unicode/Narrow)
            else tier = 4;                                                             // family only / wrong weight
            return tier * 100 + Math.Min(extras.Count, 99);
        }

        // ---- OS fonts ----------------------------------------------------------
        static IEnumerable<string> OsFontFiles()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var dirs = new List<string>();
#if UNITY_EDITOR_OSX
            dirs.AddRange(new[] { Path.Combine(home, "Library/Fonts"), "/Library/Fonts",
                "/System/Library/Fonts", "/System/Library/Fonts/Supplemental" });
#elif UNITY_EDITOR_WIN
            dirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.Fonts));
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft/Windows/Fonts"));
#else
            dirs.AddRange(new[] { "/usr/share/fonts", "/usr/local/share/fonts",
                Path.Combine(home, ".fonts"), Path.Combine(home, ".local/share/fonts") });
#endif
            foreach (var d in dirs)
            {
                if (!Directory.Exists(d)) continue;
                string[] files;
                try { files = Directory.GetFiles(d, "*.*", SearchOption.AllDirectories); }
                catch { continue; }
                foreach (var f in files) if (IsFontFile(f)) yield return f;
            }
        }

        static bool IsFontFile(string p) =>
            p.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".otf", StringComparison.OrdinalIgnoreCase);

        static void ApplyFigForgeFontDefaults(TMP_FontAsset asset)
        {
            if (asset == null) return;
            var path = AssetDatabase.GetAssetPath(asset);
            if (string.IsNullOrEmpty(path) || !path.StartsWith(FontFolder + "/", StringComparison.Ordinal)) return;

            var mat = asset.material;
            if (mat == null) return;
            mat.SetFloat(ShaderUtilities.ID_FaceDilate, Mathf.Clamp(FaceDilate, 0f, 1f));
            EditorUtility.SetDirty(mat);
            EditorUtility.SetDirty(asset);
        }

        // ---- tokenisation / helpers --------------------------------------------
        static HashSet<string> Tokens(string s)
        {
            if (string.IsNullOrEmpty(s)) return new HashSet<string>();
            var spaced = Regex.Replace(s, "([a-z0-9])([A-Z])", "$1 $2"); // camelCase → words
            return new HashSet<string>(
                Regex.Split(spaced, "[^A-Za-z0-9]+").Where(p => p != "").Select(p => p.ToLowerInvariant()));
        }

        static string Norm(string s) => new string((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        static string Safe(string s) => new string((s ?? "Font").Select(c => char.IsLetterOrDigit(c) || c == ' ' ? c : '_').ToArray()).Trim();
        static string ProjectAbs(string assetPath) =>
            Path.Combine(Directory.GetParent(Application.dataPath).FullName, assetPath.Replace('/', Path.DirectorySeparatorChar));
    }
}
