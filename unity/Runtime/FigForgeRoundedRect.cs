// =============================================================================
// FigForge — procedural rounded-rect UI graphic. Drives the FigForge/RoundedRect
// SDF shader from serialized params (corner radius, fill, optional gradient,
// border), so a button background is razor-crisp at any size and matches the
// Figma vector — no exported PNG, no 9-slice. All instances share one material;
// per-rect params are encoded into the quad vertices so uGUI can batch them.
// FigForgeButtonStateColors swaps the fill.
// =============================================================================

using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;
using System.Text;

namespace FigForge
{
    public enum FigForgeFillKind { Solid, Gradient }
    public enum FigForgeGradientKind { Linear, Radial, Angular, Diamond }
    public enum FigForgeStrokeAlign { Inside, Center, Outside }

    // One button-state fill: a solid colour OR a gradient, in a single
    // serialized field. Gradients use Unity's Gradient so any number of stops can
    // be carried through the importer and edited naturally in the inspector.
    [System.Serializable]
    public struct FigForgeFill
    {
        public bool disabled;
        public FigForgeFillKind kind;
        public Color color;       // solid fallback, or the gradient's first stop
        public FigForgeGradientKind gradientKind;
        public Gradient gradient; // null or empty = solid
        public Vector2 dir;       // linear direction / angular-diamond rotation hint

        public FigForgeFill(Color c, Gradient g, Vector2 d, FigForgeGradientKind gradientKind = FigForgeGradientKind.Linear)
        {
            disabled = false;
            kind = FigForgeFillKind.Gradient;
            color = c;
            this.gradientKind = gradientKind;
            gradient = g;
            dir = d;
            Normalize();
        }

        public bool HasGradient =>
            kind == FigForgeFillKind.Gradient
            && gradient != null && gradient.colorKeys != null && gradient.colorKeys.Length > 0
            && !IsSingleColorGradient(gradient);

        public static FigForgeFill Solid(Color c)
        {
            return new FigForgeFill
            {
                disabled = false,
                kind = FigForgeFillKind.Solid,
                color = c,
                gradientKind = FigForgeGradientKind.Linear,
                gradient = null,
                dir = Vector2.zero,
            };
        }

        public static FigForgeFill None => new FigForgeFill
        {
            disabled = true,
            kind = FigForgeFillKind.Solid,
            color = new Color(0, 0, 0, 0),
            gradientKind = FigForgeGradientKind.Linear,
            gradient = null,
            dir = Vector2.zero,
        };

        public static FigForgeFill GradientFill(Gradient g, FigForgeGradientKind kind, Vector2 d, Color fallback)
            => new FigForgeFill(fallback, g, d, kind);
        public static FigForgeFill LinearGradient(Gradient g, Vector2 d, Color fallback)
            => GradientFill(g, FigForgeGradientKind.Linear, d, fallback);

        public void Normalize()
        {
            if (disabled)
            {
                return;
            }

            if (kind == FigForgeFillKind.Solid)
            {
                gradient = null;
                gradientKind = FigForgeGradientKind.Linear;
                dir = Vector2.zero;
                return;
            }

            if (gradient == null || gradient.colorKeys == null || gradient.colorKeys.Length == 0)
            {
                kind = FigForgeFillKind.Solid;
                gradient = null;
                dir = Vector2.zero;
                return;
            }

            if (IsSingleColorGradient(gradient))
            {
                kind = FigForgeFillKind.Solid;
                color = gradient.Evaluate(0f);
                gradient = null;
                gradientKind = FigForgeGradientKind.Linear;
                dir = Vector2.zero;
                return;
            }

            if (dir.sqrMagnitude <= 1e-8f) dir = new Vector2(0f, -1f);
        }

        static bool IsSingleColorGradient(Gradient gradient)
        {
            if (gradient == null) return true;
            Color first = gradient.Evaluate(0f);
            if (!SameColor(first, gradient.Evaluate(1f))) return false;
            var colors = gradient.colorKeys;
            if (colors != null)
                for (int i = 0; i < colors.Length; i++)
                    if (!SameColor(first, gradient.Evaluate(colors[i].time))) return false;
            var alphas = gradient.alphaKeys;
            if (alphas != null)
                for (int i = 0; i < alphas.Length; i++)
                    if (!SameColor(first, gradient.Evaluate(alphas[i].time))) return false;
            return true;
        }

        static bool SameColor(Color a, Color b)
        {
            const float Eps = 0.0005f;
            return Mathf.Abs(a.r - b.r) <= Eps
                && Mathf.Abs(a.g - b.g) <= Eps
                && Mathf.Abs(a.b - b.b) <= Eps
                && Mathf.Abs(a.a - b.a) <= Eps;
        }

        // Same visual fill? Lets state-swap callers (scrollbar/list-row hover) skip
        // re-applying an identical fill: re-applying releases cached surfaces and
        // dirties the page compositor, which on a re-enabled-every-frame caller
        // (scrollbar fade) becomes a full-page recomposite per frame. Distinct
        // Gradient instances count as different (conservative).
        public bool ValueEquals(in FigForgeFill other)
        {
            if (disabled != other.disabled) return false;
            if (disabled) return true;
            return kind == other.kind
                && color == other.color
                && gradientKind == other.gradientKind
                && dir == other.dir
                && ReferenceEquals(gradient, other.gradient);
        }
    }

    [System.Serializable]
    public struct FigForgeStroke
    {
        public bool enabled;
        public FigForgeStrokeStyle style;
        public Color color;
        public float weight;
        public FigForgeStrokeAlign align;
        public float dash;
        public float gap;

        public float Outset => !enabled ? 0f : Mathf.Max(0f, weight) * AlignFactor(align);

        public static FigForgeStroke None => new FigForgeStroke
        {
            enabled = false,
            style = FigForgeStrokeStyle.Solid,
            color = new Color(0, 0, 0, 0),
            weight = 0f,
            align = FigForgeStrokeAlign.Inside,
            dash = 0f,
            gap = 0f,
        };

        public static FigForgeStroke Create(Color color, float weight, FigForgeStrokeAlign align,
                                            bool dashed = false, float dash = 0f, float gap = 0f)
        {
            var stroke = new FigForgeStroke
            {
                enabled = weight > 0.001f && color.a > 0.001f,
                style = dashed ? FigForgeStrokeStyle.Dashed : FigForgeStrokeStyle.Solid,
                color = color,
                weight = Mathf.Max(0f, weight),
                align = align,
                dash = Mathf.Max(0f, dash),
                gap = Mathf.Max(0f, gap),
            };
            return stroke;
        }

        public void Normalize()
        {
            weight = Mathf.Max(0f, weight);
            dash = Mathf.Max(0f, dash);
            gap = Mathf.Max(0f, gap);
            if (style != FigForgeStrokeStyle.Dashed) style = FigForgeStrokeStyle.Solid;
            if (!enabled) return;
            if (weight <= 0.001f) weight = 1f;
            if (color.a <= 0.001f)
            {
                color = color.maxColorComponent <= 0.001f ? Color.black : color;
                color.a = 1f;
            }
        }

        static float AlignFactor(FigForgeStrokeAlign align)
        {
            switch (align)
            {
                case FigForgeStrokeAlign.Outside: return 1f;
                case FigForgeStrokeAlign.Center: return 0.5f;
                default: return 0f;
            }
        }
    }

    [System.Serializable]
    public struct FigForgeShapeStyle
    {
        public FigForgeFill fill;
        public FigForgeStroke stroke;
        public Vector4 corners;
        public Color shadowColor;
        public Vector2 shadowOffset;
        public float shadowBlur;
        public float shadowSpread;

        public FigForgeShapeStyle WithFill(FigForgeFill f)
        {
            fill = f;
            return this;
        }
    }

    [AddComponentMenu("FigForge/Rounded Rect")]
    public class FigForgeRoundedRect : MaskableGraphic
    {
        [SerializeField] FigForgeFill fill = FigForgeFill.Solid(Color.white);
        [SerializeField] FigForgeStroke stroke = FigForgeStroke.None;
        [SerializeField] bool strokeUsesFillGradient = false;
        [SerializeField] Vector4 corners = Vector4.zero;     // per-corner radii px: (tl, tr, br, bl)
        [SerializeField] Color shadowColor = new Color(0, 0, 0, 0); // drop shadow (a==0 → off)
        [SerializeField] Vector2 shadowOffset = Vector2.zero;        // Figma-style px (+y down)
        [SerializeField] float shadowBlur = 0f;              // px
        [SerializeField] float shadowSpread = 0f;            // px

        static Material _sharedMat;
        const AdditionalCanvasShaderChannels SdfChannels =
            AdditionalCanvasShaderChannels.TexCoord1 |
            AdditionalCanvasShaderChannels.TexCoord2 |
            AdditionalCanvasShaderChannels.TexCoord3 |
            AdditionalCanvasShaderChannels.Normal |
            AdditionalCanvasShaderChannels.Tangent;

        // How far the stroke extends OUTSIDE the fill edge (scaled px).
        float StrokeOutset() => stroke.Outset;
        // How far the drop shadow reaches beyond the fill edge (offset + spread + the
        // Gaussian tail + margin). The shader uses sigma = blur/2 and the tail is
        // negligible past ~3·sigma = 1.5·blur, so pad by 1.75·blur to avoid clipping it.
        float ShadowReach() => shadowColor.a <= 0.001f ? 0f
            : Mathf.Max(Mathf.Abs(shadowOffset.x), Mathf.Abs(shadowOffset.y))
              + 1.75f * Mathf.Max(0f, shadowBlur) + Mathf.Max(0f, shadowSpread) + 1f;
        // Total mesh padding so stroke AND shadow have geometry to draw on.
        float MeshPad() => Mathf.Max(StrokeOutset(), ShadowReach());

        public FigForgeFill Fill { get => fill; set => SetFill(value); }

        // Swap the whole fill at runtime (used by FigForgeButtonStateColors for
        // per-state colours). A null/empty gradient or zero direction renders solid.
        public void SetFill(Color fill, Gradient gradient, Vector2 grad)
        {
            SetFill(fill, gradient, FigForgeGradientKind.Linear, grad);
        }

        public void SetFill(Color fill, Gradient gradient, FigForgeGradientKind gradientKind, Vector2 grad)
        {
            SetFill(FigForgeFill.GradientFill(gradient, gradientKind, grad, fill));
        }

        // Backwards-compatible overload for user code compiled against the older
        // two-colour API. New importer code uses the Gradient overload above.
        public void SetFill(Color fill, Color fill2, Vector2 grad)
            => SetFill(grad.sqrMagnitude > 1e-8f ? FigForgeFill.LinearGradient(MakeGradient(fill, fill2), grad, fill) : FigForgeFill.Solid(fill));

        // Swap the whole fill from a single FigForgeFill (solid or gradient).
        public void SetFill(FigForgeFill f) => SetFillInternal(f);
        void SetFillInternal(FigForgeFill f)
        {
            f.Normalize();
            if (fill.ValueEquals(f)) return; // state-swap callers re-apply per frame
            fill = f;
            Push();
        }

        // Per-corner radii px (tl, tr, br, bl) — e.g. list rows matching their container's
        // rounded corners. Keeps fill/stroke/shadow untouched.
        public void SetCorners(Vector4 cornerRadii)
        {
            if (corners == cornerRadii) return;
            corners = cornerRadii;
            Push();
        }

        public void Configure(FigForgeFill fill, FigForgeStroke stroke, Vector4 cornerRadii, bool strokeUsesFillGradient = false)
        {
            SetShapeFields(fill, stroke, cornerRadii, strokeUsesFillGradient);
            Push();
        }

        public void Configure(FigForgeFill fill, Color strokeColor, float strokeWeight, Vector4 cornerRadii, float strokeAlignment = 0f)
            => Configure(fill, FigForgeStroke.Create(strokeColor, strokeWeight, StrokeAlignFromFactor(strokeAlignment)), cornerRadii);

        public void Configure(Color fill, Color fill2, Vector2 grad, Color strokeColor, float strokeWeight, Vector4 cornerRadii, float strokeAlignment = 0f)
            => Configure(grad.sqrMagnitude > 1e-8f ? FigForgeFill.LinearGradient(MakeGradient(fill, fill2), grad, fill) : FigForgeFill.Solid(fill),
                strokeColor, strokeWeight, cornerRadii, strokeAlignment);

        public void SetStyle(FigForgeShapeStyle style)
        {
            SetShapeFields(style.fill, style.stroke, style.corners, false);
            SetShadowFields(style.shadowColor, style.shadowOffset, style.shadowBlur, style.shadowSpread);
            SetVerticesDirty(); // mesh padding may have changed
            Push();
        }

        // Drop shadow behind the shape. color.a==0 → no shadow. offset is Unity-space
        // Figma-style px (+y down); blur/spread in px. The mesh auto-grows to fit the shadow.
        public void SetShadow(Color color, Vector2 offset, float blur, float spread)
        {
            SetShadowFields(color, offset, blur, spread);
            SetVerticesDirty(); // mesh padding may have changed
            Push();
        }

        void SetShapeFields(FigForgeFill fill, FigForgeStroke stroke, Vector4 cornerRadii, bool strokeUsesFillGradient)
        {
            fill.Normalize();
            stroke.Normalize();
            this.fill = fill;
            this.stroke = stroke;
            this.strokeUsesFillGradient = strokeUsesFillGradient && stroke.enabled && fill.HasGradient;
            corners = cornerRadii;
        }

        void SetShadowFields(Color color, Vector2 offset, float blur, float spread)
        {
            shadowColor = color; shadowOffset = offset; shadowBlur = blur; shadowSpread = spread;
        }

        static Material EnsureMat()
        {
            if (_sharedMat == null)
            {
                var sh = Shader.Find("FigForge/RoundedRect");
                if (sh != null) _sharedMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
            }
            return _sharedMat;
        }

        public override Material material { get => EnsureMat() ?? base.material; set { } }
        public override Texture mainTexture
            => !fill.disabled && HasGradient() ? FigForgeGradientTextureCache.Get(fill.gradient) : Texture2D.whiteTexture;

        void EnsureCanvasChannels()
        {
            var c = canvas;
            if (c != null && (c.additionalShaderChannels & SdfChannels) != SdfChannels)
                c.additionalShaderChannels |= SdfChannels;
        }

        void Push()
        {
            EnsureMat();
            EnsureCanvasChannels();
            SetVerticesDirty();
            SetMaterialDirty();
        }

        bool HasGradient() => !fill.disabled && fill.HasGradient;

        // The shader supplies fill/border/gradient; vertex color is reserved for
        // Graphic.color / CanvasRenderer tint.
        protected override void OnPopulateMesh(VertexHelper vh)
        {
            var r = GetPixelAdjustedRect();
            // Pad the quad by the stroke outset AND shadow reach so both have geometry
            // to draw on (the shader maps UV across this padded span and keeps the SDF
            // box at the rect size). UV stays 0..1.
            float pad = MeshPad();
            float x0 = r.x - pad, y0 = r.y - pad, x1 = r.xMax + pad, y1 = r.yMax + pad;
            var c = color;
            var fillColor = !fill.disabled ? fill.color : new Color(0, 0, 0, 0);
            var fillPacked = PackColor(fillColor);
            var strokePacked = PackColor(stroke.enabled ? stroke.color : new Color(0, 0, 0, 0));
            var shadow = PackColor(shadowColor);
            float grad = HasGradient() ? PackSignedUnitPair(fill.dir) : 0f;
            float gradKind = HasGradient() ? (float)fill.gradientKind + 1f : 0f;
            var uv0 = new Vector4(0, 0, fillPacked.x, fillPacked.y);
            var uv1 = new Vector4(gradKind, strokeUsesFillGradient ? 1f : 0f, strokePacked.x, strokePacked.y);
            var uv2 = new Vector4(shadow.x, shadow.y, r.width, r.height);
            var uv3 = new Vector4(grad, corners.x, corners.y, corners.z);
            float strokeWeight = stroke.enabled ? stroke.weight : 0f;
            float signedStrokeWeight = stroke.style == FigForgeStrokeStyle.Dashed ? -strokeWeight : strokeWeight;
            var n = new Vector3(corners.w, signedStrokeWeight, StrokeOutset());
            var t = new Vector4(shadowOffset.x, shadowOffset.y, shadowBlur, shadowSpread);
            vh.Clear();
            AddVert(vh, new Vector3(x0, y0), c, uv0, uv1, uv2, uv3, n, t);
            uv0.y = 1f; AddVert(vh, new Vector3(x0, y1), c, uv0, uv1, uv2, uv3, n, t);
            uv0.x = 1f; AddVert(vh, new Vector3(x1, y1), c, uv0, uv1, uv2, uv3, n, t);
            uv0.y = 0f; AddVert(vh, new Vector3(x1, y0), c, uv0, uv1, uv2, uv3, n, t);
            vh.AddTriangle(0, 1, 2);
            vh.AddTriangle(2, 3, 0);
        }

        static void AddVert(VertexHelper vh, Vector3 pos, Color32 color, Vector4 uv0, Vector4 uv1, Vector4 uv2, Vector4 uv3, Vector3 normal, Vector4 tangent)
        {
            var v = UIVertex.simpleVert;
            v.position = pos;
            v.color = color;
            v.uv0 = uv0;
            v.uv1 = uv1;
            v.uv2 = uv2;
            v.uv3 = uv3;
            v.normal = normal;
            v.tangent = tangent;
            vh.AddVert(v);
        }

        static Vector2 PackColor(Color c)
            => new Vector2(PackBytes(c.r, c.g), PackBytes(c.b, c.a));

        static float PackBytes(float a, float b)
        {
            int ai = Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(a) * 255f), 0, 255);
            int bi = Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(b) * 255f), 0, 255);
            return (ai * 256 + bi) / 65535f;
        }

        static float PackSignedUnitPair(Vector2 v)
        {
            if (v.sqrMagnitude <= 1e-8f) return 0f;
            v = Vector2.ClampMagnitude(v, 1f);
            int x = Mathf.Clamp(Mathf.RoundToInt((v.x * 0.5f + 0.5f) * 255f), 0, 255);
            int y = Mathf.Clamp(Mathf.RoundToInt((v.y * 0.5f + 0.5f) * 255f), 0, 255);
            return (x * 256 + y + 1) / 65536f;
        }

        static Gradient MakeGradient(Color a, Color b)
        {
            var g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(a, 0f), new GradientColorKey(b, 1f) },
                new[] { new GradientAlphaKey(a.a, 0f), new GradientAlphaKey(b.a, 1f) });
            return g;
        }

        static FigForgeStrokeAlign StrokeAlignFromFactor(float factor)
        {
            if (factor >= 0.75f) return FigForgeStrokeAlign.Outside;
            if (factor >= 0.25f) return FigForgeStrokeAlign.Center;
            return FigForgeStrokeAlign.Inside;
        }

        protected override void OnEnable() { base.OnEnable(); Push(); }
        protected override void OnCanvasHierarchyChanged() { base.OnCanvasHierarchyChanged(); EnsureCanvasChannels(); }
        protected override void OnRectTransformDimensionsChange() { base.OnRectTransformDimensionsChange(); Push(); }
#if UNITY_EDITOR
        protected override void OnValidate() { fill.Normalize(); stroke.Normalize(); base.OnValidate(); Push(); }
#endif
    }

    static class FigForgeGradientTextureCache
    {
        const int Res = 256;
        // HideAndDontSave Texture2Ds accumulate one per distinct gradient and are never
        // scene-collected, so cap residency by a byte budget and evict the least-recently-
        // used entry (mirrors FigForgeCompositorCache.Trim). Each entry is Res*4 = 1KB.
        const long MaxBytes = 16L * 1024L * 1024L;
        static readonly Dictionary<string, Entry> _mem = new Dictionary<string, Entry>();
        static long _bytes;
        static int _clock;

#if UNITY_EDITOR
        // Statics reset on domain reload but the HideAndDontSave textures survive it —
        // without this hook every reload strands the whole cache on the GPU until the
        // editor closes (matches FigForgeCompositorCache).
        static FigForgeGradientTextureCache()
        {
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += Clear;
        }
#endif

        public static Texture2D Get(Gradient gradient)
        {
            if (gradient == null) return Texture2D.whiteTexture;
            string key = Key(gradient);
            if (_mem.TryGetValue(key, out var cached) && cached.texture != null)
            {
                cached.lastUsed = ++_clock;
                _mem[key] = cached;
                return cached.texture;
            }

            var tex = new Texture2D(Res, 1, TextureFormat.RGBA32, false)
            {
                name = "FigForgeGradient_" + key,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave,
            };
            var px = new Color[Res];
            for (int x = 0; x < Res; x++)
                px[x] = gradient.Evaluate((float)x / (Res - 1));
            tex.SetPixels(px);
            tex.Apply(false, true);
            _mem[key] = new Entry { texture = tex, bytes = (long)Res * 4L, lastUsed = ++_clock };
            _bytes += (long)Res * 4L;
            Trim();
            return tex;
        }

        static void Trim()
        {
            while (_bytes > MaxBytes && _mem.Count > 1)
            {
                string oldestKey = null;
                Entry oldest = default;
                foreach (var kv in _mem)
                {
                    if (oldestKey == null || kv.Value.lastUsed < oldest.lastUsed)
                    {
                        oldestKey = kv.Key;
                        oldest = kv.Value;
                    }
                }
                if (oldestKey == null) break;
                _mem.Remove(oldestKey);
                _bytes -= oldest.bytes;
                DestroyTexture(oldest.texture);
            }
        }

        public static void Clear()
        {
            foreach (var kv in _mem)
                DestroyTexture(kv.Value.texture);
            _mem.Clear();
            _bytes = 0;
        }

        // HideAndDontSave textures are never scene-collected, so evicting one needs an
        // explicit Destroy (matches FigForgeCompositorCache.ReleaseRT).
        static void DestroyTexture(Texture2D tex)
        {
            if (tex == null) return;
            if (Application.isPlaying) Object.Destroy(tex);
            else Object.DestroyImmediate(tex);
        }

        struct Entry
        {
            public Texture2D texture;
            public long bytes;
            public int lastUsed;
        }

        static string Key(Gradient gradient)
        {
            var sb = new StringBuilder();
            var colors = gradient.colorKeys;
            var alphas = gradient.alphaKeys;
            for (int i = 0; i < colors.Length; i++)
            {
                var k = colors[i];
                sb.Append('c').Append(Mathf.RoundToInt(k.time * 10000f)).Append('_')
                  .Append(Mathf.RoundToInt(k.color.r * 255f)).Append('_')
                  .Append(Mathf.RoundToInt(k.color.g * 255f)).Append('_')
                  .Append(Mathf.RoundToInt(k.color.b * 255f)).Append(';');
            }
            for (int i = 0; i < alphas.Length; i++)
            {
                var k = alphas[i];
                sb.Append('a').Append(Mathf.RoundToInt(k.time * 10000f)).Append('_')
                  .Append(Mathf.RoundToInt(k.alpha * 255f)).Append(';');
            }
            return StableHash(sb.ToString()).ToString("X8");
        }

        static uint StableHash(string text)
        {
            unchecked
            {
                uint hash = 2166136261u;
                for (int i = 0; i < text.Length; i++)
                {
                    hash ^= text[i];
                    hash *= 16777619u;
                }
                return hash;
            }
        }
    }
}
