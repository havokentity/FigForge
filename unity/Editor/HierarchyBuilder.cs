// =============================================================================
// FigForge — turns a parsed manifest into a uGUI hierarchy.
//
// Supports: constraint-driven anchors (offsetMin/offsetMax straight from the
// manifest), sprite images, solid/gradient fills, rounded fill-only panels,
// real stroke borders, rotation, text (TMP), clipping, auto-layout, and
// canonical UI elements (a `Btn_*` element becomes an instance of a library
// prefab instead of being rebuilt).
// =============================================================================

using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace FigForge
{
    public class BuildContext
    {
        public float scaleFactor = 1f;
        public Dictionary<string, Sprite> sprites = new Dictionary<string, Sprite>();
        public Func<string, string, TMP_FontAsset> resolveFont;
        public CanonicalLibrary canonical;
        public bool disableRaycasts = true;
        public Action<string> log = _ => { };
        // name → GameObject collected during a build, applied to the page's FigForgeScreen.
        public readonly List<KeyValuePair<string, GameObject>> registered = new List<KeyValuePair<string, GameObject>>();
        // element id → built GameObject, so the codegen can register members for typed wiring.
        public readonly Dictionary<string, GameObject> byElementId = new Dictionary<string, GameObject>();
    }

    public static class HierarchyBuilder
    {
        public static GameObject BuildPage(Manifest manifest, Transform parent, BuildContext ctx)
        {
            var index = ManifestParser.Index(manifest);
            var roots = ManifestParser.Roots(manifest);
            if (roots.Count == 0) { ctx.log("no root element in manifest"); return null; }

            ctx.registered.Clear();

            // One Figma frame = one root. If several, wrap them under a page root.
            GameObject pageRoot;
            if (roots.Count == 1)
            {
                pageRoot = BuildElement(roots[0], index, parent, ctx);
            }
            else
            {
                pageRoot = NewRect(manifest.screen.name, parent);
                Stretch(pageRoot.GetComponent<RectTransform>());
                foreach (var r in roots) BuildElement(r, index, pageRoot.transform, ctx);
            }

            // Registry of named controls, for code to fetch by Figma name.
            if (pageRoot != null && ctx.registered.Count > 0)
            {
                var reg = pageRoot.GetComponent<FigForgeScreen>() ?? pageRoot.AddComponent<FigForgeScreen>();
                foreach (var kv in ctx.registered) reg.Register(kv.Key, kv.Value);
            }
            ConfigurePageCompositor(pageRoot, ctx);
            WarmUpGeneratedGraphics(pageRoot);
            return pageRoot;
        }

        static void ConfigurePageCompositor(GameObject pageRoot, BuildContext ctx)
        {
            if (pageRoot == null) return;
            var sources = pageRoot.GetComponentsInChildren<IFigForgeCompositorSource>(true);
            bool needsPageCompositor = false;
            for (int i = 0; i < sources.Length; i++)
            {
                var layer = sources[i];
                if (layer == null || !layer.RequiresPageCompositor) continue;
                needsPageCompositor = true;
                WarnIfAdvancedBlendUnderStencilMask(layer, ctx);
            }

            if (!needsPageCompositor) return;
            if (pageRoot.GetComponent<FigForgePageCompositor>() == null)
                pageRoot.AddComponent<FigForgePageCompositor>();
        }

        static void WarnIfAdvancedBlendUnderStencilMask(IFigForgeCompositorSource layer, BuildContext ctx)
        {
            var masks = layer.transform.GetComponentsInParent<Mask>(true);
            for (int i = 0; i < masks.Length; i++)
            {
                var mask = masks[i];
                if (mask == null || mask.transform == layer.transform) continue;
                ctx.log($"advanced blend '{HierarchyPath(layer.transform)}' is under a stencil Mask; FigForgePageCompositor MVP does not reproduce stencil masking.");
                return;
            }
        }

        static string HierarchyPath(Transform t)
        {
            if (t == null) return "";
            var parts = new List<string>();
            while (t != null)
            {
                parts.Add(t.name);
                t = t.parent;
            }
            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }

        // Warm the generated SDF graphics so the SceneView shows them crisp right after
        // import. Done ASYNC: a big page would stall the editor if we built every
        // gradient texture + dirtied every mesh + forced a canvas rebuild in one tick.
        // The SDF shaders are SINGLE-VARIANT, so we compile each shader ONCE (not per
        // rect), then spread the per-rect texture/dirty work across editor frames.
        const int WarmUpBatchPerTick = 64;

        static void WarmUpGeneratedGraphics(GameObject pageRoot)
        {
            if (pageRoot == null) return;

            EditorApplication.delayCall += () =>
            {
                if (pageRoot == null) return;
                var rects = pageRoot.GetComponentsInChildren<FigForgeRoundedRect>(true);
                var layered = pageRoot.GetComponentsInChildren<FigForgeLayeredRect>(true);

                var all = new List<Graphic>(rects.Length + layered.Length);
                for (int i = 0; i < rects.Length; i++) if (rects[i] != null) all.Add(rects[i]);
                for (int i = 0; i < layered.Length; i++) if (layered[i] != null) all.Add(layered[i]);
                if (all.Count == 0) return;

                // One-time shader compile per shader (single variant → first SetPass compiles,
                // the rest were redundant). Warming the first instance of each type is enough.
                WarmShaderOnce(rects.Length > 0 ? rects[0] : null);
                WarmShaderOnce(layered.Length > 0 ? layered[0] : null);

                // Build gradient textures (deduped/cached) + dirty meshes in small batches,
                // re-scheduling onto the next editor tick — Unity's per-tick canvas update
                // then rebuilds each batch, so the cost is amortized instead of one stall.
                int idx = 0;
                void Step()
                {
                    if (pageRoot == null) return;
                    int end = Mathf.Min(idx + WarmUpBatchPerTick, all.Count);
                    for (; idx < end; idx++)
                    {
                        var g = all[idx];
                        if (g == null) continue;
                        _ = g.mainTexture; // builds/caches the gradient texture (deduped)
                        g.SetVerticesDirty();
                        g.SetMaterialDirty();
                    }
                    if (idx < all.Count) { EditorApplication.delayCall += Step; return; }
                    Canvas.ForceUpdateCanvases();
                    SceneView.RepaintAll();
                }
                Step();
            };
        }

        static void WarmShaderOnce(Graphic g)
        {
            if (g == null) return;
            var mat = g.materialForRendering;
            if (mat != null)
            {
                try { mat.SetPass(0); } catch { /* shader may compile lazily in SceneView */ }
            }
        }

        static GameObject BuildElement(ElementData e, Dictionary<string, ElementData> index, Transform parent, BuildContext ctx)
        {
            // ---- Canonical element: instantiate a prefab (generated once from the
            // Figma component, or a hand-made one from the CanonicalLibrary) ------
            if (e.canonical != null)
            {
                string canonicalKind = string.IsNullOrEmpty(e.canonical.kind) ? "button" : e.canonical.kind;
                var prefab = ResolveOrGenerateCanonicalPrefab(e, ctx);
                GameObject inst;
                if (prefab != null)
                {
                    inst = (GameObject)UnityEditor.PrefabUtility.InstantiatePrefab(prefab, parent);
                    inst.name = e.name;
                }
                else if (canonicalKind == "button" && e.canonical.shape != null)
                {
                    inst = BuildShapeButton(e, parent, ctx); // fallback: inline SDF shader
                }
                else if (canonicalKind == "button" && e.canonical.states != null)
                {
                    inst = BuildStateButton(e, parent, ctx); // fallback: inline state PNGs
                }
                else if ((canonicalKind == "toggle" || canonicalKind == "radio") && e.canonical.shape != null)
                {
                    inst = BuildToggle(e, parent, ctx);
                }
                else if (canonicalKind == "input")
                {
                    inst = BuildInputField(e, parent, ctx);
                }
                else if (canonicalKind == "dropdown")
                {
                    inst = BuildDropdown(e, parent, ctx);
                }
                else if (canonicalKind == "list")
                {
                    inst = BuildList(e, parent, ctx);
                }
                else
                {
                    ctx.log($"canonical {e.canonical.kind} '{e.canonical.Ref}' → placeholder");
                    inst = BuildPlaceholderButton(e, parent, ctx);
                }
                // Radios under the same parent share one ToggleGroup → mutually exclusive.
                if (canonicalKind == "radio")
                {
                    var grp = parent.GetComponent<ToggleGroup>() ?? parent.gameObject.AddComponent<ToggleGroup>();
                    grp.allowSwitchOff = true;
                    var tg = inst.GetComponentInChildren<Toggle>(true);
                    if (tg != null) tg.group = grp;
                }
                ApplyTransform(inst.GetComponent<RectTransform>() ?? inst.AddComponent<RectTransform>(), e, ctx);

                // Fill the prefab's binding slots (label/value/options); else stamp label.
                var bindings = inst.GetComponentInChildren<FigForgeBindings>(true);
                if (bindings != null) bindings.Apply(e.canonical.label, e.canonical.value, e.canonical.options);
                else StampLabel(inst, e.canonical.label);

                // Per-instance label font override — only when we know the component
                // font (defLabelFont) AND this instance genuinely differs from it.
                // Without defLabelFont (older export) we can't tell a real override
                // from the component default, so we leave the prefab's font alone.
                if (e.canonical.labelFont != null && e.canonical.defLabelFont != null
                    && !SameFont(e.canonical.labelFont, e.canonical.defLabelFont))
                {
                    var labelTmp = (bindings != null ? bindings.label : null) ?? inst.GetComponentInChildren<TMP_Text>(true);
                    ApplyFont(labelTmp, e.canonical.labelFont, ctx);
                }
                if (e.canonical.labelFontSize.HasValue && e.canonical.defLabelFontSize.HasValue
                    && Mathf.Abs(e.canonical.labelFontSize.Value - e.canonical.defLabelFontSize.Value) > 0.001f)
                {
                    var labelTmp = (bindings != null ? bindings.label : null) ?? inst.GetComponentInChildren<TMP_Text>(true);
                    ApplyFontSize(labelTmp, e.canonical.labelFontSize.Value, ctx);
                }

                // Per-instance shape override — an instance whose stroke/fill/corner
                // was tweaked in Figma re-skins just this button (shared prefab intact).
                if (e.canonical.instanceShape != null)
                    ApplyInstanceShape(inst, e.canonical.instanceShape, e.canonical.stateColors, ctx);

                if (e.canonical.instanceRootShape != null)
                    ApplyInstanceRootShape(inst, e.canonical.instanceRootShape, ctx);

                if (e.canonical.instanceStateShapes != null)
                    ApplyInstanceStateShapes(inst, e.canonical.instanceStateShapes, ctx);

                // Per-instance hover/press colour override — an instance whose rollover
                // or pressed colour differs from the component re-skins just this button.
                if (e.canonical.instanceStateColors != null)
                    ApplyInstanceStateColors(inst, e.canonical.instanceStateColors, e.canonical.instanceShape ?? e.canonical.shape, ctx);

                AttachNav(inst, e, ctx);
                if (!string.IsNullOrEmpty(e.canonical.instanceName))
                    ctx.registered.Add(new KeyValuePair<string, GameObject>(e.canonical.instanceName, inst));
                if (!string.IsNullOrEmpty(e.id)) ctx.byElementId[e.id] = inst;
                return inst;
            }

            var go = NewRect(string.IsNullOrEmpty(e.name) ? e.type : e.name, parent);
            if (!string.IsNullOrEmpty(e.id)) ctx.byElementId[e.id] = go;
            var rt = go.GetComponent<RectTransform>();
            ApplyTransform(rt, e, ctx);

            bool hasAsset = !string.IsNullOrEmpty(e.asset) && ctx.sprites.ContainsKey(e.asset);

            if (e.text != null && !hasAsset)
                ApplyText(go, e, ctx);
            else
                ApplyVisual(go, e, ctx, hasAsset);

            if (e.interactive && go.GetComponent<Selectable>() == null)
            {
                if (go.GetComponent<Graphic>() == null) AddTransparentRaycastTarget(go);
                go.AddComponent<Button>();
            }

            if (e.clipsContent) ApplyClip(go);
            if (e.autoLayout != null) ApplyAutoLayout(go, e.autoLayout, ctx.scaleFactor);
            AttachNav(go, e, ctx);

            // children
            if (e.children != null)
                foreach (var childId in e.children)
                    if (index.TryGetValue(childId, out var child))
                        BuildElement(child, index, go.transform, ctx);

            return go;
        }

        // -----------------------------------------------------------------------
        static void ApplyTransform(RectTransform rt, ElementData e, BuildContext ctx)
        {
            float sf = ctx.scaleFactor;
            var t = e.transform;
            rt.localScale = Vector3.one;

            // Fall back to the raw rect when no mapped transform is present
            // (partial export, or an element kind the mapper skipped): centre-
            // anchored, sized from the Figma rect. Avoids a hard NRE on build.
            if (t == null)
            {
                ctx.log($"element '{e.name}' has no transform — positioned from rect");
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
                rt.sizeDelta = e.rect != null ? new Vector2(e.rect.w * sf, e.rect.h * sf) : new Vector2(100f, 100f);
                rt.anchoredPosition = Vector2.zero;
                return;
            }

            rt.anchorMin = V(t.anchorMin, 0.5f);
            rt.anchorMax = V(t.anchorMax, 0.5f);
            rt.pivot = V(t.pivot, 0.5f);
            rt.offsetMin = V(t.offsetMin, 0f) * sf;
            rt.offsetMax = V(t.offsetMax, 0f) * sf;
            if (Mathf.Abs(t.rotationZ) > 0.001f)
                rt.localEulerAngles = new Vector3(0, 0, t.rotationZ);
        }

        static void ApplyVisual(GameObject go, ElementData e, BuildContext ctx, bool hasAsset)
        {
            var style = e.style;
            bool hasVector = HasVector(e.vector);
            bool needGraphic = hasAsset || hasVector || (style != null && (style.fill != null || style.stroke != null
                || (style.fills != null && style.fills.Count > 0)
                || (style.strokes != null && style.strokes.Count > 0)
                || (style.effects != null && style.effects.Count > 0)));
            if (!needGraphic) return;

            // Crisp procedural vector mesh wins over PNG/SDF for representable glyphs.
            // Strokes are baked into the mesh; opacity + blend mode are applied live by
            // the graphic (no separate stroke pass, no ApplyOpacity double-dim).
            if (hasVector && TryBuildVectorGraphic(go, e.vector, ctx, !ctx.disableRaycasts,
                    BlendModeFromManifest(style != null ? style.blendMode : null),
                    style != null ? style.opacity : 1f))
            {
                return;
            }

            // Procedural SDF for rounded/bordered solid panels (crisp at any size,
            // per-corner). Gradients stay on the baked path; images are textures;
            // flat un-bordered solids stay a cheap plain Image.
            if (!hasAsset && UseSdf(style)) { BuildSdfPanel(go, e, ctx); return; }

            var img = go.AddComponent<Image>();
            img.raycastTarget = !ctx.disableRaycasts;

            if (hasAsset)
            {
                img.sprite = ctx.sprites[e.asset];
                // Auto-9-slice when the sprite was imported with a border (rounded /
                // bordered panel) so it scales without smearing the corners.
                if (img.sprite != null && img.sprite.border.sqrMagnitude > 0.01f)
                { img.type = Image.Type.Sliced; img.pixelsPerUnitMultiplier = 1f; }
                ApplyOpacity(go, e, Color.white, opacityBaked: true);
            }
            else if (style?.fill != null)
            {
                ApplyFill(img, style, ctx);
            }
            else
            {
                img.color = new Color(1, 1, 1, 0); // stroke-only container
            }

            if (style?.stroke != null) AddStroke(go, e, ctx);
        }

        // SDF panel covers solid (or fill-less) rounded/bordered rects AND linear
        // gradients with any number of stops (rendered crisp + rounded by the shader).
        // Image fills and radial/angular gradients stay on the baked path.
        static bool UseSdf(StyleData s)
        {
            if (s == null) return false;
            if (s.fill != null && s.fill.kind == "image") return false;
            if (s.fills != null)
            {
                for (int i = 0; i < s.fills.Count; i++)
                    if (s.fills[i] != null && s.fills[i].kind == "image") return false;
            }
            if (s.fill != null && s.fill.kind == "gradient") return IsSdfGradient(s.fill);
            bool rounded = s.cornerRadius > 0.01f || AnyCorner(s.corners);
            bool border = s.stroke != null || (s.strokes != null && s.strokes.Count > 0);
            bool hasShadow = s.effects != null && s.effects.Count > 0;
            bool layeredPaint = (s.fills != null && s.fills.Count > 1) || (s.strokes != null && s.strokes.Count > 1);
            return rounded || border || hasShadow || layeredPaint;
        }

        // Apply a captured Figma drop shadow to the SDF graphic. Figma offset is +y
        // DOWN; flip for Unity's +y-up. color.a==0 / null → no-op.
        static void ApplyShadow(FigForgeRoundedRect rr, ShadowData s, float sf)
        {
            if (EffectKind(s) != "dropShadow") return;
            if (s == null || s.color == null) return;
            var c = ToColor(s.color);
            if (c.a <= 0.001f) return;
            rr.SetShadow(c, new Vector2(s.offsetX * sf, s.offsetY * sf), s.blur * sf, s.spread * sf);
        }

        // A gradient the SDF shader can render: linear/radial/angular/diamond with 2+ stops.
        static bool IsSdfGradient(Fill f)
            => f != null && f.kind == "gradient"
               && (f.gradient == "linear" || f.gradient == "radial" || f.gradient == "angular" || f.gradient == "diamond")
               && f.stops != null && f.stops.Count >= 2;

        static FigForgeGradientKind GradientKind(Fill f)
        {
            switch (f != null ? f.gradient : null)
            {
                case "radial": return FigForgeGradientKind.Radial;
                case "angular": return FigForgeGradientKind.Angular;
                case "diamond": return FigForgeGradientKind.Diamond;
                default: return FigForgeGradientKind.Linear;
            }
        }

        // Linear-gradient direction in the SDF shader's UV space (origin centre,
        // +y UP), from Figma's gradientTransform first row [a,b,...]. Figma UV is
        // +y DOWN, so flip y. Magnitude is dropped (the shader spans the shape);
        // default top→bottom when no transform is present.
        static Vector2 GradientDir(float[] transform)
        {
            if (transform != null && transform.Length >= 2)
            {
                var d = new Vector2(transform[0], -transform[1]);
                if (d.sqrMagnitude > 1e-6f) return d.normalized;
            }
            return new Vector2(0f, -1f);
        }

        static Gradient ToUnityGradient(List<GradientStop> stops)
        {
            var g = new Gradient();
            if (stops == null || stops.Count == 0)
            {
                g.SetKeys(
                    new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                    new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
                return g;
            }

            var sorted = new List<GradientStop>(stops);
            sorted.Sort((a, b) => a.position.CompareTo(b.position));
            var colors = new GradientColorKey[sorted.Count];
            var alphas = new GradientAlphaKey[sorted.Count];
            for (int i = 0; i < sorted.Count; i++)
            {
                var c = ToColor(sorted[i].color);
                float t = Mathf.Clamp01(sorted[i].position);
                colors[i] = new GradientColorKey(new Color(c.r, c.g, c.b, 1f), t);
                alphas[i] = new GradientAlphaKey(c.a, t);
            }
            g.SetKeys(colors, alphas);
            return g;
        }

        static FigForgeFill FillFromManifest(Fill fill)
        {
            if (fill != null && IsSdfGradient(fill))
            {
                var c = ToColor(fill.stops[0].color);
                return FigForgeFill.GradientFill(ToUnityGradient(fill.stops), GradientKind(fill), GradientDir(fill.transform), c);
            }
            if (fill != null && fill.kind == "solid") return FigForgeFill.Solid(ToColor(fill.color));
            return FigForgeFill.None;
        }

        static FigForgeFill LegacyGradientFill(float[] fill, float[] fill2, float[] transform)
        {
            var c0 = ToColor(fill);
            if (fill2 == null) return FigForgeFill.Solid(c0);
            var c1 = ToColor(fill2);
            var g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(new Color(c0.r, c0.g, c0.b, 1f), 0f), new GradientColorKey(new Color(c1.r, c1.g, c1.b, 1f), 1f) },
                new[] { new GradientAlphaKey(c0.a, 0f), new GradientAlphaKey(c1.a, 1f) });
            return FigForgeFill.LinearGradient(g, GradientDir(transform), c0);
        }

        static bool AnyCorner(float[] c)
        {
            if (c == null) return false;
            for (int i = 0; i < c.Length; i++) if (c[i] > 0.01f) return true;
            return false;
        }

        static FigForgeStrokeAlign StrokeAlign(string align)
        {
            switch (align)
            {
                case "outside": return FigForgeStrokeAlign.Outside;
                case "center": return FigForgeStrokeAlign.Center;
                default: return FigForgeStrokeAlign.Inside;
            }
        }

        static FigForgeBlendMode BlendModeFromManifest(string mode)
        {
            switch (mode)
            {
                case "passThrough": return FigForgeBlendMode.PassThrough;
                case "darken": return FigForgeBlendMode.Darken;
                case "multiply": return FigForgeBlendMode.Multiply;
                case "plusDarker": return FigForgeBlendMode.PlusDarker;
                case "colorBurn": return FigForgeBlendMode.ColorBurn;
                case "lighten": return FigForgeBlendMode.Lighten;
                case "screen": return FigForgeBlendMode.Screen;
                case "plusLighter": return FigForgeBlendMode.PlusLighter;
                case "colorDodge": return FigForgeBlendMode.ColorDodge;
                case "overlay": return FigForgeBlendMode.Overlay;
                case "softLight": return FigForgeBlendMode.SoftLight;
                case "hardLight": return FigForgeBlendMode.HardLight;
                case "difference": return FigForgeBlendMode.Difference;
                case "exclusion": return FigForgeBlendMode.Exclusion;
                case "hue": return FigForgeBlendMode.Hue;
                case "saturation": return FigForgeBlendMode.Saturation;
                case "color": return FigForgeBlendMode.Color;
                case "luminosity": return FigForgeBlendMode.Luminosity;
                default: return FigForgeBlendMode.Normal;
            }
        }

        // Strokes render ~1px thinner than their Figma weight because the SDF edge
        // is anti-aliased on BOTH sides (a thin border has little solid core). Add a
        // small px bias so outlines read at (or a hair above) the design weight.
        // Returns 0 for no stroke so we never fabricate a border.
        const float OutlineThickenPx = 1f;
        static float StrokePx(float weight, float sf)
            => weight > 0.001f ? Mathf.Max(1f, (weight + OutlineThickenPx) * sf) : 0f;

        // Per-corner radii (tl,tr,br,bl) in scaled px, from style.corners or the uniform radius.
        static Vector4 CornerRadii(StyleData s, float sf)
        {
            if (s.corners != null && s.corners.Length >= 4)
                return new Vector4(s.corners[0], s.corners[1], s.corners[2], s.corners[3]) * sf;
            float r = s.cornerRadius * sf;
            return new Vector4(r, r, r, r);
        }

        // The SDF shader is found via Shader.Find, which works in the editor but
        // not in a player build unless the shader is "always included". Register it
        // once per domain so built players render the procedural panels/buttons.
        static bool _sdfShaderRegistered;
        static void EnsureSdfShaderIncluded()
        {
            if (_sdfShaderRegistered) return;
            _sdfShaderRegistered = true;
            try
            {
                var so = new UnityEditor.SerializedObject(UnityEngine.Rendering.GraphicsSettings.GetGraphicsSettings());
                var arr = so.FindProperty("m_AlwaysIncludedShaders");
                if (arr == null) return;
                AddAlwaysIncludedShader(arr, Shader.Find("FigForge/RoundedRect"));
                AddAlwaysIncludedShader(arr, Shader.Find("FigForge/LayeredRect4"));
                AddAlwaysIncludedShader(arr, Shader.Find("FigForge/CachedQuad"));
                AddAlwaysIncludedShader(arr, Shader.Find("FigForge/CachedBlend"));
                AddAlwaysIncludedShader(arr, Shader.Find("FigForge/LayerBlur"));
                AddAlwaysIncludedShader(arr, Shader.Find("FigForge/Composite"));
                AddAlwaysIncludedShader(arr, Shader.Find("FigForge/VectorBake"));
                so.ApplyModifiedProperties();
            }
            catch { /* best effort — editor still works via Shader.Find */ }
        }

        static void AddAlwaysIncludedShader(UnityEditor.SerializedProperty arr, Shader shader)
        {
            if (shader == null || arr == null) return;
            for (int i = 0; i < arr.arraySize; i++)
                if (arr.GetArrayElementAtIndex(i).objectReferenceValue == shader) return;
            int idx = arr.arraySize;
            arr.InsertArrayElementAtIndex(idx);
            arr.GetArrayElementAtIndex(idx).objectReferenceValue = shader;
        }

        static void BuildSdfPanel(GameObject go, ElementData e, BuildContext ctx)
        {
            EnsureSdfShaderIncluded();
            var s = e.style;
            var sh = ShapeFromStyle(s);
            BuildShapeVisualLayers(go, sh, ctx);
            ApplyOpacity(go, e, Color.white);
        }

        static CanonicalShape ShapeFromStyle(StyleData s)
        {
            if (s == null) return null;
            return new CanonicalShape
            {
                cornerRadius = s.cornerRadius,
                opacity = s.opacity,
                blendMode = s.blendMode,
                fill = s.fill != null && s.fill.kind == "solid" ? s.fill.color : null,
                gradient = s.fill != null && s.fill.kind == "gradient" ? s.fill : null,
                fills = s.fills,
                stroke = s.stroke,
                strokes = s.strokes,
                effects = s.effects,
            };
        }

        // Clip child content. A rounded element (already backed by an SDF
        // FigForgeRoundedRect) clips to its rounded corners via a stencil Mask —
        // RectMask2D can only clip to a rectangle. Flat rects keep the cheaper
        // RectMask2D.
        static void ApplyClip(GameObject go)
        {
            if (go.GetComponent<FigForgeRoundedRect>() != null || go.GetComponent<FigForgeLayeredRect>() != null)
            {
                var mask = go.AddComponent<Mask>();
                mask.showMaskGraphic = true; // still draw the panel's own fill/gradient/border
            }
            else
            {
                go.AddComponent<RectMask2D>();
            }
        }

        static void ApplyFill(Image img, StyleData style, BuildContext ctx)
        {
            var fill = style.fill;
            int radius = style.cornerRadius > 0
                ? Mathf.RoundToInt(style.cornerRadius * ctx.scaleFactor)
                : 0;

            if (fill.kind == "gradient")
            {
                var grad = GradientSpriteCache.Get(fill);
                if (grad != null) { img.sprite = grad; img.color = Color.white; }
            }
            else if (fill.kind == "solid")
            {
                var c = ToColor(fill.color);
                if (radius > 0)
                {
                    img.sprite = RoundedRectSpriteCache.Get(radius);
                    img.type = Image.Type.Sliced;
                    img.pixelsPerUnitMultiplier = 1f;
                }
                img.color = c;
            }
        }

        static void AddStroke(GameObject parent, ElementData e, BuildContext ctx)
        {
            var stroke = e.style.stroke;
            int thickness = Mathf.Max(1, Mathf.RoundToInt(stroke.weight * ctx.scaleFactor));
            int radius = e.style.cornerRadius > 0 ? Mathf.RoundToInt(e.style.cornerRadius * ctx.scaleFactor) : 0;

            var strokeGo = NewRect("Stroke", parent.transform);
            Stretch(strokeGo.GetComponent<RectTransform>());
            var img = strokeGo.AddComponent<Image>();
            img.raycastTarget = false;
            img.sprite = RoundedOutlineSpriteCache.Get(radius, thickness);
            img.type = Image.Type.Sliced;
            img.pixelsPerUnitMultiplier = 1f;
            img.color = ToColor(stroke.color);
            if (stroke.dashed) ctx.log($"stroke on '{e.name}' is dashed — use SDF/layered rect for procedural dashed rendering");
        }

        static void ApplyText(GameObject go, ElementData e, BuildContext ctx)
        {
            var t = e.text;
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = t.content ?? "";
            tmp.fontSize = Mathf.Max(1f, t.fontSize * ctx.scaleFactor);
            tmp.color = ToColor(t.color);
            tmp.raycastTarget = !ctx.disableRaycasts;

            var font = ctx.resolveFont?.Invoke(t.fontFamily, t.fontStyle);
            if (font != null) tmp.font = font;
            tmp.fontStyle = FauxStyle(t.fontStyle, font); // synthesize bold/italic if the real weight isn't available

            tmp.alignment = MapAlign(t.alignH, t.alignV);
            if (t.letterSpacing.HasValue) tmp.characterSpacing = t.letterSpacing.Value;
            // Figma "auto width" text (WIDTH_AND_HEIGHT) hugs its content and never
            // wraps — so don't wrap, else a hair of width difference pushes a word
            // to the next line. Fixed-width/auto-height text still wraps.
            tmp.enableWordWrapping = !string.Equals(t.autoResize, "WIDTH_AND_HEIGHT", System.StringComparison.OrdinalIgnoreCase);
            ApplyText_Outline(tmp, t);
            MatchTextWeight(tmp);
            ApplyOpacity(go, e, tmp.color);
        }

        // A Figma text stroke → a TMP outline. Uses the per-instance fontMaterial
        // (so the shared font asset isn't outlined globally). TMP's outline width is
        // normalised (0..~0.5 usable) against the SDF spread, so map the px weight
        // relative to the font size (scale-independent) and clamp.
        static void ApplyText_Outline(TMP_Text tmp, TextData t)
        {
            if (t.outline == null || t.outline.weight <= 0.001f || t.outline.color == null) return;
            var mat = tmp.fontMaterial; // instance — does not touch the shared asset material
            mat.SetColor(TMPro.ShaderUtilities.ID_OutlineColor, ToColor(t.outline.color));
            // ~2.2 reads close to the Figma stroke (TMP outlineWidth is normalised; 1.4 was thin).
            float w = Mathf.Clamp(t.outline.weight / Mathf.Max(1f, t.fontSize) * 2.2f, 0f, 0.5f);
            mat.SetFloat(TMPro.ShaderUtilities.ID_OutlineWidth, w);
            tmp.UpdateMeshPadding();
        }

        // Faux bold/italic only when the resolved face doesn't already encode that
        // weight/slant — so a real Bold asset isn't double-bolded.
        static FontStyles FauxStyle(string style, TMP_FontAsset font)
        {
            string s = (style ?? "").ToLowerInvariant();
            string fn = (font != null ? font.name : "").ToLowerInvariant();
            FontStyles fs = FontStyles.Normal;
            if (s.Contains("bold") && !fn.Contains("bold")) fs |= FontStyles.Bold;
            if ((s.Contains("italic") || s.Contains("oblique")) && !fn.Contains("italic") && !fn.Contains("oblique")) fs |= FontStyles.Italic;
            return fs;
        }

        static TextAlignmentOptions MapAlign(string h, string v)
        {
            bool top = v == "top", bottom = v == "bottom";
            if (h == "center") return top ? TextAlignmentOptions.Top : bottom ? TextAlignmentOptions.Bottom : TextAlignmentOptions.Center;
            if (h == "right") return top ? TextAlignmentOptions.TopRight : bottom ? TextAlignmentOptions.BottomRight : TextAlignmentOptions.Right;
            return top ? TextAlignmentOptions.TopLeft : bottom ? TextAlignmentOptions.BottomLeft : TextAlignmentOptions.Left;
        }

        static void ApplyAutoLayout(GameObject go, AutoLayout al, float sf)
        {
            HorizontalOrVerticalLayoutGroup g = al.mode == "vertical"
                ? go.AddComponent<VerticalLayoutGroup>()
                : (HorizontalOrVerticalLayoutGroup)go.AddComponent<HorizontalLayoutGroup>();
            g.spacing = al.spacing * sf;
            g.padding = new RectOffset(
                Mathf.RoundToInt(al.paddingLeft * sf), Mathf.RoundToInt(al.paddingRight * sf),
                Mathf.RoundToInt(al.paddingTop * sf), Mathf.RoundToInt(al.paddingBottom * sf));
            g.childControlWidth = false; g.childControlHeight = false;
            g.childForceExpandWidth = false; g.childForceExpandHeight = false;
        }

        // opacityBaked: the graphic is a Figma-exported PNG whose alpha already
        // encodes the node's layer opacity (exportAsync bakes it in). Re-multiplying
        // it onto the leaf Graphic would double-dim, so skip that path. Container
        // opacity (CanvasGroup over child elements) still applies, since children are
        // separate GameObjects the baked PNG doesn't cover.
        static void ApplyOpacity(GameObject go, ElementData e, Color baseColor, bool opacityBaked = false)
        {
            float o = e.style != null ? e.style.opacity : 1f;
            var layered = go.GetComponent<FigForgeLayeredRect>();
            if (layered != null)
            {
                layered.ConfigureAppearance(o, BlendModeFromManifest(e.style != null ? e.style.blendMode : null));
                return;
            }
            if (o >= 0.999f) return;
            if (e.children != null && e.children.Count > 0)
            {
                var cg = go.GetComponent<CanvasGroup>() ?? go.AddComponent<CanvasGroup>();
                cg.alpha = o;
            }
            else if (!opacityBaked)
            {
                var g = go.GetComponent<Graphic>();
                if (g != null) { var c = g.color; c.a *= o; g.color = c; }
            }
        }

        // -----------------------------------------------------------------------
        // A real interactive Button built from the Figma component's state sprites.
        // The visible states are sibling children; HitArea owns raycasts and Label
        // stays shared.
        static GameObject BuildStateButton(ElementData e, Transform parent, BuildContext ctx)
        {
            var go = NewRect(string.IsNullOrEmpty(e.name) ? "Button" : e.name, parent);
            var st = e.canonical.states;

            var normal = SpriteByFile(st.normal, ctx);
            var hi = SpriteByFile(st.highlighted, ctx) ?? normal;
            var pr = SpriteByFile(st.pressed, ctx) ?? hi ?? normal;

            var regularGo = AddSpriteStateChild(go.transform, "Regular", normal, true);
            var rollGo = AddSpriteStateChild(go.transform, "RollOver", hi, false);
            var pressGo = AddSpriteStateChild(go.transform, "Pressed", pr, false);
            var hit = AddHitArea(go.transform, e.canonical.parts);

            var btn = go.AddComponent<FigForgeButton>();
            btn.targetGraphic = hit;
            btn.transition = Selectable.Transition.None;

            var stateObjects = go.AddComponent<FigForgeButtonStateObjects>();
            stateObjects.regular = regularGo;
            stateObjects.rollOver = rollGo;
            stateObjects.pressed = pressGo;

            var labelGo = NewRect("Label", go.transform);
            AnchorPart(labelGo.GetComponent<RectTransform>(), e.canonical.parts, "Label");
            var tmp = labelGo.AddComponent<TextMeshProUGUI>();
            tmp.text = e.canonical.label ?? e.name;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            ApplyFontSize(tmp, e.canonical.defLabelFontSize ?? 16f, ctx);
            tmp.raycastTarget = false;
            ConfigureButtonLabelText(tmp);
            // The prefab/definition mirrors the canonical COMPONENT's label font.
            ApplyFont(tmp, e.canonical != null ? e.canonical.defLabelFont : null, ctx);
            MatchTextWeight(tmp);
            btn.tmpTxt_label = tmp;
            return go;
        }

        // Procedural rounded-rect button (SDF shader) — resolution-independent,
        // matches the Figma vector exactly. Used when the export captured a solid
        // background shape; otherwise we fall back to the state-PNG button.
        static GameObject BuildShapeButton(ElementData e, Transform parent, BuildContext ctx)
        {
            var sh = e.canonical.shape;
            EnsureSdfShaderIncluded();
            var go = NewRect(string.IsNullOrEmpty(e.name) ? "Button" : e.name, parent);

            var sc = e.canonical.stateColors;
            var regularShape = e.canonical.stateShapes != null && e.canonical.stateShapes.normal != null
                ? e.canonical.stateShapes.normal : sh;
            var rollShape = ShapeForState(regularShape, e.canonical.stateShapes != null ? e.canonical.stateShapes.highlighted : null, sc != null ? sc.highlighted : null);
            var pressShape = ShapeForState(regularShape, e.canonical.stateShapes != null ? e.canonical.stateShapes.pressed : null, sc != null ? sc.pressed : null);
            var rootShape = RootShadowShape(e.canonical.rootShape, regularShape);
            StripRootShadowsFromState(regularShape, rootShape);
            StripRootShadowsFromState(rollShape, rootShape);
            StripRootShadowsFromState(pressShape, rootShape);

            if (rootShape != null)
                BuildButtonRootVisualLayers(go, rootShape, ctx);
            var regularGo = AddShapeStateChild(go.transform, "Regular", regularShape, e.canonical.parts, ctx, true);
            var rollGo = AddShapeStateChild(go.transform, "RollOver", rollShape, e.canonical.parts, ctx, false);
            var pressGo = AddShapeStateChild(go.transform, "Pressed", pressShape, e.canonical.parts, ctx, false);
            var hit = AddHitArea(go.transform, e.canonical.parts);

            var btn = go.AddComponent<FigForgeButton>();
            btn.targetGraphic = hit;
            btn.transition = Selectable.Transition.None;

            var stateObjects = go.AddComponent<FigForgeButtonStateObjects>();
            stateObjects.regular = regularGo;
            stateObjects.rollOver = rollGo;
            stateObjects.pressed = pressGo;

            var labelGo = NewRect("Label", go.transform);
            AnchorPart(labelGo.GetComponent<RectTransform>(), e.canonical.parts, "Label");
            var tmp = labelGo.AddComponent<TextMeshProUGUI>();
            tmp.text = e.canonical.label ?? e.name;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            ApplyFontSize(tmp, e.canonical.defLabelFontSize ?? 16f, ctx);
            tmp.raycastTarget = false;
            ConfigureButtonLabelText(tmp);
            ApplyFont(tmp, e.canonical.defLabelFont, ctx);
            MatchTextWeight(tmp);
            btn.tmpTxt_label = tmp;
            return go;
        }

        static GameObject AddSpriteStateChild(Transform parent, string name, Sprite sprite, bool active)
        {
            var child = NewRect(name, parent);
            Stretch(child.GetComponent<RectTransform>());
            var img = child.AddComponent<Image>();
            img.raycastTarget = false;
            if (sprite != null) img.sprite = sprite;
            else img.color = new Color(0.45f, 0.36f, 1f, 1f);
            if (sprite != null && sprite.border.sqrMagnitude > 0.01f)
            { img.type = Image.Type.Sliced; img.pixelsPerUnitMultiplier = 1f; }
            child.SetActive(active);
            return child;
        }

        static GameObject AddShapeStateChild(Transform parent, string name, CanonicalShape shape, Dictionary<string, float[]> parts, BuildContext ctx, bool active)
        {
            return AddShapeLayerContainer(parent, name, shape, parts, ctx, active);
        }

        static GameObject AddShapeLayerContainer(Transform parent, string name, CanonicalShape shape, Dictionary<string, float[]> parts, BuildContext ctx, bool active)
        {
            var child = NewRect(name, parent);
            AnchorPart(child.GetComponent<RectTransform>(), parts, name);
            BuildShapeVisualLayers(child, shape, ctx);
            child.SetActive(active);
            return child;
        }

        static void BuildShapeVisualLayers(GameObject owner, CanonicalShape shape, BuildContext ctx, bool allowOwnerRenderer = true)
        {
            if (shape == null) return;
            // Crisp procedural vector mesh wins over the PNG fallback when present.
            if (allowOwnerRenderer && TryBuildShapeVector(owner, shape, ctx, false))
                return;
            if (allowOwnerRenderer && TryBuildShapeAsset(owner, shape, ctx, false))
                return;

            var fills = ShapeFills(shape);
            var strokes = ShapeStrokes(shape);
            var shadows = ShapeShadows(shape);

            if (allowOwnerRenderer && TryBuildLayeredRect(owner, shape, fills, strokes, shadows, ctx))
                return;

            if (allowOwnerRenderer)
                RemoveOwnerVisualRenderers(owner);

            if (fills.Count == 0 && strokes.Count == 0 && shadows.Count == 0)
            {
                AddShapePaintLayer(owner.transform, "Fill", FigForgeFill.None, FigForgeStroke.None, shape, ctx);
                return;
            }

            // Figma paints fills as a stack, then strokes on top. Drop shadows are
            // effects of the whole rendered object, so render them behind paint.
            for (int i = 0; i < shadows.Count; i++)
            {
                var rr = AddShapePaintLayer(owner.transform, LayerName("Effect", i, shadows.Count), FigForgeFill.None, FigForgeStroke.None, shape, ctx);
                ApplyShadow(rr, shadows[i], ctx.scaleFactor);
            }

            for (int i = 0; i < fills.Count; i++)
                AddShapePaintLayer(owner.transform, LayerName("Fill", i, fills.Count), fills[i], FigForgeStroke.None, shape, ctx);

            for (int i = 0; i < strokes.Count; i++)
            {
                bool usesGradient;
                var strokeFill = StrokeFillFromManifest(strokes[i], out usesGradient);
                AddShapePaintLayer(owner.transform, LayerName("Stroke", i, strokes.Count), strokeFill, StrokeFromManifest(strokes[i], ctx), shape, ctx, usesGradient);
            }
        }

        // ---- Procedural vector mesh (FigForgeVectorGraphic) -----------------------
        static bool HasVector(VectorDrawing v)
            => v != null && v.meshes != null && v.meshes.Count > 0;

        // Bake all of a drawing's fill/stroke meshes into one flat vertex/index/colour
        // buffer and attach a FigForgeVectorGraphic. Per-vertex colour = region colour
        // × AA alpha; opacity + Figma blend mode are applied live by the graphic.
        static bool TryBuildVectorGraphic(GameObject owner, VectorDrawing v, BuildContext ctx, bool raycast,
                                          FigForgeBlendMode blend, float opacity)
        {
            if (!HasVector(v)) return false;
            var verts = new List<float>();
            var tris = new List<int>();
            var colors = new List<Color32>();
            foreach (var m in v.meshes)
            {
                if (m == null || m.verts == null || m.tris == null) continue;
                int baseV = verts.Count / 2;
                int nv = m.verts.Count / 2;
                Color baseCol = (m.color != null && m.color.Length >= 4)
                    ? new Color(m.color[0], m.color[1], m.color[2], m.color[3])
                    : Color.white;
                for (int i = 0; i < nv; i++)
                {
                    verts.Add(m.verts[i * 2]);
                    verts.Add(m.verts[i * 2 + 1]);
                    float a = (m.alpha != null && i < m.alpha.Count) ? m.alpha[i] : 1f;
                    var c = baseCol;
                    c.a *= a;
                    colors.Add(c);
                }
                for (int i = 0; i + 2 < m.tris.Count; i += 3)
                {
                    tris.Add(m.tris[i] + baseV);
                    tris.Add(m.tris[i + 1] + baseV);
                    tris.Add(m.tris[i + 2] + baseV);
                }
            }
            if (verts.Count < 6 || tris.Count < 3) return false;

            RemoveOwnerVisualRenderers(owner);
            if (owner.GetComponent<CanvasRenderer>() == null) owner.AddComponent<CanvasRenderer>();
            var g = owner.AddComponent<FigForgeVectorGraphic>();
            var bounds = (v.bounds != null && v.bounds.Length >= 2)
                ? new Vector2(v.bounds[0], v.bounds[1]) : Vector2.one;
            g.Configure(bounds, verts.ToArray(), tris.ToArray(), colors.ToArray(),
                raycast && !ctx.disableRaycasts, blend, opacity);
            return true;
        }

        // Vector mesh for a canonical shape. Opacity + blend mode are applied live by
        // the graphic (mesh renders at full opacity; opacity folds in at draw/present).
        static bool TryBuildShapeVector(GameObject owner, CanonicalShape shape, BuildContext ctx, bool raycast)
        {
            if (shape == null || !HasVector(shape.vector)) return false;
            return TryBuildVectorGraphic(owner, shape.vector, ctx, raycast,
                BlendModeFromManifest(shape.blendMode), shape.opacity);
        }

        static bool TryBuildLayeredRect(GameObject owner, CanonicalShape shape, List<FigForgeFill> fills, List<Stroke> strokes, List<ShadowData> shadows, BuildContext ctx)
        {
            if (shape != null && HasVector(shape.vector)) return false;
            if (HasShapeAsset(shape, ctx)) return false;
            if (!CanUseLayeredRect(fills, strokes, shadows)) return false;

            RemoveOwnerVisualRenderers(owner);
            if (owner.GetComponent<CanvasRenderer>() == null) owner.AddComponent<CanvasRenderer>();
            var rr = owner.AddComponent<FigForgeLayeredRect>();
            rr.raycastTarget = false;
            ApplyShapeValues(shape, ctx.scaleFactor, out _, out _, out var corners);
            rr.ConfigureLayers(fills, LayeredStrokes(strokes, ctx), LayeredEffects(shadows, ctx), corners);
            rr.ConfigureAppearance(shape != null ? shape.opacity : 1f, BlendModeFromManifest(shape != null ? shape.blendMode : null));
            return true;
        }

        static bool HasShapeAsset(CanonicalShape shape, BuildContext ctx)
            => shape != null
               && !string.IsNullOrEmpty(shape.asset)
               && ctx.sprites != null
               && ctx.sprites.ContainsKey(shape.asset);

        static bool TryBuildShapeAsset(GameObject owner, CanonicalShape shape, BuildContext ctx, bool raycastTarget)
        {
            if (!HasShapeAsset(shape, ctx))
            {
                // A shape that references a vector PNG we never received degrades to
                // a procedural rounded-rect box. Surface it so a dropped/blank asset
                // is debuggable instead of silently wrong.
                if (shape != null && !string.IsNullOrEmpty(shape.asset))
                    ctx.log($"shape sprite asset '{shape.asset}' missing for {owner.name} — using procedural fallback");
                return false;
            }
            RemoveOwnerVisualRenderers(owner);
            if (owner.GetComponent<CanvasRenderer>() == null) owner.AddComponent<CanvasRenderer>();
            var img = owner.AddComponent<Image>();
            img.sprite = ctx.sprites[shape.asset];
            // Figma exportAsync bakes the node's appearance into this PNG, including
            // layer opacity/effects. Keep the Unity tint neutral so opacity is not
            // applied a second time on vector/icon fallbacks.
            img.color = Color.white;
            img.raycastTarget = raycastTarget && !ctx.disableRaycasts;
            return true;
        }

        static bool CanUseLayeredRect(List<FigForgeFill> fills, List<Stroke> strokes, List<ShadowData> shadows)
        {
            if (fills.Count > 4 || strokes.Count > 4) return false;
            int drop = 0, inner = 0, blur = 0;
            for (int i = 0; i < shadows.Count; i++)
            {
                switch (EffectKind(shadows[i]))
                {
                    case "innerShadow": inner++; break;
                    case "layerBlur": blur++; break;
                    default: drop++; break;
                }
            }
            if (drop > 4 || inner > 4 || blur > 1) return false;
            return true;
        }

        static List<FigForgeStrokeLayer> LayeredStrokes(List<Stroke> strokes, BuildContext ctx)
        {
            var outStrokes = new List<FigForgeStrokeLayer>();
            for (int i = 0; i < strokes.Count; i++)
                outStrokes.Add(StrokeLayerFromManifest(strokes[i], ctx));
            return outStrokes;
        }

        static FigForgeStrokeLayer StrokeLayerFromManifest(Stroke stroke, BuildContext ctx)
        {
            if (stroke == null) return default;
            DashPattern(stroke, ctx.scaleFactor, out var dash, out var gap);
            return FigForgeStrokeLayer.Create(
                StrokePaintFromManifest(stroke),
                StrokePx(stroke.weight, ctx.scaleFactor),
                StrokeAlign(stroke.align),
                stroke.dashed,
                dash,
                gap);
        }

        static FigForgeFill StrokePaintFromManifest(Stroke stroke)
        {
            if (stroke == null) return FigForgeFill.None;
            if (stroke.fill != null && stroke.fill.kind == "gradient" && IsSdfGradient(stroke.fill))
                return FillFromManifest(stroke.fill);
            if (stroke.fill != null && stroke.fill.kind == "solid")
                return FillFromManifest(stroke.fill);
            return FigForgeFill.Solid(ToColor(stroke.color));
        }

        static List<FigForgeEffectLayer> LayeredEffects(List<ShadowData> shadows, BuildContext ctx)
        {
            var outEffects = new List<FigForgeEffectLayer>();
            for (int i = 0; i < shadows.Count; i++)
            {
                var s = shadows[i];
                if (s == null) continue;
                string kind = EffectKind(s);
                if (kind == "layerBlur")
                {
                    float start = (s.startBlur ?? s.blur) * ctx.scaleFactor;
                    float end = (s.endBlur ?? s.blur) * ctx.scaleFactor;
                    var effect = FigForgeEffectLayer.LayerBlur(start);
                    effect.blurMode = string.Equals(s.blurMode, "progressive", StringComparison.OrdinalIgnoreCase)
                        ? FigForgeLayerBlurMode.Progressive
                        : FigForgeLayerBlurMode.Uniform;
                    effect.endBlur = end;
                    effect.enabled = Mathf.Max(effect.blur, effect.endBlur) > 0.001f;
                    outEffects.Add(effect);
                    continue;
                }

                if (s.color == null) continue;
                var c = ToColor(s.color);
                if (c.a <= 0.001f) continue;
                var offset = new Vector2(s.offsetX * ctx.scaleFactor, s.offsetY * ctx.scaleFactor);
                float blur = s.blur * ctx.scaleFactor;
                float spread = s.spread * ctx.scaleFactor;
                outEffects.Add(kind == "innerShadow"
                    ? FigForgeEffectLayer.InnerShadow(c, offset, blur, spread)
                    : FigForgeEffectLayer.DropShadow(c, offset, blur, spread));
            }
            return outEffects;
        }

        static string EffectKind(ShadowData effect)
        {
            if (effect == null) return "dropShadow";
            if (!string.IsNullOrEmpty(effect.kind)) return effect.kind;
            return effect.inner ? "innerShadow" : "dropShadow";
        }

        static void RemoveOwnerVisualRenderers(GameObject owner)
        {
            var layered = owner.GetComponents<FigForgeLayeredRect>();
            for (int i = layered.Length - 1; i >= 0; i--)
                if (layered[i] != null) UnityEngine.Object.DestroyImmediate(layered[i]);

            var rounded = owner.GetComponents<FigForgeRoundedRect>();
            for (int i = rounded.Length - 1; i >= 0; i--)
                if (rounded[i] != null) UnityEngine.Object.DestroyImmediate(rounded[i]);
        }

        static string LayerName(string prefix, int index, int count)
        {
            return count == 1 ? prefix : $"{prefix} {index + 1}";
        }

        static FigForgeRoundedRect AddShapePaintLayer(Transform parent, string name, FigForgeFill fill, FigForgeStroke stroke, CanonicalShape shape, BuildContext ctx, bool strokeUsesFillGradient = false)
        {
            var go = NewRect(name, parent);
            Stretch(go.GetComponent<RectTransform>());
            if (go.GetComponent<CanvasRenderer>() == null) go.AddComponent<CanvasRenderer>();
            var rr = go.AddComponent<FigForgeRoundedRect>();
            rr.raycastTarget = false;
            ApplyShapeValues(shape, ctx.scaleFactor, out _, out _, out var corners);
            rr.Configure(fill, stroke, corners, strokeUsesFillGradient);
            return rr;
        }

        static Image AddHitArea(Transform parent, Dictionary<string, float[]> parts)
        {
            var hitGo = NewRect("HitArea", parent);
            AnchorPart(hitGo.GetComponent<RectTransform>(), parts, "HitArea");
            var hit = hitGo.AddComponent<Image>();
            hit.color = new Color(0, 0, 0, 0);
            hit.raycastTarget = true;
            return hit;
        }

        static CanonicalShape ShapeForState(CanonicalShape normal, CanonicalShape stateShape, float[] stateColor)
        {
            if (stateShape != null) return stateShape;
            var sh = CloneShape(normal);
            if (stateColor != null)
            {
                sh.fill = stateColor;
                sh.fills = new List<Fill> { new Fill { kind = "solid", color = stateColor } };
                sh.gradient = null;
                sh.fill2 = null;
                sh.gradientTransform = null;
            }
            return sh;
        }

        static CanonicalShape RootShadowShape(CanonicalShape root, CanonicalShape regular)
        {
            if (root == null || regular == null) return root;
            var rootShadows = ShapeShadows(root);
            if (rootShadows.Count == 0) return root;
            if (HasVisualPaint(root) || root.cornerRadius > 0.001f) return root;

            return new CanonicalShape
            {
                cornerRadius = regular.cornerRadius,
                shadow = rootShadows[0],
                shadows = rootShadows,
            };
        }

        static bool HasVisualPaint(CanonicalShape sh)
        {
            if (sh == null) return false;
            if (HasVisibleColor(sh.fill)) return true;
            if (sh.gradient != null || sh.fill2 != null) return true;
            if (sh.fills != null)
                for (int i = 0; i < sh.fills.Count; i++)
                    if (HasVisibleFill(sh.fills[i])) return true;
            if (HasVisibleStroke(sh.stroke)) return true;
            if (sh.strokes != null)
                for (int i = 0; i < sh.strokes.Count; i++)
                    if (HasVisibleStroke(sh.strokes[i])) return true;
            return HasVisibleColor(sh.borderColor) && sh.borderWidth > 0.001f;
        }

        static bool HasVisibleFill(Fill f)
        {
            if (f == null) return false;
            if (f.kind == "image") return true;
            if (f.kind == "gradient")
            {
                if (f.stops == null || f.stops.Count == 0) return true;
                for (int i = 0; i < f.stops.Count; i++)
                    if (f.stops[i] != null && HasVisibleColor(f.stops[i].color)) return true;
                return false;
            }
            return HasVisibleColor(f.color);
        }

        static bool HasVisibleStroke(Stroke s)
        {
            if (s == null || s.weight <= 0.001f) return false;
            return HasVisibleFill(s.fill) || HasVisibleColor(s.color);
        }

        static bool HasVisibleColor(float[] rgba)
            => rgba != null && rgba.Length >= 4 && rgba[3] > 0.001f;

        static CanonicalShape CloneShape(CanonicalShape sh)
        {
            if (sh == null) return null;
            return new CanonicalShape
            {
                asset = sh.asset,
                vector = sh.vector,
                cornerRadius = sh.cornerRadius,
                opacity = sh.opacity,
                blendMode = sh.blendMode,
                fill = sh.fill,
                gradient = sh.gradient,
                fill2 = sh.fill2,
                gradientTransform = sh.gradientTransform,
                fills = sh.fills,
                stroke = sh.stroke,
                strokes = sh.strokes,
                borderColor = sh.borderColor,
                borderWidth = sh.borderWidth,
                borderAlign = sh.borderAlign,
                shadow = sh.shadow,
                shadows = sh.shadows,
                effects = sh.effects,
            };
        }

        static List<FigForgeFill> ShapeFills(CanonicalShape sh)
        {
            var outFills = new List<FigForgeFill>();
            if (sh == null) return outFills;
            if (sh.fills != null)
            {
                for (int i = 0; i < sh.fills.Count; i++)
                    if (HasVisibleFill(sh.fills[i]))
                        outFills.Add(FillFromManifest(sh.fills[i]));
            }
            if (outFills.Count == 0 && (HasVisibleColor(sh.fill) || sh.gradient != null || HasVisibleColor(sh.fill2)))
                outFills.Add(sh.gradient != null && IsSdfGradient(sh.gradient)
                    ? FillFromManifest(sh.gradient)
                    : LegacyGradientFill(sh.fill, sh.fill2, sh.gradientTransform));
            return outFills;
        }

        static List<Stroke> ShapeStrokes(CanonicalShape sh)
        {
            var strokes = new List<Stroke>();
            if (sh == null) return strokes;
            if (sh.strokes != null && sh.strokes.Count > 0) strokes.AddRange(sh.strokes);
            else if (sh.stroke != null) strokes.Add(sh.stroke);
            else if (sh.borderColor != null && sh.borderWidth > 0.001f)
            {
                strokes.Add(new Stroke
                {
                    color = sh.borderColor,
                    weight = sh.borderWidth,
                    align = sh.borderAlign,
                    dashed = false,
                    dashPattern = null,
                });
            }
            return strokes;
        }

        static List<ShadowData> ShapeShadows(CanonicalShape sh)
        {
            var shadows = new List<ShadowData>();
            if (sh == null) return shadows;
            if (sh.effects != null && sh.effects.Count > 0) shadows.AddRange(sh.effects);
            else if (sh.shadows != null && sh.shadows.Count > 0) shadows.AddRange(sh.shadows);
            else if (sh.shadow != null) shadows.Add(sh.shadow);
            return shadows;
        }

        static ShadowData FirstDropShadow(CanonicalShape sh)
        {
            var effects = ShapeShadows(sh);
            for (int i = 0; i < effects.Count; i++)
                if (EffectKind(effects[i]) == "dropShadow") return effects[i];
            return null;
        }

        static void StripRootShadowsFromState(CanonicalShape state, CanonicalShape root)
        {
            if (state == null || root == null) return;
            var rootShadows = ShapeShadows(root);
            if (rootShadows.Count == 0) return;
            var stateShadows = ShapeShadows(state);
            if (stateShadows.Count == 0) return;
            bool allRootCopies = stateShadows.Count == rootShadows.Count;
            if (allRootCopies)
            {
                for (int i = 0; i < stateShadows.Count; i++)
                {
                    if (!SameShadow(stateShadows[i], rootShadows[i])) { allRootCopies = false; break; }
                }
            }
            if (!allRootCopies) return;
            state.shadow = null;
            state.shadows = null;
            state.effects = null;
        }

        static bool SameShadow(ShadowData a, ShadowData b)
        {
            if (a == null || b == null) return false;
            return SigF(a.color) == SigF(b.color)
                && Mathf.Abs(a.offsetX - b.offsetX) < 0.001f
                && Mathf.Abs(a.offsetY - b.offsetY) < 0.001f
                && Mathf.Abs(a.blur - b.blur) < 0.001f
                && Mathf.Abs(a.spread - b.spread) < 0.001f
                && a.inner == b.inner;
        }

        static FigForgeStroke StrokeFromManifest(Stroke stroke, BuildContext ctx)
        {
            if (stroke == null) return FigForgeStroke.None;
            return StrokeFromManifest(stroke, ctx.scaleFactor);
        }

        static FigForgeStroke StrokeFromManifest(Stroke stroke, float scaleFactor)
        {
            if (stroke == null) return FigForgeStroke.None;
            DashPattern(stroke, scaleFactor, out var dash, out var gap);
            return FigForgeStroke.Create(ToColor(stroke.color), StrokePx(stroke.weight, scaleFactor), StrokeAlign(stroke.align), stroke.dashed, dash, gap);
        }

        static void DashPattern(Stroke stroke, float scaleFactor, out float dash, out float gap)
        {
            dash = 0f;
            gap = 0f;
            if (stroke == null || !stroke.dashed) return;
            if (stroke.dashPattern == null || stroke.dashPattern.Count == 0) return;
            dash = Mathf.Max(0f, stroke.dashPattern[0] * scaleFactor);
            gap = Mathf.Max(0f, (stroke.dashPattern.Count > 1 ? stroke.dashPattern[1] : stroke.dashPattern[0]) * scaleFactor);
        }

        static FigForgeFill StrokeFillFromManifest(Stroke stroke, out bool usesGradient)
        {
            usesGradient = false;
            if (stroke != null && stroke.fill != null && stroke.fill.kind == "gradient" && IsSdfGradient(stroke.fill))
            {
                usesGradient = true;
                return FillFromManifest(stroke.fill);
            }
            return FigForgeFill.None;
        }

        static bool HasVisibleFill(FigForgeFill fill)
        {
            if (fill.disabled) return false;
            if (fill.kind == FigForgeFillKind.Solid) return fill.color.a > 0.001f;
            if (fill.gradient == null) return fill.color.a > 0.001f;
            var alphas = fill.gradient.alphaKeys;
            if (alphas == null || alphas.Length == 0) return fill.color.a > 0.001f;
            for (int i = 0; i < alphas.Length; i++)
                if (alphas[i].alpha > 0.001f) return true;
            return false;
        }

        // Push a CanonicalShape onto a FigForgeRoundedRect (fill/gradient/stroke/
        // corners/align). Outputs the resolved base fill so callers can keep
        // FigForgeButtonStateColors' normal state in sync.
        static void ApplyShapeToRR(FigForgeRoundedRect rr, CanonicalShape sh, float sf, out FigForgeFill fill)
        {
            ApplyShapeValues(sh, sf, out fill, out var stroke, out var corners);
            rr.Configure(fill, stroke, corners);
            var shadows = ShapeShadows(sh);
            if (shadows.Count > 0) ApplyShadow(rr, shadows[0], sf); // null/transparent → no-op
        }

        static void ApplyShapeValues(CanonicalShape sh, float sf, out FigForgeFill fill,
                                     out FigForgeStroke stroke, out Vector4 corners)
        {
            var fills = ShapeFills(sh);
            fill = fills.Count > 0 ? fills[0] : FigForgeFill.None;
            var strokes = ShapeStrokes(sh);
            stroke = strokes.Count > 0
                ? StrokeFromManifest(strokes[0], sf)
                : FigForgeStroke.None;
            float br = sh.cornerRadius * sf;
            corners = new Vector4(br, br, br, br);
        }

        static FigForgeShapeStyle ShapeStyle(CanonicalShape sh, BuildContext ctx)
        {
            ApplyShapeValues(sh, ctx.scaleFactor, out var fill, out var stroke, out var corners);
            var style = new FigForgeShapeStyle
            {
                fill = fill,
                stroke = stroke,
                corners = corners,
                shadowColor = new Color(0, 0, 0, 0),
                shadowOffset = Vector2.zero,
                shadowBlur = 0f,
                shadowSpread = 0f,
            };
            var firstShadow = FirstDropShadow(sh);
            if (firstShadow != null && firstShadow.color != null)
            {
                style.shadowColor = ToColor(firstShadow.color);
                style.shadowOffset = new Vector2(firstShadow.offsetX * ctx.scaleFactor, firstShadow.offsetY * ctx.scaleFactor);
                style.shadowBlur = firstShadow.blur * ctx.scaleFactor;
                style.shadowSpread = firstShadow.spread * ctx.scaleFactor;
            }
            return style;
        }

        // Apply a per-instance shape override onto an instantiated canonical button:
        // re-skin its FigForgeRoundedRect (so an instance-level stroke/fill/corner
        // tweak in Figma shows) and re-sync the base (normal) fill of its state-colour
        // swapper. No-op for non-SDF (state-PNG) buttons. Creates prefab overrides on
        // just this instance — the shared prefab is untouched.
        static void ApplyInstanceShape(GameObject inst, CanonicalShape sh, CanonicalStateColors sc, BuildContext ctx)
        {
            if (inst.GetComponentInChildren<FigForgeButtonStateObjects>(true) != null)
            {
                ApplyStateShape(inst, "Regular", sh, ctx);
                if (sc != null)
                {
                    if (sc.highlighted != null) ApplyStateShape(inst, "RollOver", ShapeForState(sh, null, sc.highlighted), ctx);
                    if (sc.pressed != null) ApplyStateShape(inst, "Pressed", ShapeForState(sh, null, sc.pressed), ctx);
                }
                return;
            }
            var rr = inst.GetComponentInChildren<FigForgeRoundedRect>(true);
            if (rr == null) return;
            ApplyShapeToRR(rr, sh, ctx.scaleFactor, out var fill);
            var states = rr.GetComponent<FigForgeButtonStateColors>();
            if (states != null)
            {
                // Re-derive ALL three states exactly like BuildShapeButton: the new base
                // fill drives 'normal', while an explicit Figma rollover/pressed colour
                // wins for hover/press. Only fall back to the base fill when the component
                // has no rollover/pressed — so a gradient instance no longer clobbers a
                // real hover/press colour with its regular gradient.
                SetStates(states, fill, sc);
            }
        }

        static void ApplyInstanceStateShapes(GameObject inst, CanonicalStateShapes shapes, BuildContext ctx)
        {
            if (shapes == null) return;
            ApplyStateShape(inst, "Regular", shapes.normal, ctx);
            ApplyStateShape(inst, "RollOver", shapes.highlighted, ctx);
            ApplyStateShape(inst, "Pressed", shapes.pressed, ctx);
            var stateObjects = inst.GetComponentInChildren<FigForgeButtonStateObjects>(true);
            if (stateObjects != null) stateObjects.Refresh();
        }

        static void ApplyStateShape(GameObject inst, string name, CanonicalShape shape, BuildContext ctx)
        {
            if (shape == null) return;
            var t = inst.transform.Find(name);
            if (t == null) return;
            ClearChildren(t);
            BuildShapeVisualLayers(t.gameObject, shape, ctx);
        }

        static void ApplyInstanceRootShape(GameObject inst, CanonicalShape shape, BuildContext ctx)
        {
            BuildButtonRootVisualLayers(inst, shape, ctx);
        }

        static void ClearChildren(Transform t)
        {
            for (int i = t.childCount - 1; i >= 0; i--)
                UnityEngine.Object.DestroyImmediate(t.GetChild(i).gameObject);
        }

        static void BuildButtonRootVisualLayers(GameObject button, CanonicalShape shape, BuildContext ctx)
        {
            RemoveOwnerVisualRenderers(button);
            ClearButtonRootVisualLayers(button.transform);
            if (shape == null) return;
            var fillGo = NewRect("Fill", button.transform);
            Stretch(fillGo.GetComponent<RectTransform>());
            BuildShapeVisualLayers(fillGo, shape, ctx);
            MoveButtonRootVisualLayersToFront(button.transform);
        }

        static void ClearButtonRootVisualLayers(Transform button)
        {
            for (int i = button.childCount - 1; i >= 0; i--)
            {
                var child = button.GetChild(i);
                if (IsRootVisualLayerName(child.name))
                    UnityEngine.Object.DestroyImmediate(child.gameObject);
            }
        }

        static void MoveButtonRootVisualLayersToFront(Transform button)
        {
            var layers = new List<Transform>();
            for (int i = 0; i < button.childCount; i++)
            {
                var child = button.GetChild(i);
                if (IsRootVisualLayerName(child.name)) layers.Add(child);
            }
            for (int i = 0; i < layers.Count; i++)
                layers[i].SetSiblingIndex(i);
        }

        static bool IsRootVisualLayerName(string name)
        {
            return name == "Effect" || name.StartsWith("Effect ")
                || name == "Fill" || name.StartsWith("Fill ")
                || name == "Stroke" || name.StartsWith("Stroke ");
        }

        // Build the three state fills from a base fill + optional captured hover/press
        // colours: 'normal' is the base; an explicit Figma rollover/pressed colour wins
        // for hover/press (as a SOLID), otherwise the state keeps the base fill. Shared
        // by BuildShapeButton (prefab) and ApplyInstanceShape (per-instance).
        static void SetStates(FigForgeButtonStateColors states, FigForgeFill baseFill, CanonicalStateColors sc)
        {
            states.normal = baseFill;
            states.highlighted = (sc != null && sc.highlighted != null) ? FigForgeFill.Solid(ToColor(sc.highlighted)) : baseFill;
            states.pressed     = (sc != null && sc.pressed != null)     ? FigForgeFill.Solid(ToColor(sc.pressed))     : baseFill;
        }

        // Apply a per-instance hover/press colour override onto an instantiated
        // canonical button. Legacy prefabs still use FigForgeButtonStateColors;
        // new child-state prefabs update the RollOver/Pressed rounded-rect child.
        static void ApplyInstanceStateColors(GameObject inst, CanonicalStateColors sc, CanonicalShape baseShape, BuildContext ctx)
        {
            var states = inst.GetComponentInChildren<FigForgeButtonStateColors>(true);
            if (states != null)
            {
                if (sc.highlighted != null) states.highlighted = FigForgeFill.Solid(ToColor(sc.highlighted));
                if (sc.pressed != null) states.pressed = FigForgeFill.Solid(ToColor(sc.pressed));
                return;
            }

            if (baseShape == null) return;
            if (sc.highlighted != null) ApplyStateShape(inst, "RollOver", ShapeForState(baseShape, null, sc.highlighted), ctx);
            if (sc.pressed != null) ApplyStateShape(inst, "Pressed", ShapeForState(baseShape, null, sc.pressed), ctx);
            var stateObjects = inst.GetComponentInChildren<FigForgeButtonStateObjects>(true);
            if (stateObjects != null) stateObjects.Refresh();
        }

        // ===================== Canonical controls =====================

        // Anchor a child RectTransform to a captured normalized rect (full-bleed if absent).
        static void AnchorPart(RectTransform rt, Dictionary<string, float[]> parts, string name)
        {
            if (parts != null && parts.TryGetValue(name, out var a) && a != null && a.Length >= 4)
            { rt.anchorMin = new Vector2(a[0], a[1]); rt.anchorMax = new Vector2(a[2], a[3]); }
            else { rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; }
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        }

        // A control's targetGraphic must receive raycasts to be clickable, but SDF /
        // asset / vector backgrounds default raycastTarget=false (they're decorative).
        // Re-enable it on the control background so toggle/radio/dropdown/input click.
        // This is UNCONDITIONAL — unlike decorative graphics, an interactive control's
        // click surface must stay raycastable even when the project opts to strip
        // raycasts from non-interactive graphics (disableRaycasts). Mirrors the way
        // button hit areas force raycastTarget=true (AddHitArea).
        static void MakeClickTarget(Graphic g, BuildContext ctx)
        {
            if (g != null) g.raycastTarget = true;
        }

        // Background Graphic from a CanonicalShape (crisp SDF) or a transparent Image.
        // Add the CanvasRenderer up front: Toggle.isOn cross-fades graphic.canvasRenderer
        // the instant we set it, before RequireComponent would otherwise add one.
        static Graphic AddShapeGraphic(GameObject go, CanonicalShape sh, BuildContext ctx)
        {
            if (go.GetComponent<CanvasRenderer>() == null) go.AddComponent<CanvasRenderer>();
            if (sh == null) { var img = go.AddComponent<Image>(); img.color = new Color(1, 1, 1, 0); return img; }
            if (TryBuildShapeVector(go, sh, ctx, false))
                return go.GetComponent<FigForgeVectorGraphic>();
            if (TryBuildShapeAsset(go, sh, ctx, false))
                return go.GetComponent<Image>();
            EnsureSdfShaderIncluded();
            var fills = ShapeFills(sh);
            var strokes = ShapeStrokes(sh);
            var shadows = ShapeShadows(sh);
            if (TryBuildLayeredRect(go, sh, fills, strokes, shadows, ctx))
                return go.GetComponent<FigForgeLayeredRect>();
            var rr = go.AddComponent<FigForgeRoundedRect>();
            ApplyShapeToRR(rr, sh, ctx.scaleFactor, out _);
            return rr;
        }

        static bool HasPartTree(CanonicalRef c, string name)
            => c != null && c.partTrees != null && c.partTrees.TryGetValue(name, out var t)
               && t != null && t.elements != null && t.elements.Count > 0 && !string.IsNullOrEmpty(t.root);

        // Render a canonical control PART as its FULL Figma subtree (render-only) by
        // reusing the main element renderer: build a local id→element index and run
        // BuildElement on the subtree root under `container`. Forces raycasts off so the
        // whole subtree passes clicks through to the control. The subtree root carries a
        // full-bleed stretch transform (from the exporter) so it fills the anchored
        // container exactly. Returns a representative Graphic for wiring (or null).
        static Graphic BuildPartSubtree(GameObject container, ElementSubtree tree, BuildContext ctx)
        {
            if (tree == null || tree.elements == null || tree.elements.Count == 0) return null;
            var localIndex = new Dictionary<string, ElementData>();
            foreach (var el in tree.elements)
                if (el != null && el.id != null) localIndex[el.id] = el;
            if (string.IsNullOrEmpty(tree.root) || !localIndex.TryGetValue(tree.root, out var rootEl)) return null;

            bool prevDisable = ctx.disableRaycasts;
            ctx.disableRaycasts = true; // a part subtree is render-only; clicks reach the control
            GameObject built;
            try { built = BuildElement(rootEl, localIndex, container.transform, ctx); }
            finally { ctx.disableRaycasts = prevDisable; }
            return built != null ? built.GetComponentInChildren<Graphic>(true) : null;
        }

        // A control Background that doubles as the click target (input/dropdown): render
        // its visuals (full subtree when present, else the flat shape) and return a
        // raycastable graphic spanning the whole background. With a subtree, the visuals
        // are render-only and a transparent full-bleed Image on the container catches
        // clicks — so the entire background is clickable regardless of subtree shape.
        static Graphic BuildClickableBackground(GameObject bgGo, CanonicalRef c, BuildContext ctx)
        {
            if (HasPartTree(c, "Background"))
            {
                BuildPartSubtree(bgGo, c.partTrees["Background"], ctx);
                var img = bgGo.AddComponent<Image>();
                img.color = new Color(0, 0, 0, 0);
                return img;
            }
            return AddShapeGraphic(bgGo, c.shape, ctx);
        }

        static void MatchTextWeight(TMP_Text tmp)
        {
            if (tmp == null) return;
            tmp.UpdateMeshPadding();
        }

        // A non-interactive TMP label child, anchored to a captured part (or full-bleed).
        static TextMeshProUGUI AddControlLabel(GameObject parent, string name, string text,
            Dictionary<string, float[]> parts, string part, BuildContext ctx, TextAlignmentOptions align)
        {
            var go = NewRect(name, parent.transform);
            AnchorPart(go.GetComponent<RectTransform>(), parts, part);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = text ?? ""; tmp.alignment = align; tmp.color = new Color(0.1f, 0.1f, 0.12f);
            tmp.fontSize = 14f * ctx.scaleFactor; tmp.raycastTarget = false;
            tmp.textWrappingMode = TextWrappingModes.NoWrap; // single line (no wrap to a 2nd row)
            tmp.overflowMode = TextOverflowModes.Overflow;   // show the FULL label — never clip to "Tog…"
            ApplyFont(tmp, null, ctx);
            MatchTextWeight(tmp);
            return tmp;
        }

        // Toggle / Radio: Background (targetGraphic) + Checkmark (graphic, shown when on)
        // + optional Label. Radios get grouped into a per-parent ToggleGroup in BuildElement.
        static GameObject BuildToggle(ElementData e, Transform parent, BuildContext ctx)
        {
            var c = e.canonical;
            var go = NewRect(string.IsNullOrEmpty(e.name) ? "Toggle" : e.name, parent);
            var toggle = go.AddComponent<FigForgeToggle>();
            toggle.transition = Selectable.Transition.None;

            // Background — full render-only Figma subtree when captured, else the flat
            // shape. Decorative either way (the HitArea below is the click surface).
            var bgGo = NewRect("Background", go.transform);
            AnchorPart(bgGo.GetComponent<RectTransform>(), c.parts, "Background");
            Graphic bg = HasPartTree(c, "Background")
                ? BuildPartSubtree(bgGo, c.partTrees["Background"], ctx)
                : AddShapeGraphic(bgGo, c.shape, ctx);

            // Checkmark — the "on" indicator. With a subtree it's a full composite shown/
            // hidden by FigForgeToggleGraphicObject (Toggle.graphic can only cross-fade a
            // single Graphic). Without one, fall back to the flat checkShape as Toggle.graphic.
            if (HasPartTree(c, "Checkmark"))
            {
                var ckGo = NewRect("Checkmark", go.transform);
                AnchorPart(ckGo.GetComponent<RectTransform>(), c.parts, "Checkmark");
                BuildPartSubtree(ckGo, c.partTrees["Checkmark"], ctx);
                toggle.graphic = null;
                ckGo.SetActive(false); // initial off; the reveal syncs the real isOn
                var reveal = go.AddComponent<FigForgeToggleGraphicObject>();
                reveal.toggle = toggle; reveal.graphicRoot = ckGo;
            }
            else if (c.checkShape != null)
            {
                var ckGo = NewRect("Checkmark", go.transform);
                AnchorPart(ckGo.GetComponent<RectTransform>(), c.parts, "Checkmark");
                toggle.graphic = AddShapeGraphic(ckGo, c.checkShape, ctx);
            }

            TextMeshProUGUI label = null;
            if ((c.parts != null && c.parts.ContainsKey("Label")) || !string.IsNullOrEmpty(c.label))
            {
                label = AddControlLabel(go, "Label", c.label, c.parts, "Label", ctx, TextAlignmentOptions.Left);
                // Keep the captured LEFT edge, but span the control's full HEIGHT and extend
                // to its right edge. The captured Label box hugs the measured text height
                // (smaller than both the control and the line height), so centering inside it
                // leaves the text sitting high. Full-height box + vertical-middle (Left)
                // alignment centers the label against the box/checkmark, no truncation.
                var lrt = label.GetComponent<RectTransform>();
                lrt.anchorMin = new Vector2(lrt.anchorMin.x, 0f);
                lrt.anchorMax = new Vector2(1f, 1f);
                lrt.offsetMin = new Vector2(0f, 0f);
                lrt.offsetMax = new Vector2(-6f * ctx.scaleFactor, 0f);
            }

            // Whole-component click surface: a transparent HitArea spanning the control
            // (or the captured "HitArea" layer if the Figma component defines one) so
            // clicking the box OR the label toggles — the Figma component frame is the
            // hit area, matching standard checkbox/radio UX and button hit areas. Added
            // last so it sits on top of the visuals; the Background stays decorative.
            var hit = AddHitArea(go.transform, c.parts);
            toggle.targetGraphic = hit;

            // Leave isOn at the default (off): the per-instance value is applied by
            // FigForgeBindings.Apply AFTER the ToggleGroup is wired, so a radio prefab
            // doesn't bake one instance's "on" and clobber its group-mates.
            toggle.isOn = false;

            toggle.tmpTxt_label = label;
            toggle.checkmark = toggle.graphic; // null for a composite checkmark (driven by FigForgeToggleGraphicObject)

            var bind = go.AddComponent<FigForgeBindings>();
            bind.control = toggle; bind.label = label; bind.background = bg;
            return go;
        }

        // InputField: Background + text viewport + placeholder + value text.
        static GameObject BuildInputField(ElementData e, Transform parent, BuildContext ctx)
        {
            var c = e.canonical;
            float sf = ctx.scaleFactor;
            var go = NewRect(string.IsNullOrEmpty(e.name) ? "InputField" : e.name, parent);
            var input = go.AddComponent<FigForgeInputField>();
            input.transition = Selectable.Transition.None;
            input.lineType = TMP_InputField.LineType.SingleLine;
            input.contentType = TMP_InputField.ContentType.Standard;
            input.customCaretColor = true;
            input.caretColor = new Color(0.1f, 0.1f, 0.12f, 1f);
            input.selectionColor = new Color(0.49f, 0.36f, 1f, 0.28f);

            var bgGo = NewRect("Background", go.transform);
            AnchorPart(bgGo.GetComponent<RectTransform>(), c.parts, "Background");
            var bg = BuildClickableBackground(bgGo, c, ctx);
            input.targetGraphic = bg;
            MakeClickTarget(bg, ctx); // the background IS the click target here

            var area = NewRect("Text Area", go.transform);
            var art = area.GetComponent<RectTransform>();
            if (c.parts != null && c.parts.ContainsKey("Text")) AnchorPart(art, c.parts, "Text");
            else if (c.parts != null && c.parts.ContainsKey("Value")) AnchorPart(art, c.parts, "Value");
            else if (c.parts != null && c.parts.ContainsKey("Placeholder")) AnchorPart(art, c.parts, "Placeholder");
            else
            {
                art.anchorMin = Vector2.zero;
                art.anchorMax = Vector2.one;
                art.offsetMin = new Vector2(12f * sf, 2f * sf);
                art.offsetMax = new Vector2(-12f * sf, -2f * sf);
            }
            area.AddComponent<RectMask2D>();
            input.textViewport = art;

            var placeholderGo = NewRect("Placeholder", area.transform);
            Stretch(placeholderGo.GetComponent<RectTransform>());
            var placeholder = placeholderGo.AddComponent<TextMeshProUGUI>();
            placeholder.text = c.placeholder ?? c.label ?? "";
            placeholder.alignment = TextAlignmentOptions.Left; // vertical-middle (Midline sat high)
            placeholder.color = new Color(0.55f, 0.56f, 0.62f, 1f);
            placeholder.fontSize = 14f * sf;
            placeholder.raycastTarget = false;
            placeholder.textWrappingMode = TextWrappingModes.NoWrap;
            placeholder.overflowMode = TextOverflowModes.Overflow;
            ApplyFont(placeholder, c.defLabelFont, ctx);
            MatchTextWeight(placeholder);

            var textGo = NewRect("Text", area.transform);
            Stretch(textGo.GetComponent<RectTransform>());
            var text = textGo.AddComponent<TextMeshProUGUI>();
            text.text = "";
            text.alignment = TextAlignmentOptions.Left; // vertical-middle (Midline sat high)
            text.color = new Color(0.1f, 0.1f, 0.12f, 1f);
            text.fontSize = 14f * sf;
            text.raycastTarget = false;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.overflowMode = TextOverflowModes.Overflow;
            ApplyFont(text, c.defLabelFont, ctx);
            MatchTextWeight(text);

            input.placeholder = placeholder;
            input.textComponent = text;
            input.SetTextWithoutNotify(c.value ?? "");

            var bind = go.AddComponent<FigForgeBindings>();
            bind.control = input; bind.label = placeholder; bind.valueText = text; bind.background = bg;
            return go;
        }

        // Dropdown: Background + caption + arrow + a standard (hidden) TMP_Dropdown template.
        static GameObject BuildDropdown(ElementData e, Transform parent, BuildContext ctx)
        {
            var c = e.canonical;
            var go = NewRect(string.IsNullOrEmpty(e.name) ? "Dropdown" : e.name, parent);
            var dd = go.AddComponent<FigForgeDropdown>();
            dd.transition = Selectable.Transition.None;

            var bgGo = NewRect("Background", go.transform);
            AnchorPart(bgGo.GetComponent<RectTransform>(), c.parts, "Background");
            var bg = BuildClickableBackground(bgGo, c, ctx);
            dd.targetGraphic = bg;
            MakeClickTarget(bg, ctx);
            ApplyDropdownBackgroundStates(bg, c, ctx);

            var caption = AddControlLabel(go, "Label", c.value, c.parts, "Label", ctx, TextAlignmentOptions.Left);
            dd.captionText = caption;
            // Span the control's full height so the caption is vertically centered; the
            // captured Label box hugs the text height (sits high). Keep the captured
            // horizontal box so the caption doesn't overlap the Arrow on the right.
            var crt = caption.GetComponent<RectTransform>();
            crt.anchorMin = new Vector2(crt.anchorMin.x, 0f);
            crt.anchorMax = new Vector2(crt.anchorMax.x, 1f);
            crt.offsetMin = new Vector2(crt.offsetMin.x, 0f);
            crt.offsetMax = new Vector2(crt.offsetMax.x, 0f);
            if (HasPartTree(c, "Arrow"))
            {
                // Full render-only arrow subtree (the Regular state, with its nested
                // vectors/shapes). Hover/press recolouring is best-effort and skipped
                // for a composite arrow (the flat arrowColor path handles the simple case).
                var arrowGo = NewRect("Arrow", go.transform);
                AnchorPart(arrowGo.GetComponent<RectTransform>(), c.parts, "Arrow");
                BuildPartSubtree(arrowGo, c.partTrees["Arrow"], ctx);
            }
            else if (c.parts != null && c.parts.ContainsKey("Arrow"))
            {
                if (!string.IsNullOrEmpty(c.arrowAsset))
                {
                    var arrow = AddControlImage(go, "Arrow", c.parts, "Arrow", c.arrowAsset, ctx);
                    ApplyDropdownArrowSpriteStates(go, arrow, c, ctx);
                }
                else
                {
                    var arrow = AddControlLabel(go, "Arrow", "v", c.parts, "Arrow", ctx, TextAlignmentOptions.Center);
                    ApplyDropdownArrowStates(go, arrow, c);
                }
            }

            BuildDropdownTemplate(go, c, ctx, dd);
            if (c.options != null && c.options.Count > 0) { dd.ClearOptions(); dd.AddOptions(c.options); }
            if (c.optionShape != null)
            {
                var edgeStyler = go.AddComponent<FigForgeDropdownOptionEdges>();
                edgeStyler.dropdown = dd;
            }

            var bind = go.AddComponent<FigForgeBindings>();
            bind.control = dd; bind.optionsTarget = dd; bind.valueText = caption; bind.background = bg;
            return go;
        }

        static void ApplyDropdownArrowStates(GameObject control, TMP_Text arrow, CanonicalRef c)
        {
            if (arrow == null) return;
            var normal = c.arrowColor != null ? ToColor(c.arrowColor) : arrow.color;
            arrow.color = normal;
            if (c.arrowRollover == null && c.arrowPressed == null) return;

            var states = control.AddComponent<FigForgeGraphicStateColors>();
            states.target = arrow;
            states.normal = normal;
            states.highlighted = c.arrowRollover != null ? ToColor(c.arrowRollover) : normal;
            states.pressed = c.arrowPressed != null ? ToColor(c.arrowPressed) : states.highlighted;
        }

        static Image AddControlImage(GameObject parent, string name, Dictionary<string, float[]> parts, string part, string asset, BuildContext ctx)
        {
            var go = NewRect(name, parent.transform);
            AnchorPart(go.GetComponent<RectTransform>(), parts, part);
            var img = go.AddComponent<Image>();
            if (ctx.sprites != null && ctx.sprites.TryGetValue(asset, out var sprite)) img.sprite = sprite;
            else ctx.log($"sprite asset '{asset}' missing for {name}");
            img.color = Color.white;
            img.raycastTarget = false;
            return img;
        }

        static void ApplyDropdownArrowSpriteStates(GameObject control, Image arrow, CanonicalRef c, BuildContext ctx)
        {
            if (arrow == null) return;
            Sprite rollover = !string.IsNullOrEmpty(c.arrowRolloverAsset) && ctx.sprites.ContainsKey(c.arrowRolloverAsset)
                ? ctx.sprites[c.arrowRolloverAsset]
                : null;
            Sprite pressed = !string.IsNullOrEmpty(c.arrowPressedAsset) && ctx.sprites.ContainsKey(c.arrowPressedAsset)
                ? ctx.sprites[c.arrowPressedAsset]
                : null;
            if (rollover == null && pressed == null) return;

            var states = control.AddComponent<FigForgeGraphicStateSprites>();
            states.target = arrow;
            states.normal = arrow.sprite;
            states.highlighted = rollover != null ? rollover : arrow.sprite;
            states.pressed = pressed != null ? pressed : states.highlighted;
        }

        static void ApplyDropdownBackgroundStates(Graphic bg, CanonicalRef c, BuildContext ctx)
        {
            if (bg == null || (c.bgRollover == null && c.bgPressed == null)) return;
            if (c.shape != null && (bg is FigForgeRoundedRect || bg is FigForgeLayeredRect))
            {
                var fill = ShapeFills(c.shape).Count > 0 ? ShapeFills(c.shape)[0] : FigForgeFill.None;
                var states = bg.gameObject.AddComponent<FigForgeButtonStateColors>();
                SetStates(states, fill, new CanonicalStateColors
                {
                    highlighted = c.bgRollover,
                    pressed = c.bgPressed,
                });
                return;
            }

            var graphicStates = bg.gameObject.AddComponent<FigForgeGraphicStateColors>();
            graphicStates.target = bg;
            graphicStates.normal = bg.color;
            graphicStates.highlighted = c.bgRollover != null ? ToColor(c.bgRollover) : bg.color;
            graphicStates.pressed = c.bgPressed != null ? ToColor(c.bgPressed) : graphicStates.highlighted;
        }

        // Build the standard (Viewport/Content/Item) TMP_Dropdown popup template, hidden.
        static void BuildDropdownTemplate(GameObject root, CanonicalRef c, BuildContext ctx, TMP_Dropdown dd)
        {
            float sf = ctx.scaleFactor;
            float itemHeight = c.optionHeight > 0.01f ? c.optionHeight * sf : 28f * sf;
            var template = NewRect("Template", root.transform);
            var trt = template.GetComponent<RectTransform>();
            trt.anchorMin = new Vector2(0, 0); trt.anchorMax = new Vector2(1, 0); trt.pivot = new Vector2(0.5f, 1);
            trt.anchoredPosition = new Vector2(0, 2 * sf);
            trt.sizeDelta = new Vector2(0, Mathf.Max(itemHeight, itemHeight * Mathf.Min(Mathf.Max(c.options != null ? c.options.Count : 3, 1), 6)));
            var popupShape = c.popupShape ?? c.shape ?? c.optionShape;
            if (popupShape != null)
            {
                AddShapeGraphic(template, popupShape, ctx);
                template.AddComponent<Mask>().showMaskGraphic = true;
            }
            else
                template.AddComponent<Image>().color = Color.white;
            var scroll = template.AddComponent<ScrollRect>();
            scroll.horizontal = false; scroll.vertical = true; scroll.movementType = ScrollRect.MovementType.Clamped;

            var viewport = NewRect("Viewport", template.transform);
            var vrt = viewport.GetComponent<RectTransform>();
            vrt.anchorMin = Vector2.zero; vrt.anchorMax = Vector2.one; vrt.sizeDelta = Vector2.zero; vrt.pivot = new Vector2(0, 1);
            viewport.AddComponent<Image>().color = Color.white;
            viewport.AddComponent<Mask>().showMaskGraphic = false;
            scroll.viewport = vrt;

            var content = NewRect("Content", viewport.transform);
            var crt = content.GetComponent<RectTransform>();
            crt.anchorMin = new Vector2(0, 1); crt.anchorMax = new Vector2(1, 1); crt.pivot = new Vector2(0.5f, 1);
            crt.sizeDelta = new Vector2(0, itemHeight);
            scroll.content = crt;

            var item = NewRect("Item", content.transform);
            var irt = item.GetComponent<RectTransform>();
            irt.anchorMin = new Vector2(0, 0.5f); irt.anchorMax = new Vector2(1, 0.5f); irt.sizeDelta = new Vector2(0, itemHeight);
            var itemToggle = item.AddComponent<Toggle>(); itemToggle.transition = Selectable.Transition.None;

            var itemBg = NewRect("Item Background", item.transform);
            Stretch(itemBg.GetComponent<RectTransform>());
            Graphic itemGraphic;
            if (c.optionShape != null)
            {
                itemGraphic = AddShapeGraphic(itemBg, c.optionShape, ctx);
                ApplyDropdownOptionStates(item, itemGraphic, itemToggle, c, ctx);
            }
            else
            {
                var ibImg = itemBg.AddComponent<Image>(); ibImg.color = new Color(0.96f, 0.96f, 0.98f);
                var colors = itemToggle.colors;
                if (c.optionRollover != null) colors.highlightedColor = ToColor(c.optionRollover);
                if (c.optionPressed != null) colors.pressedColor = ToColor(c.optionPressed);
                if (c.optionSelected != null) colors.selectedColor = ToColor(c.optionSelected);
                itemToggle.colors = colors;
                itemToggle.transition = Selectable.Transition.None;
                itemGraphic = ibImg;
            }
            itemToggle.targetGraphic = itemGraphic;
            MakeClickTarget(itemGraphic, ctx);
            itemToggle.graphic = null; // selected state is represented by optionSelected fill, not a TMP checkmark box

            var itemLbl = NewRect("Item Label", item.transform);
            var ilrt = itemLbl.GetComponent<RectTransform>();
            ilrt.anchorMin = Vector2.zero; ilrt.anchorMax = Vector2.one; ilrt.offsetMin = new Vector2(12 * sf, 1 * sf); ilrt.offsetMax = new Vector2(-12 * sf, -2 * sf);
            var itemText = itemLbl.AddComponent<TextMeshProUGUI>();
            itemText.text = "Option"; itemText.color = new Color(0.1f, 0.1f, 0.12f); itemText.fontSize = 14f * ctx.scaleFactor;
            itemText.raycastTarget = false;
            itemText.alignment = TextAlignmentOptions.MidlineLeft; ApplyFont(itemText, null, ctx);

            dd.template = trt;
            dd.itemText = itemText;
            template.SetActive(false);
        }

        static void ApplyDropdownOptionStates(GameObject item, Graphic graphic, Toggle itemToggle, CanonicalRef c, BuildContext ctx)
        {
            if (graphic == null || c.optionShape == null) return;
            var normal = ShapeStyle(c.optionShape, ctx);
            var highlighted = c.optionRolloverShape != null
                ? ShapeStyle(c.optionRolloverShape, ctx)
                : (c.optionRollover != null ? normal.WithFill(FigForgeFill.Solid(ToColor(c.optionRollover))) : normal);
            var pressed = c.optionPressedShape != null
                ? ShapeStyle(c.optionPressedShape, ctx)
                : (c.optionPressed != null ? normal.WithFill(FigForgeFill.Solid(ToColor(c.optionPressed))) : highlighted);
            bool hasSelected = c.optionSelectedShape != null || c.optionSelected != null;
            var selected = c.optionSelectedShape != null
                ? ShapeStyle(c.optionSelectedShape, ctx)
                : (c.optionSelected != null ? normal.WithFill(FigForgeFill.Solid(ToColor(c.optionSelected))) : normal);

            var states = item.AddComponent<FigForgeToggleStateColors>();
            states.target = graphic as FigForgeRoundedRect;
            states.layeredTarget = graphic as FigForgeLayeredRect;
            states.toggle = itemToggle;
            states.useShapeStyles = true;
            states.normalShape = normal;
            states.highlightedShape = highlighted;
            states.pressedShape = pressed;
            states.selectedShape = selected;
            states.hasSelected = hasSelected;
            states.normal = normal.fill;
            states.highlighted = highlighted.fill;
            states.pressed = pressed.fill;
            states.selected = selected.fill;
        }

        // List: a scrollable, masked, rounded container that repeats ONE interactive Item
        // row `count` times. Each row is its own Button with a FigForgeRoundedRect from the
        // template's Regular shape and a rollover-colour swap (FigForgeButtonStateColors).
        // Built per-instance (not a shared prefab) so each list's height sets its row count.
        static GameObject BuildList(ElementData e, Transform parent, BuildContext ctx)
        {
            var c = e.canonical;
            float sf = ctx.scaleFactor;
            var go = NewRect(string.IsNullOrEmpty(e.name) ? "List" : e.name, parent);
            // Rounded container background — full render-only subtree when present (added
            // first so it sits behind the scrollable viewport/content), else the flat shape.
            Graphic bg;
            if (HasPartTree(c, "Background"))
            {
                var bgGo = NewRect("Background", go.transform);
                Stretch(bgGo.GetComponent<RectTransform>());
                bg = BuildPartSubtree(bgGo, c.partTrees["Background"], ctx);
            }
            else bg = AddShapeGraphic(go, c.shape, ctx);

            // Optional Header — a full render-only subtree pinned to the top; the scroll
            // viewport is inset below it so rows don't slide under the header.
            float headerH = c.headerHeight > 0.01f ? c.headerHeight * sf : 0f;
            if (headerH > 0f && HasPartTree(c, "Header"))
            {
                var headerGo = NewRect("Header", go.transform);
                var hrt = headerGo.GetComponent<RectTransform>();
                hrt.anchorMin = new Vector2(0, 1); hrt.anchorMax = new Vector2(1, 1); hrt.pivot = new Vector2(0.5f, 1);
                hrt.anchoredPosition = Vector2.zero; hrt.sizeDelta = new Vector2(0, headerH);
                BuildPartSubtree(headerGo, c.partTrees["Header"], ctx);
            }

            var scroll = go.AddComponent<ScrollRect>();
            scroll.horizontal = false; scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Elastic; scroll.scrollSensitivity = 20f;

            // The viewport is the clip region, DESIGNER-DEFINED where possible:
            //   1. An explicit (usually hidden) 'Mask' layer in the Figma component gives
            //      the exact scroll/clip box — anchored verbatim.
            //   2. Otherwise the box is derived: the Background INTERIOR, inset past the
            //      background stroke + 1px of SDF anti-aliasing (so row edges can't poke
            //      outside the rounded background at fractional canvas scales), excluding
            //      the Header strip at the top.
            // Whether content CLIPS at all also follows the designer: the Figma frame's
            // clipsContent (an explicit Mask layer implies clipping).
            float bgStrokeW = c.shape != null && c.shape.stroke != null ? Mathf.Max(0f, c.shape.stroke.weight) : 0f;
            float maskInset = (bgStrokeW + 1f) * sf;
            bool hasMaskPart = c.parts != null && c.parts.ContainsKey("Mask");
            var viewport = NewRect("Viewport", go.transform);
            var vrt = viewport.GetComponent<RectTransform>();
            if (hasMaskPart)
            {
                AnchorPart(vrt, c.parts, "Mask");
            }
            else
            {
                vrt.anchorMin = Vector2.zero; vrt.anchorMax = Vector2.one;
                vrt.offsetMin = new Vector2(maskInset, maskInset);
                vrt.offsetMax = new Vector2(-maskInset, -(headerH > 0f ? headerH : maskInset));
            }
            if (viewport.GetComponent<CanvasRenderer>() == null) viewport.AddComponent<CanvasRenderer>();
            float maskR = (c.maskShape != null ? c.maskShape.cornerRadius : 0f) * sf;
            bool listClips = e.clipsContent || hasMaskPart;
            if (listClips && maskR > 0.5f)
            {
                // ROUNDED clip, designer-defined by the Mask layer's own corner radius.
                // The rounded SDF graphic doubles as drag target and stencil source — its
                // shader discards outside the rounded shape, so the stencil Mask clips
                // rows to the curve at EVERY scroll position (same pattern as ApplyClip).
                var rr = viewport.AddComponent<FigForgeRoundedRect>();
                rr.Configure(FigForgeFill.Solid(new Color(1f, 1f, 1f, 0.004f)), FigForgeStroke.None,
                    new Vector4(maskR, maskR, maskR, maskR));
                rr.raycastTarget = true; // the scroll drag surface
                viewport.AddComponent<Mask>().showMaskGraphic = true;
            }
            else
            {
                viewport.AddComponent<Image>().color = new Color(1, 1, 1, 0.004f); // near-invisible drag/clip target
                if (listClips)
                {
                    var mask = viewport.AddComponent<RectMask2D>();
                    // Feather the clip edge ~1px so hard row edges blend like the AA'd background edge.
                    int soft = Mathf.Max(1, Mathf.RoundToInt(sf));
                    mask.softness = new Vector2Int(soft, soft);
                }
            }
            scroll.viewport = vrt;

            var content = NewRect("Content", viewport.transform);
            var crt = content.GetComponent<RectTransform>();
            crt.anchorMin = new Vector2(0, 1); crt.anchorMax = new Vector2(1, 1); crt.pivot = new Vector2(0.5f, 1); crt.sizeDelta = Vector2.zero;
            var vlg = content.AddComponent<VerticalLayoutGroup>();
            // childControlHeight MUST be true: rows carry LayoutElement preferredHeight =
            // rowHeight, and a layout group only honours LayoutElement on a controlled axis.
            // With false it uses each child's raw sizeDelta.y instead — and a cloned
            // RowTemplate is stretch-anchored (sizeDelta.y = 0), so every row laid out
            // zero-height and the viewport mask hid the whole list.
            vlg.childControlWidth = true; vlg.childControlHeight = true; vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false; vlg.spacing = 0;
            content.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scroll.content = crt;

            // Scrollbar — a real uGUI Scrollbar on the right edge, styled from the captured
            // 'Scrollbar' layer (Track/Thumb shapes + ThumbRollover/Pressed colours); a
            // neutral rounded thumb when the design has none. Overlay style: floats over
            // the rows and auto-hides when they fit. INTERACTION uses plain transparent
            // Images on the bar + handle (the stock uGUI raycast pattern) — the SDF shapes
            // are render-only children, so dragging never depends on SDF raycast geometry.
            float sbWFig = c.scrollbarWidth > 0.01f ? c.scrollbarWidth : 6f;
            float sbW = sbWFig * sf;
            var sbGo = NewRect("Scrollbar", go.transform);
            var srt = sbGo.GetComponent<RectTransform>();
            srt.pivot = new Vector2(1f, 0.5f);
            if (hasMaskPart)
            {
                // Hug the right edge of the designer's clip region.
                var mp = c.parts["Mask"]; // [minX, minY, maxX, maxY] normalized
                srt.anchorMin = new Vector2(mp[2], mp[1]);
                srt.anchorMax = new Vector2(mp[2], mp[3]);
                srt.offsetMin = new Vector2(-sbW, 0f);
                srt.offsetMax = Vector2.zero;
            }
            else
            {
                srt.anchorMin = new Vector2(1f, 0f); srt.anchorMax = Vector2.one;
                srt.offsetMin = new Vector2(-(sbW + maskInset), maskInset);
                srt.offsetMax = new Vector2(-maskInset, -(headerH > 0f ? headerH + maskInset : maskInset));
            }
            if (sbGo.GetComponent<CanvasRenderer>() == null) sbGo.AddComponent<CanvasRenderer>();
            var sbHit = sbGo.AddComponent<Image>();
            sbHit.color = new Color(0, 0, 0, 0);
            sbHit.raycastTarget = true; // track click pages the list
            if (c.scrollTrackShape != null)
            {
                var trackGo = NewRect("Track", sbGo.transform);
                Stretch(trackGo.GetComponent<RectTransform>());
                AddShapeGraphic(trackGo, c.scrollTrackShape, ctx); // render-only
            }
            var slideGo = NewRect("Sliding Area", sbGo.transform);
            Stretch(slideGo.GetComponent<RectTransform>());
            var handleGo = NewRect("Handle", slideGo.transform);
            var handleRt = handleGo.GetComponent<RectTransform>();
            handleRt.anchorMin = Vector2.zero; handleRt.anchorMax = Vector2.one;
            handleRt.offsetMin = Vector2.zero; handleRt.offsetMax = Vector2.zero;
            if (handleGo.GetComponent<CanvasRenderer>() == null) handleGo.AddComponent<CanvasRenderer>();
            var handleHit = handleGo.AddComponent<Image>();
            handleHit.color = new Color(0, 0, 0, 0);
            handleHit.raycastTarget = true; // thumb drag surface
            var thumbGo = NewRect("Thumb", handleGo.transform);
            Stretch(thumbGo.GetComponent<RectTransform>());
            var thumbShape = c.scrollThumbShape ?? new CanonicalShape
            { cornerRadius = sbWFig * 0.5f, fill = new float[] { 0.55f, 0.56f, 0.6f, 0.55f } };
            var thumbVisual = AddShapeGraphic(thumbGo, thumbShape, ctx); // render-only
            var sbar = sbGo.AddComponent<Scrollbar>();
            sbar.transition = Selectable.Transition.None;
            sbar.interactable = true;
            sbar.direction = Scrollbar.Direction.BottomToTop;
            sbar.handleRect = handleRt;
            sbar.targetGraphic = handleHit;
            sbar.value = 1f; // start at the top
            scroll.verticalScrollbar = sbar;
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
            // Thumb hover/press tint: captured ThumbRollover/Pressed colours, or a brighter
            // version of the regular fill so hover feedback always exists.
            var thumbReg = thumbShape.fill != null && thumbShape.fill.Length >= 4
                ? ToColor(thumbShape.fill) : new Color(0.55f, 0.56f, 0.6f, 0.55f);
            var sbStates = sbGo.AddComponent<FigForgeScrollbarStates>();
            sbStates.bound = thumbVisual;
            sbStates.regular = FigForgeFill.Solid(thumbReg);
            sbStates.rollover = FigForgeFill.Solid(c.scrollThumbRollover != null
                ? ToColor(c.scrollThumbRollover)
                : new Color(thumbReg.r, thumbReg.g, thumbReg.b, Mathf.Clamp01(thumbReg.a * 1.4f)));
            sbStates.pressed = FigForgeFill.Solid(c.scrollThumbPressed != null
                ? ToColor(c.scrollThumbPressed)
                : new Color(thumbReg.r * 0.85f, thumbReg.g * 0.85f, thumbReg.b * 0.85f, Mathf.Clamp01(thumbReg.a * 1.6f)));
            sbStates.hasRollover = true;
            sbStates.hasPressed = true;

            float rowH = (c.itemHeight > 0.01f ? c.itemHeight : 44f) * sf;
            int count = c.count > 0 ? c.count : 5;
            bool hasHover = c.itemRollover != null;
            Color hover = hasHover ? ToColor(c.itemRollover) : Color.white;
            string labelTmpl = string.IsNullOrEmpty(c.label) ? "Item" : c.label;
            var list = go.AddComponent<FigForgeList>();
            list.Configure(crt, rowH, labelTmpl, ToListRowStyle(c.itemShape, sf), hover, hasHover);
            // Row corner rounding (containerCorners) stays zero: the clip shape is owned by
            // the designer's Mask layer now — its corner radius rounds the clip itself, so
            // nothing is derived from the Background's corners anymore.

            // Rich rows: build the captured Item subtree once as a hidden template that
            // FigForgeList clones per row (icon/title/subtitle/accessory/divider), with
            // per-state row fills (Regular/Rollover/Pressed/Selected) + single-select.
            if (HasPartTree(c, "Item"))
            {
                var tpl = NewRect("RowTemplate", go.transform);
                Stretch(tpl.GetComponent<RectTransform>());
                BuildPartSubtree(tpl, c.partTrees["Item"], ctx);
                tpl.SetActive(false); // never shown directly; cloned per row at build/runtime
                list.rowTemplate = tpl;
                list.rowRegular = ToListRowStyle(c.itemShape, sf).fill;
                list.rowRollover = FigForgeFill.Solid(hover); list.rowHasRollover = hasHover;
                list.rowPressed = FigForgeFill.Solid(c.itemPressed != null ? ToColor(c.itemPressed) : hover); list.rowHasPressed = c.itemPressed != null;
                list.rowSelected = FigForgeFill.Solid(c.itemSelected != null ? ToColor(c.itemSelected) : hover); list.rowHasSelected = c.itemSelected != null;
            }

            // Populate rows from the captured per-row data when present; else generate
            // `count` placeholder rows. Runtime code can replace them via list.SetItems.
            if (c.listItems != null && c.listItems.Count > 0)
            {
                var rows = new List<FigForgeListItem>(c.listItems.Count);
                foreach (var li in c.listItems)
                    if (li != null) rows.Add(new FigForgeListItem(li.title, li.subtitle));
                list.SetItems(rows);
            }
            else list.CreatePreviewRows(count);

            var bind = go.AddComponent<FigForgeBindings>(); bind.background = bg;
            return go;
        }

        static FigForgeListRowStyle ToListRowStyle(CanonicalShape sh, float sf)
        {
            var style = new FigForgeListRowStyle { enabled = sh != null };
            if (sh == null) return style;
            ApplyShapeValues(sh, sf, out style.fill,
                out style.stroke, out style.corners);
            var firstShadow = FirstDropShadow(sh);
            if (firstShadow != null && firstShadow.color != null)
            {
                style.shadowColor = ToColor(firstShadow.color);
                style.shadowOffset = new Vector2(firstShadow.offsetX * sf, firstShadow.offsetY * sf);
                style.shadowBlur = firstShadow.blur * sf;
                style.shadowSpread = firstShadow.spread * sf;
            }
            return style;
        }

        static Sprite SpriteByFile(string file, BuildContext ctx)
        {
            return !string.IsNullOrEmpty(file) && ctx.sprites.TryGetValue(file, out var s) ? s : null;
        }

        static GameObject BuildPlaceholderButton(ElementData e, Transform parent, BuildContext ctx)
        {
            var go = NewRect(e.name, parent);
            var img = go.AddComponent<Image>();
            img.color = new Color(0.45f, 0.36f, 1f, 1f);
            var btn = go.AddComponent<FigForgeButton>();
            var label = NewRect("Label", go.transform);
            Stretch(label.GetComponent<RectTransform>());
            var tmp = label.AddComponent<TextMeshProUGUI>();
            tmp.text = e.canonical?.label ?? e.name;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            ApplyFontSize(tmp, e.canonical?.defLabelFontSize ?? 16f, ctx);
            ConfigureButtonLabelText(tmp);
            btn.tmpTxt_label = tmp;
            return go;
        }

        static void StampLabel(GameObject inst, string label)
        {
            if (string.IsNullOrEmpty(label)) return;
            var tmp = inst.GetComponentInChildren<TMP_Text>(true);
            if (tmp != null) { tmp.text = label; ConfigureButtonLabelText(tmp); return; }
            var ui = inst.GetComponentInChildren<Text>(true);
            if (ui != null) ui.text = label;
        }

        static void ConfigureButtonLabelText(TMP_Text tmp)
        {
            if (tmp == null) return;
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.overflowMode = TextOverflowModes.Overflow;
            tmp.enableAutoSizing = false;
        }

        // Apply a specific canonical label font (family/style) to a TMP label;
        // null → Inter/Regular. The prefab/definition uses the COMPONENT's font
        // (defLabelFont); an instance only overrides when its font differs.
        static void ApplyFont(TMP_Text label, CanonicalLabelFont lf, BuildContext ctx)
        {
            if (label == null) return;
            string fam = lf != null && !string.IsNullOrEmpty(lf.family) ? lf.family : "Inter";
            string sty = lf != null && !string.IsNullOrEmpty(lf.style) ? lf.style : "Regular";
            var font = ctx.resolveFont?.Invoke(fam, sty);
            if (font != null) label.font = font;
            label.fontStyle = FauxStyle(sty, font);
        }

        static void ApplyFontSize(TMP_Text label, float figmaPx, BuildContext ctx)
        {
            if (label == null) return;
            label.fontSize = Mathf.Max(1f, figmaPx * ctx.scaleFactor);
        }

        static bool SameFont(CanonicalLabelFont a, CanonicalLabelFont b)
        {
            string af = a != null ? (a.family ?? "") : "", asy = a != null ? (a.style ?? "") : "";
            string bf = b != null ? (b.family ?? "") : "", bsy = b != null ? (b.style ?? "") : "";
            return string.Equals(af, bf, System.StringComparison.OrdinalIgnoreCase)
                && string.Equals(asy, bsy, System.StringComparison.OrdinalIgnoreCase);
        }

        // ---- Canonical prefab: one Figma component → one reusable Unity prefab --
        const string CanonicalFolder = "Assets/FigForge/Canonical";
        const string CanonicalLibraryPath = "Assets/FigForge/FigForgeCanonicalLibrary.asset";

        /// <summary>
        /// Resolve a canonical (kind, ref) to a reusable prefab — the heart of the
        /// "define once, reference everywhere" model. A hand-made library prefab
        /// wins; otherwise an existing generated prefab is reused; otherwise one is
        /// generated from the Figma component (state sprites → SpriteSwap, else a
        /// placeholder), saved, and registered in the auto-managed library. Reuse
        /// is deliberate so manual skinning survives re-imports.
        /// </summary>
        static GameObject ResolveOrGenerateCanonicalPrefab(ElementData e, BuildContext ctx)
        {
            string kind = string.IsNullOrEmpty(e.canonical.kind) ? "button" : e.canonical.kind;
            string refName = e.canonical.Ref;
            if (string.IsNullOrEmpty(refName)) return null;
            string sig = CanonicalSignature(e, kind, ctx.scaleFactor);
            bool signatureShare = CanShareCanonicalBySignature(e, kind);

            // Candidate prefab: a library-mapped one (hand-made or previously
            // generated) wins lookup; else an existing generated prefab on disk.
            string path = $"{CanonicalFolder}/{SafeAsset(refName)}.prefab";
            var lib = ctx.canonical ?? LoadOrCreateCanonicalLibrary();
            ctx.canonical = lib;
            var refEntry = lib.ResolveEntry(kind, refName);
            GameObject candidate = refEntry != null ? refEntry.prefab : null;
            if (candidate == null) candidate = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(path);

            if (candidate != null)
            {
                // Regenerate ONLY a FigForge-managed prefab (one living in the auto-
                // managed Canonical folder) whose Figma definition changed — so design
                // edits (stroke weight, fill, colours, font…) actually apply. A hand-made
                // prefab mapped from elsewhere is ALWAYS reused (manual skin survives).
                // A managed prefab with no/old signature (made before signatures existed)
                // regenerates once, then tracks changes.
                string candidatePath = UnityEditor.AssetDatabase.GetAssetPath(candidate);
                bool managed = candidatePath != null && candidatePath.StartsWith(CanonicalFolder + "/", StringComparison.Ordinal);
                var stamp = candidate.GetComponent<FigForgeBindings>();
                string prevSig = !string.IsNullOrEmpty(refEntry != null ? refEntry.signature : null)
                    ? refEntry.signature
                    : (stamp != null ? stamp.signature : null);
                bool stale = managed && prevSig != sig;
                if (!stale)
                {
                    RegisterInLibrary(ctx, kind, refName, candidate, signatureShare && managed ? sig : null);
                    return candidate;
                }
                ctx.log($"canonical {kind} '{refName}' definition changed — regenerating prefab.");
            }

            if (signatureShare)
            {
                var sigEntry = lib.ResolveSignature(kind, sig);
                if (sigEntry != null && sigEntry.prefab != null)
                {
                    ctx.log($"canonical {kind} '{refName}' shares prefab with '{sigEntry.referenceName}' (signature match).");
                    RegisterInLibrary(ctx, kind, refName, sigEntry.prefab, sig);
                    return sigEntry.prefab;
                }
            }

            // Generate from the canonical definition, save (overwrites if present), register.
            GameObject temp =
                (kind == "button" && e.canonical.shape != null) ? BuildShapeButton(e, null, ctx)   // crisp SDF shader
                : (kind == "button" && e.canonical.states != null) ? BuildStateButton(e, null, ctx) // exported state PNGs
                : ((kind == "toggle" || kind == "radio") && e.canonical.shape != null) ? BuildToggle(e, null, ctx)
                : (kind == "input") ? BuildInputField(e, null, ctx)
                : (kind == "dropdown") ? BuildDropdown(e, null, ctx)
                : (kind == "list") ? BuildList(e, null, ctx)
                : BuildPlaceholderButton(e, null, ctx);
            if (temp == null) return candidate; // generation failed — keep whatever we had
            temp.name = SafeAsset(refName);

            // Wire binding slots so per-instance label/value apply onto the prefab.
            var bind = temp.GetComponent<FigForgeBindings>() ?? temp.AddComponent<FigForgeBindings>();
            if (kind != "list") // a list has no single label/Selectable — its rows are data-driven via FigForgeList.SetItems
            {
                bind.label = temp.GetComponentInChildren<TMP_Text>(true);
                bind.control = temp.GetComponent<Selectable>();
            }
            bind.signature = sig; // stamp so a later definition change triggers regen

            TextureImportHelper.EnsureFolder(CanonicalFolder);
            var prefab = UnityEditor.PrefabUtility.SaveAsPrefabAsset(temp, path);
            UnityEngine.Object.DestroyImmediate(temp);
            if (prefab != null)
            {
                ctx.log($"generated canonical {kind} '{refName}' → {path}");
                RegisterInLibrary(ctx, kind, refName, prefab, signatureShare ? sig : null);
            }
            return prefab;
        }

        // A stable string of the DEFINITION fields that determine the generated
        // prefab's look. When it changes, the prefab is regenerated. Excludes
        // per-instance data (label text, position) and state-PNG filenames (which
        // embed the instance name) so distinct instances don't thrash regeneration.
        // Size is included because a component's visual definition includes the master
        // bounds used to author the canonical control.
        // Bump when the GENERATED prefab's component layout changes (not its data) so an
        // importer upgrade auto-regenerates managed prefabs instead of leaving stale
        // serialization behind. v2: FigForgeButtonStateColors → grouped FigForgeFill.
        // v4: dropdown selected option fill + closed background hover/press fills.
        // v5: dropdown arrow glyph switched to ASCII to avoid TMP fallback warnings.
        // v6: dropdown background SDF moved to a child targetGraphic.
        // v7: popup options no longer use TMP's checkmark box or ColorTint.
        // v8: popup item hover state driven from Toggle item root.
        // v9: dropdown Arrow renders from Figma sprite(s), not a TMP fallback glyph.
        // v10: dropdown popup option states carry full shape style, not fill only.
        // v11: dropdown popup rows get edge-aware first/middle/last corner rules.
        // v12: dropdown popup shell uses option shape styling instead of a hard-edged white backing.
        // v13: popup shell can be styled independently from the Options frame.
        // v14: popup shell also masks dropdown option rows to its rounded silhouette.
        // v15: popup shell falls back to the closed select-box shape before option rows.
        // v16: generated input canonical prefabs build TMP_InputField instead of placeholders.
        // v17: canonical procedural gradients carry full n-stop Gradient data.
        // v18: generated buttons use Regular/RollOver/Pressed/HitArea child objects.
        // v19: button root/state shapes preserve layered fills, strokes, and effects.
        // v20: control backgrounds are raycastable (clickable toggle/radio/dropdown/input).
        // v21: control backgrounds raycast UNCONDITIONALLY (not gated by disableRaycasts,
        //      which defaults on and was suppressing clicks on toggle/radio/dropdown/input).
        // v22: toggle/radio get a full-component HitArea (or captured "HitArea" layer) so
        //      the whole Figma component frame is clickable, not just the small box.
        // v23: canonical part visuals render as full Figma subtrees (render-only) when
        //      partTrees present — nested children/fills/strokes/shadows/vectors/masks/text.
        // v24: subtree parity extended to dropdown Background+Arrow and input Background.
        // v25: subtree parity extended to the list container Background.
        // v26: list rows clone the rich Item subtree (icon/title/subtitle/accessory) with
        //      Regular/Rollover/Pressed/Selected states + single-select, and a pinned Header.
        // v27: list rows come from captured per-row items (title+subtitle), bound per row.
        // v28: canonical list part layers (row state backgrounds, Icon, container
        //      Background) stay procedural instead of baking to PNG — Unity rebuilds
        //      them via the SDF FigForgeRoundedRect path; the flat row Regular is a
        //      tintable Image so FigForgeListRow's per-state recolour applies.
        // v29: canonical controls are FigForge-owned types (FigForgeButton/Toggle/
        //      Dropdown/InputField) with typed part refs (label/checkmark) wired here,
        //      so generated frame accessors can return them strongly-typed.
        // v30: the List is now prefab-backed like the other controls (shared prefab per
        //      ref → edit-one-propagate); signature drops preview row count and folds
        //      itemPressed/itemSelected/headerHeight as the visual fingerprint.
        // NOTE: bumping this also busts the importer's screen-level reuse cache
        // (folded into FigForgeImporterWindow.ManifestHash), so a schema change
        // forces unchanged screens to rebuild and pick up the new generation.
        // v31: toggle/radio Label spans full control height + vertical-middle alignment
        //      (was MidlineLeft in a text-height-hugging box, which sat high).
        // v32: same vertical-middle treatment for dropdown caption + input placeholder/
        //      text; dropdown/input Background kept procedural (SDF) when shape covers it.
        // v33: list content VerticalLayoutGroup controls child height, so rows honour their
        //      LayoutElement rowHeight (stretch-anchored template clones laid out 0-high).
        // v34: list rows find Title/Subtitle/Regular/HitArea case-insensitively (captured
        //      subtree names are sanitized lowercase), always get a raycastable hit surface,
        //      and serialize the state-colour binding (was lost entering play mode).
        // v35: list viewport masks the background INTERIOR (inset past stroke + 1px AA, so
        //      rows can't visually bleed at fractional scales) + styled uGUI Scrollbar from
        //      the captured 'Scrollbar' layer (auto-hides when rows fit).
        // v36: first/last list rows take the container's corner radii (top pair zeroed
        //      under a Header), so header-less lists keep their rounded corners.
        // v37: scrollbar interaction via plain transparent Images (bar + handle) with the
        //      SDF shapes as render-only children; thumb hover/press tint from the captured
        //      ThumbRollover/ThumbPressed layers (FigForgeScrollbarStates).
        // v38: designer-defined list clipping — an explicit 'Mask' layer anchors the
        //      viewport (scrollbar hugs its right edge), and the Figma frame's clipsContent
        //      decides whether the list clips at all.
        // v39: the Mask layer's own corner radius rounds the clip (rounded stencil mask,
        //      correct at every scroll position); background-corner-derived row rounding
        //      removed — the clip shape is entirely designer-owned.
        internal const int CanonicalSchema = 39;

        // Deterministic FNV-1a hash for signature terms (GetHashCode is randomized per run).
        static string SigHash(string s)
        {
            ulong h = 14695981039346656037UL;
            if (s != null) foreach (char ch in s) { h ^= ch; h *= 1099511628211UL; }
            return h.ToString("x");
        }

        static string CanonicalSignature(ElementData e, string kind, float sf)
        {
            var c = e.canonical;
            var sb = new System.Text.StringBuilder();
            sb.Append("v=").Append(CanonicalSchema).Append(";k=").Append(kind).Append(";sf=").Append(sf.ToString("0.###"));
            if (e.rect != null)
                sb.Append(";sz=").Append(e.rect.w.ToString("0.###")).Append('x').Append(e.rect.h.ToString("0.###"));
            var sh = c.shape;
            if (sh != null)
                sb.Append(";cr=").Append(sh.cornerRadius.ToString("0.###"))
                  .Append(";f=").Append(SigF(sh.fill)).Append(";f2=").Append(SigF(sh.fill2))
                  .Append(";fg=").Append(SigGradient(sh.gradient))
                  .Append(";gt=").Append(SigF(sh.gradientTransform))
                  .Append(";st=").Append(SigStroke(sh.stroke, sh.borderColor, sh.borderWidth, sh.borderAlign));
            AppendShapeSig(sb, "btn", sh);
            AppendShapeSig(sb, "root", c.rootShape);
            if (sh != null && sh.shadow != null)
            {
                var sd = sh.shadow;
                sb.Append(";shc=").Append(SigF(sd.color)).Append(";sho=").Append(sd.offsetX.ToString("0.###")).Append(',').Append(sd.offsetY.ToString("0.###"))
                  .Append(";shb=").Append(sd.blur.ToString("0.###")).Append(";shs=").Append(sd.spread.ToString("0.###"));
            }
            var sc = c.stateColors;
            if (sc != null)
                sb.Append(";sn=").Append(SigF(sc.normal)).Append(";sh=").Append(SigF(sc.highlighted)).Append(";sp=").Append(SigF(sc.pressed));
            if (c.stateShapes != null)
            {
                AppendShapeSig(sb, "ssn", c.stateShapes.normal);
                AppendShapeSig(sb, "ssh", c.stateShapes.highlighted);
                AppendShapeSig(sb, "ssp", c.stateShapes.pressed);
            }
            if (kind == "input")
                sb.Append(";ph=").Append(c.placeholder ?? "").Append(";iv=").Append(c.value ?? "");
            if (c.checkShape != null)
            {
                var cs = c.checkShape;
                sb.Append(";ckcr=").Append(cs.cornerRadius.ToString("0.###"))
                  .Append(";ckf=").Append(SigF(cs.fill)).Append(";ckf2=").Append(SigF(cs.fill2))
                  .Append(";ckfg=").Append(SigGradient(cs.gradient))
                  .Append(";ckgt=").Append(SigF(cs.gradientTransform))
                  .Append(";ckst=").Append(SigStroke(cs.stroke, cs.borderColor, cs.borderWidth, cs.borderAlign));
            }
            if (c.optionShape != null)
            {
                var os = c.optionShape;
                sb.Append(";oh=").Append(c.optionHeight.ToString("0.###"))
                  .Append(";ocr=").Append(os.cornerRadius.ToString("0.###"))
                  .Append(";of=").Append(SigF(os.fill)).Append(";of2=").Append(SigF(os.fill2))
                  .Append(";ofg=").Append(SigGradient(os.gradient))
                  .Append(";ogt=").Append(SigF(os.gradientTransform))
                  .Append(";ost=").Append(SigStroke(os.stroke, os.borderColor, os.borderWidth, os.borderAlign));
                if (os.shadow != null)
                {
                    var sd = os.shadow;
                    sb.Append(";oshc=").Append(SigF(sd.color)).Append(";osho=").Append(sd.offsetX.ToString("0.###")).Append(',').Append(sd.offsetY.ToString("0.###"))
                      .Append(";oshb=").Append(sd.blur.ToString("0.###")).Append(";oshs=").Append(sd.spread.ToString("0.###"));
                }
            }
            AppendShapeSig(sb, "pop", c.popupShape);
            AppendShapeSig(sb, "ors", c.optionRolloverShape);
            AppendShapeSig(sb, "ops", c.optionPressedShape);
            AppendShapeSig(sb, "oss", c.optionSelectedShape);
            sb.Append(";or=").Append(SigF(c.optionRollover)).Append(";op=").Append(SigF(c.optionPressed)).Append(";osel=").Append(SigF(c.optionSelected));
            sb.Append(";aa=").Append(c.arrowAsset ?? "").Append(";aar=").Append(c.arrowRolloverAsset ?? "").Append(";aap=").Append(c.arrowPressedAsset ?? "");
            sb.Append(";ac=").Append(SigF(c.arrowColor)).Append(";ar=").Append(SigF(c.arrowRollover)).Append(";ap=").Append(SigF(c.arrowPressed));
            sb.Append(";bgr=").Append(SigF(c.bgRollover)).Append(";bgp=").Append(SigF(c.bgPressed));
            if (c.itemShape != null)
            {
                var ish = c.itemShape;
                sb.Append(";ih=").Append(c.itemHeight.ToString("0.###"))
                  .Append(";icr=").Append(ish.cornerRadius.ToString("0.###"))
                  .Append(";if=").Append(SigF(ish.fill)).Append(";if2=").Append(SigF(ish.fill2))
                  .Append(";ifg=").Append(SigGradient(ish.gradient))
                  .Append(";igt=").Append(SigF(ish.gradientTransform))
                  .Append(";ist=").Append(SigStroke(ish.stroke, ish.borderColor, ish.borderWidth, ish.borderAlign));
            }
            // Per-instance preview row count is NOT folded in (it's design-time placeholder
            // data; real rows come from FigForgeList.SetItems at runtime) so distinct list
            // instances share one visual prefab instead of thrash-regenerating it.
            sb.Append(";iro=").Append(SigF(c.itemRollover)).Append(";ipr=").Append(SigF(c.itemPressed)).Append(";isel=").Append(SigF(c.itemSelected));
            sb.Append(";hh=").Append(c.headerHeight.ToString("0.###"));
            if (c.parts != null && c.parts.Count > 0)
            {
                var keys = new List<string>(c.parts.Keys);
                keys.Sort(StringComparer.Ordinal);
                foreach (var key in keys)
                {
                    var a = c.parts[key];
                    sb.Append(";part=").Append(key).Append(':').Append(SigF(a));
                }
            }
            // Full part subtrees: hash each (sorted by part) so a Figma edit to a part's
            // nested visuals regenerates the prefab. Deterministic because the exporter
            // emits stable Figma ids + rounded transforms.
            if (c.partTrees != null && c.partTrees.Count > 0)
            {
                var pkeys = new List<string>(c.partTrees.Keys);
                pkeys.Sort(StringComparer.Ordinal);
                foreach (var key in pkeys)
                {
                    var tree = c.partTrees[key];
                    sb.Append(";ptree=").Append(key).Append(':')
                      .Append(tree != null ? SigHash(JsonConvert.SerializeObject(tree)) : "0");
                }
            }
            bool hasStates = c.states != null && (c.states.normal != null || c.states.highlighted != null || c.states.pressed != null);
            sb.Append(";hasShape=").Append(sh != null ? 1 : 0).Append(";hasStates=").Append(hasStates ? 1 : 0);
            if (c.defLabelFont != null)
                sb.Append(";lf=").Append(c.defLabelFont.family ?? "").Append('/').Append(c.defLabelFont.style ?? "");
            if (c.defLabelFontSize.HasValue) sb.Append(";lfs=").Append(c.defLabelFontSize.Value.ToString("0.###"));
            return sb.ToString();
        }

        static void AppendShapeSig(System.Text.StringBuilder sb, string prefix, CanonicalShape sh)
        {
            if (sh == null) return;
            sb.Append(';').Append(prefix).Append("asset=").Append(sh.asset ?? "")
              .Append(';').Append(prefix).Append("vec=").Append(SigVector(sh.vector))
              .Append(';').Append(prefix).Append("op=").Append(sh.opacity.ToString("0.###"))
              .Append(';').Append(prefix).Append("bm=").Append(sh.blendMode ?? "")
              .Append(';').Append(prefix).Append("cr=").Append(sh.cornerRadius.ToString("0.###"))
              .Append(';').Append(prefix).Append("f=").Append(SigF(sh.fill))
              .Append(';').Append(prefix).Append("f2=").Append(SigF(sh.fill2))
              .Append(';').Append(prefix).Append("fg=").Append(SigGradient(sh.gradient))
              .Append(';').Append(prefix).Append("gt=").Append(SigF(sh.gradientTransform))
              .Append(';').Append(prefix).Append("st=").Append(SigStroke(sh.stroke, sh.borderColor, sh.borderWidth, sh.borderAlign));
            if (sh.fills != null)
                for (int i = 0; i < sh.fills.Count; i++)
                    sb.Append(';').Append(prefix).Append("fill").Append(i).Append('=').Append(SigFill(sh.fills[i]));
            if (sh.strokes != null)
                for (int i = 0; i < sh.strokes.Count; i++)
                    sb.Append(';').Append(prefix).Append("stroke").Append(i).Append('=').Append(SigStroke(sh.strokes[i], null, 0f, null));
            var effects = ShapeShadows(sh);
            if (effects != null)
                for (int i = 0; i < effects.Count; i++)
                {
                    var sd = effects[i];
                    sb.Append(';').Append(prefix).Append("effect").Append(i).Append('=')
                      .Append(EffectKind(sd)).Append(',')
                      .Append(SigF(sd.color)).Append(',')
                      .Append(sd.offsetX.ToString("0.###")).Append(',')
                      .Append(sd.offsetY.ToString("0.###")).Append(',')
                      .Append(sd.blur.ToString("0.###")).Append(',')
                      .Append(sd.spread.ToString("0.###")).Append(',')
                      .Append(sd.blurMode ?? "").Append(',')
                      .Append((sd.startBlur ?? 0f).ToString("0.###")).Append(',')
                      .Append((sd.endBlur ?? 0f).ToString("0.###"));
                }
        }

        static bool CanShareCanonicalBySignature(ElementData e, string kind)
        {
            return e.canonical != null;
        }

        static string SigF(float[] a)
            => a == null ? "_" : string.Join(",", System.Array.ConvertAll(a, x => x.ToString("0.###")));

        // Compact, stable fingerprint of a vector drawing so geometry/colour edits
        // trigger a prefab regen. Folds counts + a rolling hash of verts/colours.
        static string SigVector(VectorDrawing v)
        {
            if (!HasVector(v)) return "_";
            unchecked
            {
                int h = 17;
                if (v.bounds != null) for (int i = 0; i < v.bounds.Length; i++) h = h * 31 + v.bounds[i].GetHashCode();
                int totalV = 0, totalT = 0, totalA = 0;
                foreach (var m in v.meshes)
                {
                    if (m == null) continue;
                    if (m.color != null) for (int i = 0; i < m.color.Length; i++) h = h * 31 + m.color[i].GetHashCode();
                    if (m.alpha != null) { totalA += m.alpha.Count; for (int i = 0; i < m.alpha.Count; i++) h = h * 31 + m.alpha[i].GetHashCode(); }
                    if (m.verts != null) { totalV += m.verts.Count; for (int i = 0; i < m.verts.Count; i++) h = h * 31 + m.verts[i].GetHashCode(); }
                    if (m.tris != null) { totalT += m.tris.Count; for (int i = 0; i < m.tris.Count; i++) h = h * 31 + m.tris[i]; }
                }
                return v.meshes.Count + "/" + totalV + "/" + totalT + "/" + totalA + "/" + h.ToString("x");
            }
        }

        static string SigGradient(Fill f)
        {
            if (f == null) return "_";
            var sb = new System.Text.StringBuilder();
            sb.Append(f.gradient ?? "").Append(':').Append(SigF(f.transform));
            if (f.stops != null)
                for (int i = 0; i < f.stops.Count; i++)
                    sb.Append('|').Append(f.stops[i].position.ToString("0.###")).Append('=').Append(SigF(f.stops[i].color));
            return sb.ToString();
        }

        static string SigStroke(Stroke stroke, float[] legacyColor, float legacyWidth, string legacyAlign)
        {
            if (stroke != null)
                return SigF(stroke.color) + "," + SigFill(stroke.fill) + "," + stroke.weight.ToString("0.###") + "," + (stroke.align ?? "") + "," + (stroke.dashed ? "d" : "s") + "," + SigDash(stroke.dashPattern);
            return SigF(legacyColor) + "," + legacyWidth.ToString("0.###") + "," + (legacyAlign ?? "");
        }

        static string SigDash(List<float> dashPattern)
        {
            if (dashPattern == null || dashPattern.Count == 0) return "";
            var sb = new StringBuilder();
            for (int i = 0; i < dashPattern.Count; i++)
            {
                if (i > 0) sb.Append('/');
                sb.Append(dashPattern[i].ToString("0.###"));
            }
            return sb.ToString();
        }

        static string SigFill(Fill f)
        {
            if (f == null) return "_";
            if (f.kind == "solid") return "solid:" + SigF(f.color);
            if (f.kind == "gradient") return "grad:" + SigGradient(f);
            return (f.kind ?? "") + ":" + (f.asset ?? "") + ":" + (f.scaleMode ?? "");
        }

        static void RegisterInLibrary(BuildContext ctx, string kind, string refName, GameObject prefab, string signature = null)
        {
            var lib = ctx.canonical ?? LoadOrCreateCanonicalLibrary();
            ctx.canonical = lib; // reuse for the rest of this build
            if (!CanonicalLibrary.TryParseKind(kind, out var k)) return;
            var entry = lib.entries.Find(en => en != null && en.kind == k && en.referenceName == refName);
            if (entry == null) { entry = new CanonicalLibrary.Entry { kind = k, referenceName = refName }; lib.entries.Add(entry); }
            entry.signature = signature;
            entry.prefab = prefab; // keep current (updates on regen; same ref on plain reuse)
            UnityEditor.EditorUtility.SetDirty(lib);
            UnityEditor.AssetDatabase.SaveAssets();
        }

        static CanonicalLibrary LoadOrCreateCanonicalLibrary()
        {
            var lib = UnityEditor.AssetDatabase.LoadAssetAtPath<CanonicalLibrary>(CanonicalLibraryPath);
            if (lib != null) return lib;
            var guids = UnityEditor.AssetDatabase.FindAssets("t:CanonicalLibrary");
            if (guids.Length > 0)
                return UnityEditor.AssetDatabase.LoadAssetAtPath<CanonicalLibrary>(
                    UnityEditor.AssetDatabase.GUIDToAssetPath(guids[0]));
            TextureImportHelper.EnsureFolder("Assets/FigForge");
            lib = ScriptableObject.CreateInstance<CanonicalLibrary>();
            UnityEditor.AssetDatabase.CreateAsset(lib, CanonicalLibraryPath);
            UnityEditor.AssetDatabase.SaveAssets();
            return lib;
        }

        static string SafeAsset(string s)
        {
            s = string.IsNullOrEmpty(s) ? "Canonical" : s;
            var a = new char[s.Length];
            for (int i = 0; i < s.Length; i++) a[i] = char.IsLetterOrDigit(s[i]) ? s[i] : '_';
            return new string(a);
        }

        static void AddTransparentRaycastTarget(GameObject go)
        {
            var img = go.AddComponent<Image>();
            img.color = new Color(0, 0, 0, 0);
        }

        // Captured Figma navigation → a passive FigForgeNavLink (no listener wired).
        static void AttachNav(GameObject go, ElementData e, BuildContext ctx)
        {
            if (e.nav == null || string.IsNullOrEmpty(e.nav.target)) return;
            var link = go.GetComponent<FigForgeNavLink>() ?? go.AddComponent<FigForgeNavLink>();
            link.targetScreen = e.nav.target;
            link.trigger = string.IsNullOrEmpty(e.nav.trigger) ? "click" : e.nav.trigger;
        }

        // ---- small helpers -----------------------------------------------------
        static GameObject NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.GetComponent<RectTransform>().SetParent(parent, false);
            return go;
        }
        static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        }
        static Vector2 V(float[] a, float def) =>
            a != null && a.Length >= 2 ? new Vector2(a[0], a[1]) : new Vector2(def, def);
        static Color ToColor(float[] c) =>
            c != null && c.Length >= 4 ? new Color(c[0], c[1], c[2], c[3]) : Color.white;
    }
}
