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
using UnityEditor;
using UnityEngine;

namespace FigForge
{
    public static class RoundedRectSpriteCache
    {
        const string Folder = "Assets/FigForge/_Generated/Rounded";
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
            AssetDatabase.SaveAssets();

            _mem[radius] = sprite;
            return sprite;
        }
    }

    // Rounded (or square) outline ring, 9-sliced, for real stroke borders.
    public static class RoundedOutlineSpriteCache
    {
        const string Folder = "Assets/FigForge/_Generated/Outline";
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
            AssetDatabase.SaveAssets();
            _mem[key] = sprite;
            return sprite;
        }
    }

    public static class GradientSpriteCache
    {
        const string Folder = "Assets/FigForge/_Generated/Gradient";
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
            AssetDatabase.SaveAssets();

            _mem[key] = sprite;
            return sprite;
        }

        static Color32 Sample(List<GradientStop> stops, float t)
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

        static string Key(Fill fill)
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
}
