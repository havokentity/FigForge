// =============================================================================
// FigForge — auto font import. Resolves a Figma (family, style) to a
// TMP_FontAsset, generating one on first sight from the best-matching font file:
// an existing TMP asset, then a .ttf/.otf in the project, then a file in the OS
// font folders (copied in). Generated assets are dynamic SDF (glyphs bake on
// demand) and named after their source file so the real weight is visible (faux
// bold/italic only applies when the chosen file isn't that weight).
//
// Matching is token-based and tiered so "Arial" doesn't grab "Arial Unicode" and
// a Bold request prefers "Arial Bold" over "Arial Bold Italic".
//
// The Figma plugin can't ship the font binary (Figma exposes font *names*, not
// the file), so the file must already exist on this machine; if it doesn't we
// fall back to TMP's default and log exactly what to add.
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using TMPro;
using UnityEditor;
using UnityEngine;

namespace FigForge
{
    public static class FontAutoImporter
    {
        const string FontFolder = "Assets/FigForge/Fonts";
        static readonly Dictionary<string, TMP_FontAsset> _cache = new Dictionary<string, TMP_FontAsset>();
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
            if (_cache.TryGetValue(key, out var hit)) return hit;

            var existing = BestExisting(family, style);
            TMP_FontAsset asset;
            if (existing.tier == 0) asset = existing.asset;       // already have the exact face
            else asset = Generate(family, style, log) ?? existing.asset;

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
                if (prior != null) return prior; // reuse

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
                AssetDatabase.SaveAssets();
                AssetDatabase.ImportAsset(outPath);
                log?.Invoke($"auto-imported font '{family} {style}' → {outPath} (source: {src})");
                return AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(outPath);
            }
            catch (Exception e)
            {
                log?.Invoke($"font auto-import failed for '{family} {style}': {e.Message} — using default");
                return null;
            }
        }

        // Best .ttf/.otf for (family, style) across project + OS by tier; copies an
        // OS file into the project when it's the winner.
        static string FindFontFile(string family, string style)
        {
            var candidates = AssetDatabase.FindAssets("t:Font").Select(AssetDatabase.GUIDToAssetPath)
                .Where(IsFontFile).Concat(OsFontFiles());
            string chosen = null; int bestTier = int.MaxValue;
            foreach (var p in candidates)
            {
                int t = Tier(Path.GetFileNameWithoutExtension(p), family, style);
                if (t < 0 || t >= bestTier) continue;
                bestTier = t; chosen = p;
                if (t == 0 && p.StartsWith("Assets/")) break; // can't beat an in-project exact match
            }
            if (chosen == null) return null;
            if (chosen.StartsWith("Assets/")) return chosen;

            TextureImportHelper.EnsureFolder(FontFolder);
            string dest = $"{FontFolder}/{Path.GetFileName(chosen)}";
            try { File.Copy(chosen, ProjectAbs(dest), true); AssetDatabase.ImportAsset(dest); return dest; }
            catch { return null; }
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
