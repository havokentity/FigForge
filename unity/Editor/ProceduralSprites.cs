// =============================================================================
// FigForge — procedural sprite caches.
//
// RoundedRectSpriteCache: white 9-sliced rounded-rect sprites (tint via
// Image.color, Image.Type.Sliced keeps the radius constant at any size). Used to
// render fill-only rounded panels that have no baked PNG — so they keep their
// real colour/transparency AND rounded corners instead of a sharp/white box.
//
// GradientSpriteCache: bakes a linear/radial gradient texture from manifest
// stops so gradient fills survive into Unity instead of collapsing to a solid.
//
// Both persist generated sprites as assets so they survive scene save/reload.
// =============================================================================

using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace FigForge
{
    public static class RoundedRectSpriteCache
    {
        const string Folder = "Assets/FigForge/_GeneratedSprites/Rounded";
        static readonly Dictionary<int, Sprite> _mem = new Dictionary<int, Sprite>();

        public static Sprite Get(int radius)
        {
            radius = Mathf.Clamp(radius, 1, 256);
            if (_mem.TryGetValue(radius, out var cached) && cached != null) return cached;

            string path = $"{Folder}/RoundedRect_{radius}.asset";
            var existing = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (existing != null) { _mem[radius] = existing; return existing; }

            ProceduralSpriteUtil.EnsureFolder(Folder);

            int size = radius * 2 + 2; // 2px flat centre for the 9-slice middle
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = $"RoundedRect_{radius}_Tex",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };

            var px = new Color32[size * size];
            float cxL = radius, cxR = size - radius, cyB = radius, cyT = size - radius;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float fx = x + 0.5f, fy = y + 0.5f;
                    float dx = fx < cxL ? cxL - fx : (fx > cxR ? fx - cxR : 0f);
                    float dy = fy < cyB ? cyB - fy : (fy > cyT ? fy - cyT : 0f);
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01(radius - dist + 0.5f); // 1 inside, 1px AA at edge
                    px[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f));
                }
            }
            tex.SetPixels32(px);
            tex.Apply();

            var border = new Vector4(radius, radius, radius, radius);
            var sprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f),
                100f, 0, SpriteMeshType.FullRect, border);
            sprite.name = $"RoundedRect_{radius}";

            AssetDatabase.CreateAsset(sprite, path);
            AssetDatabase.AddObjectToAsset(tex, sprite);
            // No per-Get SaveAssets — it flushes ALL dirty assets, so calling it
            // once per generated sprite stalls the build. The in-memory `sprite` is
            // returned directly (not reloaded), and the importer's end-of-build
            // finally flushes everything created this pass.

            _mem[radius] = sprite;
            return sprite;
        }
    }

    // Soft (blurred-edge) rounded-rect sprite for shader-free DROP SHADOWS: a rounded
    // rect of corner `radius` whose alpha feathers from 1 to 0 over `blur` px outside
    // the boundary. 9-sliced (border = radius + blur) and tinted via Image.color, so
    // one sprite per (radius, blur) serves every shadow at that size. No shader — just
    // a transparent PNG on the default UI material.
    public static class SoftRoundedRectSpriteCache
    {
        const string Folder = "Assets/FigForge/_GeneratedSprites/SoftRect";
        static readonly Dictionary<string, Sprite> _mem = new Dictionary<string, Sprite>();

        public static Sprite Get(int radius, int blur)
        {
            radius = Mathf.Clamp(radius, 0, 256);
            blur = Mathf.Clamp(blur, 1, 128);
            string key = $"{radius}_{blur}";
            if (_mem.TryGetValue(key, out var cached) && cached != null) return cached;

            string path = $"{Folder}/Soft_{key}.asset";
            var existing = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (existing != null) { _mem[key] = existing; return existing; }

            ProceduralSpriteUtil.EnsureFolder(Folder);

            int border = radius + blur;
            int size = border * 2 + 2; // 2px solid centre for the 9-slice middle
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = $"Soft_{key}_Tex", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear,
            };
            var px = new Color32[size * size];
            // Corner centres sit `border` in from each edge; alpha is 1 within `radius`
            // of them (the solid rounded core) then falls off over `blur` px.
            float cxL = border, cxR = size - border, cyB = border, cyT = size - border;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float fx = x + 0.5f, fy = y + 0.5f;
                    float dx = fx < cxL ? cxL - fx : (fx > cxR ? fx - cxR : 0f);
                    float dy = fy < cyB ? cyB - fy : (fy > cyT ? fy - cyT : 0f);
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01((radius + blur - dist) / blur);
                    px[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f));
                }
            }
            tex.SetPixels32(px); tex.Apply();
            var b = new Vector4(border, border, border, border);
            var sprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f),
                100f, 0, SpriteMeshType.FullRect, b);
            sprite.name = $"Soft_{key}";
            AssetDatabase.CreateAsset(sprite, path);
            AssetDatabase.AddObjectToAsset(tex, sprite);
            // No per-Get SaveAssets — flushed once at end-of-build (see RoundedRectSpriteCache).
            _mem[key] = sprite;
            return sprite;
        }
    }

    // Rounded (or square) outline ring, 9-sliced, for real stroke borders.
    public static class RoundedOutlineSpriteCache
    {
        const string Folder = "Assets/FigForge/_GeneratedSprites/Outline";
        static readonly Dictionary<string, Sprite> _mem = new Dictionary<string, Sprite>();

        public static Sprite Get(int radius, int thickness)
        {
            radius = Mathf.Clamp(radius, 0, 256);
            thickness = Mathf.Clamp(thickness, 1, 64);
            string key = $"{radius}_{thickness}";
            if (_mem.TryGetValue(key, out var cached) && cached != null) return cached;

            string path = $"{Folder}/Outline_{key}.asset";
            var existing = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (existing != null) { _mem[key] = existing; return existing; }

            ProceduralSpriteUtil.EnsureFolder(Folder);

            int border = Mathf.Max(radius, thickness) + 1;
            int size = border * 2 + 2;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = $"Outline_{key}_Tex", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear,
            };
            var px = new Color32[size * size];
            float cxL = border, cxR = size - border, cyB = border, cyT = size - border;
            float r = radius;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float fx = x + 0.5f, fy = y + 0.5f;
                    float dx = fx < cxL ? cxL - fx : (fx > cxR ? fx - cxR : 0f);
                    float dy = fy < cyB ? cyB - fy : (fy > cyT ? fy - cyT : 0f);
                    // distance from the nearest edge of the rounded boundary
                    float corner = Mathf.Sqrt(dx * dx + dy * dy);
                    float edgeDist = r > 0 ? r - corner : Mathf.Min(fx, size - fx, fy, size - fy);
                    // ring: visible within `thickness` px inside the boundary
                    float a = Mathf.Clamp01(edgeDist + 0.5f) * Mathf.Clamp01(thickness - edgeDist + 0.5f);
                    px[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(Mathf.Clamp01(a) * 255f));
                }
            }
            tex.SetPixels32(px); tex.Apply();
            var b = new Vector4(border, border, border, border);
            var sprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f),
                100f, 0, SpriteMeshType.FullRect, b);
            sprite.name = $"Outline_{key}";
            AssetDatabase.CreateAsset(sprite, path);
            AssetDatabase.AddObjectToAsset(tex, sprite);
            // No per-Get SaveAssets — flushed once at end-of-build (see RoundedRectSpriteCache).
            _mem[key] = sprite;
            return sprite;
        }
    }

    public static class GradientSpriteCache
    {
        const string Folder = "Assets/FigForge/_GeneratedSprites/Gradient";
        const int Res = 128;
        static readonly Dictionary<string, Sprite> _mem = new Dictionary<string, Sprite>();

        public static Sprite Get(Fill fill)
        {
            if (fill == null || fill.stops == null || fill.stops.Count == 0) return null;
            string key = Key(fill);
            if (_mem.TryGetValue(key, out var cached) && cached != null) return cached;

            string path = $"{Folder}/Grad_{key}.asset";
            var existing = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (existing != null) { _mem[key] = existing; return existing; }

            ProceduralSpriteUtil.EnsureFolder(Folder);

            bool radial = fill.gradient == "radial" || fill.gradient == "diamond";
            var tex = new Texture2D(Res, Res, TextureFormat.RGBA32, false)
            {
                name = $"Grad_{key}_Tex",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            var px = new Color32[Res * Res];
            var cx = (Res - 1) * 0.5f;
            var cy = (Res - 1) * 0.5f;
            var maxR = Mathf.Sqrt(cx * cx + cy * cy);
            for (int y = 0; y < Res; y++)
            {
                for (int x = 0; x < Res; x++)
                {
                    float t = radial
                        ? Mathf.Clamp01(Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) / maxR)
                        : (float)x / (Res - 1);
                    px[y * Res + x] = Sample(fill.stops, t);
                }
            }
            tex.SetPixels32(px);
            tex.Apply();

            var sprite = Sprite.Create(tex, new Rect(0, 0, Res, Res), new Vector2(0.5f, 0.5f), 100f);
            sprite.name = $"Grad_{key}";
            AssetDatabase.CreateAsset(sprite, path);
            AssetDatabase.AddObjectToAsset(tex, sprite);
            // No per-Get SaveAssets — flushed once at end-of-build (see RoundedRectSpriteCache).

            _mem[key] = sprite;
            return sprite;
        }

        internal static Color32 Sample(List<GradientStop> stops, float t)
        {
            GradientStop a = stops[0], b = stops[stops.Count - 1];
            for (int i = 0; i < stops.Count - 1; i++)
            {
                if (t >= stops[i].position && t <= stops[i + 1].position) { a = stops[i]; b = stops[i + 1]; break; }
            }
            float span = Mathf.Max(0.0001f, b.position - a.position);
            float k = Mathf.Clamp01((t - a.position) / span);
            Color ca = ToColor(a.color), cb = ToColor(b.color);
            return Color.Lerp(ca, cb, k);
        }

        static Color ToColor(float[] c) =>
            c != null && c.Length >= 4 ? new Color(c[0], c[1], c[2], c[3]) : Color.white;

        internal static string Key(Fill fill)
        {
            var sb = new System.Text.StringBuilder(fill.gradient ?? "linear");
            foreach (var s in fill.stops)
            {
                sb.Append('_').Append(Mathf.RoundToInt(s.position * 100));
                if (s.color != null)
                    foreach (var ch in s.color) sb.Append(Mathf.RoundToInt(ch * 255)).Append('x');
            }
            return sb.ToString().Replace('.', '_');
        }
    }

    // Gradient baked WITH anti-aliased rounded-rect corners, at the element's pixel size — for
    // vanilla gradient fills that must respect cornerRadius. A flat gradient sprite renders as a
    // square box, and a stencil mask only yields a hard/fringed edge that overshoots the stroke;
    // baking the rounded alpha in matches the solid RoundedRect fill's corners exactly. Per-size
    // (the gradient can't 9-slice — it varies across the rect), so it won't reflow if the element
    // resizes at runtime; vanilla UI is static. Texture capped at 1024 on the long side.
    public static class RoundedGradientSpriteCache
    {
        const string Folder = "Assets/FigForge/_GeneratedSprites/RoundedGradient";
        static readonly Dictionary<string, Sprite> _mem = new Dictionary<string, Sprite>();

        // Bakes the gradient AND (optionally) its stroke border into ONE sprite from a single
        // rounded-rect SDF, so fill and border are pixel-perfectly aligned BY CONSTRUCTION — no
        // separate stroke sprite to line up, no magic offset. strokeColor null / thickness <= 0 →
        // fill only. The outer rounded edge is the shape edge (AA); the outer `strokeThickness` px
        // are the stroke, everything inside is the gradient.
        public static Sprite Get(Fill fill, int radius, int w, int h, float[] strokeColor, int strokeThickness)
        {
            if (fill == null || fill.stops == null || fill.stops.Count == 0 || w < 4 || h < 4) return null;

            float scale = Mathf.Max(w, h) > 2048 ? 2048f / Mathf.Max(w, h) : 1f; // cap raised for 2× supersample
            int tw = Mathf.Clamp(Mathf.RoundToInt(w * scale), 4, 2048);
            int th = Mathf.Clamp(Mathf.RoundToInt(h * scale), 4, 2048);
            int tr = Mathf.Clamp(Mathf.RoundToInt(radius * scale), 0, Mathf.Min(tw, th) / 2);
            bool hasStroke = strokeColor != null && strokeColor.Length >= 4 && strokeThickness > 0;
            float st = hasStroke ? strokeThickness * scale : 0f;
            Color sc = hasStroke ? new Color(strokeColor[0], strokeColor[1], strokeColor[2], strokeColor[3]) : Color.clear;

            string key = $"{GradientSpriteCache.Key(fill)}_{tw}x{th}_r{tr}"
                       + (hasStroke ? $"_s{Mathf.RoundToInt(st)}_{(int)(sc.r * 255)}.{(int)(sc.g * 255)}.{(int)(sc.b * 255)}.{(int)(sc.a * 255)}" : "");
            if (_mem.TryGetValue(key, out var cached) && cached != null) return cached;
            string path = $"{Folder}/RG_{key}.asset";
            var existing = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (existing != null) { _mem[key] = existing; return existing; }
            ProceduralSpriteUtil.EnsureFolder(Folder);

            bool radial = fill.gradient == "radial" || fill.gradient == "diamond";
            var tex = new Texture2D(tw, th, TextureFormat.RGBA32, false)
            { name = $"RG_{key}_Tex", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color32[tw * th];
            float cx = (tw - 1) * 0.5f, cy = (th - 1) * 0.5f, maxR = Mathf.Sqrt(cx * cx + cy * cy);
            float hx = tw * 0.5f, hy = th * 0.5f;
            for (int y = 0; y < th; y++)
                for (int x = 0; x < tw; x++)
                {
                    float t = radial
                        ? Mathf.Clamp01(Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) / maxR)
                        : (float)x / (tw - 1);
                    Color c = GradientSpriteCache.Sample(fill.stops, t);
                    // signed distance to the outer rounded-rect boundary (negative inside)
                    float qx = Mathf.Abs(x + 0.5f - hx) - (hx - tr);
                    float qy = Mathf.Abs(y + 0.5f - hy) - (hy - tr);
                    float outside = Mathf.Sqrt(Mathf.Max(qx, 0f) * Mathf.Max(qx, 0f) + Mathf.Max(qy, 0f) * Mathf.Max(qy, 0f));
                    float dist = outside + Mathf.Min(Mathf.Max(qx, qy), 0f) - tr;
                    // outer `st` px = stroke (lerp gradient → stroke, AA at the inner edge); rest = gradient
                    if (st > 0f) c = Color.Lerp(c, sc, Mathf.Clamp01(dist + st + 0.5f));
                    c.a *= Mathf.Clamp01(0.5f - dist); // outer AA coverage
                    px[y * tw + x] = c;
                }
            tex.SetPixels32(px); tex.Apply();

            var sprite = Sprite.Create(tex, new Rect(0, 0, tw, th), new Vector2(0.5f, 0.5f), 100f);
            sprite.name = $"RG_{key}";
            AssetDatabase.CreateAsset(sprite, path);
            AssetDatabase.AddObjectToAsset(tex, sprite);
            // No per-Get SaveAssets — flushed once at end-of-build (see RoundedRectSpriteCache).
            _mem[key] = sprite;
            return sprite;
        }
    }

    static class ProceduralSpriteUtil
    {
        public static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            var parts = folder.Split('/');
            string cur = parts[0]; // "Assets"
            for (int i = 1; i < parts.Length; i++)
            {
                string next = $"{cur}/{parts[i]}";
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(cur, parts[i]);
                cur = next;
            }
        }
    }

    // The caches above persist one .asset per distinct key, and the key includes the
    // element's exact pixel size (RoundedGradientSpriteCache especially — up to 2048×2048
    // RGBA32). A re-Forge after a resize generates fresh assets under new keys; the old
    // ones are never deleted, so _GeneratedSprites grows unbounded across edit cycles.
    //
    // Auto-sweeping at import start is NOT safe here: BuildPageProject rebuilds ONE project,
    // but _GeneratedSprites is shared across every imported page in the scene/project, so a
    // blind clear would delete sprites still referenced by pages that aren't being rebuilt
    // this pass. Instead this is an explicit, dependency-checked cleanup: it deletes only
    // generated sprites that NOTHING in the project (scenes + prefabs) references, so a live
    // import that still points at a sprite always keeps it.
    static class ProceduralSpriteCleanup
    {
        const string Root = "Assets/FigForge/_GeneratedSprites";

        [MenuItem("Window/FigForge/Clean Up Orphaned Generated Sprites")]
        public static void CleanUpOrphans()
        {
            if (!AssetDatabase.IsValidFolder(Root))
            {
                EditorUtility.DisplayDialog("FigForge", "No generated sprites folder to clean.", "OK");
                return;
            }

            // Every generated sprite asset under _GeneratedSprites.
            var generated = AssetDatabase.FindAssets("t:Sprite", new[] { Root })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => p.StartsWith(Root + "/", System.StringComparison.Ordinal))
                .Distinct()
                .ToList();
            if (generated.Count == 0)
            {
                EditorUtility.DisplayDialog("FigForge", "No generated sprites found.", "OK");
                return;
            }

            // Build the set of generated sprites referenced by any scene or prefab in the
            // project. GetDependencies(recursive) walks the serialized references of each
            // scene/prefab, so a sprite reachable from a built page (in-scene or saved prefab)
            // is kept; only truly unreferenced assets are removed.
            var referenced = new HashSet<string>();
            var consumers = AssetDatabase.FindAssets("t:Scene t:Prefab")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Distinct();
            foreach (var c in consumers)
                foreach (var dep in AssetDatabase.GetDependencies(c, true))
                    if (dep.StartsWith(Root + "/", System.StringComparison.Ordinal))
                        referenced.Add(dep);

            var orphans = generated.Where(p => !referenced.Contains(p)).ToList();
            if (orphans.Count == 0)
            {
                EditorUtility.DisplayDialog("FigForge",
                    $"No orphaned generated sprites — all {generated.Count} are referenced.", "OK");
                return;
            }

            if (!EditorUtility.DisplayDialog("FigForge — Clean Up Generated Sprites",
                $"Delete {orphans.Count} of {generated.Count} generated sprite asset(s) that no scene "
                + "or prefab references?\n\nReferenced sprites are kept.", "Delete", "Cancel"))
                return;

            var failed = new List<string>();
            AssetDatabase.DeleteAssets(orphans.ToArray(), failed);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            int deleted = orphans.Count - failed.Count;
            Debug.Log($"[FigForge] cleaned up {deleted} orphaned generated sprite(s)"
                + (failed.Count > 0 ? $" ({failed.Count} could not be deleted)" : "") + ".");
        }
    }
}
