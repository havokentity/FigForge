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
using TMPro;
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
            return pageRoot;
        }

        static GameObject BuildElement(ElementData e, Dictionary<string, ElementData> index, Transform parent, BuildContext ctx)
        {
            // ---- Canonical element: instantiate a prefab (generated once from the
            // Figma component, or a hand-made one from the CanonicalLibrary) ------
            if (e.canonical != null)
            {
                string canonicalKind = string.IsNullOrEmpty(e.canonical.kind) ? "button" : e.canonical.kind;
                var prefab = canonicalKind == "list" ? null : ResolveOrGenerateCanonicalPrefab(e, ctx);
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

                // Per-instance shape override — an instance whose stroke/fill/corner
                // was tweaked in Figma re-skins just this button (shared prefab intact).
                if (e.canonical.instanceShape != null)
                    ApplyInstanceShape(inst, e.canonical.instanceShape, e.canonical.stateColors, ctx);

                // Per-instance hover/press colour override — an instance whose rollover
                // or pressed colour differs from the component re-skins just this button.
                if (e.canonical.instanceStateColors != null)
                    ApplyInstanceStateColors(inst, e.canonical.instanceStateColors);

                AttachNav(inst, e, ctx);
                if (!string.IsNullOrEmpty(e.canonical.instanceName))
                    ctx.registered.Add(new KeyValuePair<string, GameObject>(e.canonical.instanceName, inst));
                return inst;
            }

            var go = NewRect(string.IsNullOrEmpty(e.name) ? e.type : e.name, parent);
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
            bool needGraphic = hasAsset || (style != null && (style.fill != null || style.stroke != null));
            if (!needGraphic) return;

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
                ApplyOpacity(go, e, Color.white);
            }
            else if (style?.fill != null)
            {
                ApplyFill(img, style, ctx);
            }
            else
            {
                img.color = new Color(1, 1, 1, 0); // stroke-only container
            }

            if (style?.stroke != null) AddBorder(go, e, ctx);
        }

        // SDF panel covers solid (or fill-less) rounded/bordered rects AND 2-stop
        // linear gradients (rendered crisp + rounded by the shader). Image fills,
        // radial/angular gradients, and 3+ stop gradients stay on the baked path.
        static bool UseSdf(StyleData s)
        {
            if (s == null) return false;
            if (s.fill != null && s.fill.kind == "image") return false;
            if (s.fill != null && s.fill.kind == "gradient") return IsSdfGradient(s.fill);
            bool rounded = s.cornerRadius > 0.01f || AnyCorner(s.corners);
            bool border = s.stroke != null;
            bool hasShadow = s.shadows != null && s.shadows.Count > 0; // drop shadow → SDF panel renders it
            return rounded || border || hasShadow;
        }

        // Apply a captured Figma drop shadow to the SDF graphic. Figma offset is +y
        // DOWN; flip for Unity's +y-up. color.a==0 / null → no-op.
        static void ApplyShadow(FigForgeRoundedRect rr, ShadowData s, float sf)
        {
            if (s == null || s.color == null) return;
            var c = ToColor(s.color);
            if (c.a <= 0.001f) return;
            rr.SetShadow(c, new Vector2(s.offsetX * sf, -s.offsetY * sf), s.blur * sf, s.spread * sf);
        }

        // A gradient the SDF shader can render exactly: linear with two stops.
        // (3+ stops or radial/angular/diamond need the baked texture path.)
        static bool IsSdfGradient(Fill f)
            => f != null && f.kind == "gradient" && f.gradient == "linear"
               && f.stops != null && f.stops.Count == 2;

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

        static bool AnyCorner(float[] c)
        {
            if (c == null) return false;
            for (int i = 0; i < c.Length; i++) if (c[i] > 0.01f) return true;
            return false;
        }

        // Figma stroke alignment → outward-extension factor for the SDF shader:
        // 0 = inside (stroke inward), 0.5 = center (straddles edge), 1 = outside.
        static float BorderAlignFactor(string align)
            => align == "outside" ? 1f : align == "center" ? 0.5f : 0f;

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
                var shader = Shader.Find("FigForge/RoundedRect");
                if (shader == null) return;
                var so = new UnityEditor.SerializedObject(UnityEngine.Rendering.GraphicsSettings.GetGraphicsSettings());
                var arr = so.FindProperty("m_AlwaysIncludedShaders");
                if (arr == null) return;
                for (int i = 0; i < arr.arraySize; i++)
                    if (arr.GetArrayElementAtIndex(i).objectReferenceValue == shader) return;
                int idx = arr.arraySize;
                arr.InsertArrayElementAtIndex(idx);
                arr.GetArrayElementAtIndex(idx).objectReferenceValue = shader;
                so.ApplyModifiedProperties();
            }
            catch { /* best effort — editor still works via Shader.Find */ }
        }

        static void BuildSdfPanel(GameObject go, ElementData e, BuildContext ctx)
        {
            EnsureSdfShaderIncluded();
            if (go.GetComponent<CanvasRenderer>() == null) go.AddComponent<CanvasRenderer>();
            var rr = go.AddComponent<FigForgeRoundedRect>();
            rr.raycastTarget = !ctx.disableRaycasts;
            var s = e.style;
            Color border = s.stroke != null ? ToColor(s.stroke.color) : new Color(0, 0, 0, 0);
            float bw = s.stroke != null ? StrokePx(s.stroke.weight, ctx.scaleFactor) : 0f;
            float align = s.stroke != null ? BorderAlignFactor(s.stroke.align) : 0f;

            Color fill, fill2; Vector2 dir;
            if (s.fill != null && IsSdfGradient(s.fill))
            {
                fill = ToColor(s.fill.stops[0].color);
                fill2 = ToColor(s.fill.stops[1].color);
                dir = GradientDir(s.fill.transform);
            }
            else
            {
                fill = s.fill != null && s.fill.kind == "solid" ? ToColor(s.fill.color) : new Color(0, 0, 0, 0);
                fill2 = fill; dir = Vector2.zero;
            }
            rr.Configure(fill, fill2, dir, border, bw, CornerRadii(s, ctx.scaleFactor), align);
            if (s.shadows != null && s.shadows.Count > 0) ApplyShadow(rr, s.shadows[0], ctx.scaleFactor);
            ApplyOpacity(go, e, fill);
        }

        // Clip child content. A rounded element (already backed by an SDF
        // FigForgeRoundedRect) clips to its rounded corners via a stencil Mask —
        // RectMask2D can only clip to a rectangle. Flat rects keep the cheaper
        // RectMask2D.
        static void ApplyClip(GameObject go)
        {
            if (go.GetComponent<FigForgeRoundedRect>() != null)
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

        static void AddBorder(GameObject parent, ElementData e, BuildContext ctx)
        {
            var stroke = e.style.stroke;
            int thickness = Mathf.Max(1, Mathf.RoundToInt(stroke.weight * ctx.scaleFactor));
            int radius = e.style.cornerRadius > 0 ? Mathf.RoundToInt(e.style.cornerRadius * ctx.scaleFactor) : 0;

            var border = NewRect("Border", parent.transform);
            Stretch(border.GetComponent<RectTransform>());
            var img = border.AddComponent<Image>();
            img.raycastTarget = false;
            img.sprite = RoundedOutlineSpriteCache.Get(radius, thickness);
            img.type = Image.Type.Sliced;
            img.pixelsPerUnitMultiplier = 1f;
            img.color = ToColor(stroke.color);
            if (stroke.dashed) ctx.log($"stroke on '{e.name}' is dashed — rendered solid (dashed not yet supported)");
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

        static void ApplyOpacity(GameObject go, ElementData e, Color baseColor)
        {
            float o = e.style != null ? e.style.opacity : 1f;
            if (o >= 0.999f) return;
            if (e.children != null && e.children.Count > 0)
            {
                var cg = go.GetComponent<CanvasGroup>() ?? go.AddComponent<CanvasGroup>();
                cg.alpha = o;
            }
            else
            {
                var g = go.GetComponent<Graphic>();
                if (g != null) { var c = g.color; c.a *= o; g.color = c; }
            }
        }

        // -----------------------------------------------------------------------
        // A real interactive Button built from the Figma component's state sprites
        // (SpriteSwap). No prefab, no hand-wiring.
        static GameObject BuildStateButton(ElementData e, Transform parent, BuildContext ctx)
        {
            var go = NewRect(string.IsNullOrEmpty(e.name) ? "Button" : e.name, parent);
            var img = go.AddComponent<Image>();
            img.raycastTarget = true;
            var st = e.canonical.states;

            var normal = SpriteByFile(st.normal, ctx);
            if (normal != null) img.sprite = normal;
            else img.color = new Color(0.45f, 0.36f, 1f, 1f);
            // 9-slice the button background so instances scale without smearing
            // the corner radius (the state sprites were imported with a border).
            if (normal != null && normal.border.sqrMagnitude > 0.01f)
            { img.type = Image.Type.Sliced; img.pixelsPerUnitMultiplier = 1f; }

            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            var hi = SpriteByFile(st.highlighted, ctx);
            var pr = SpriteByFile(st.pressed, ctx);
            if (hi != null || pr != null)
            {
                btn.transition = Selectable.Transition.SpriteSwap;
                var ss = btn.spriteState;
                ss.highlightedSprite = hi;
                ss.selectedSprite = hi;
                ss.pressedSprite = pr;
                btn.spriteState = ss;
            }

            var labelGo = NewRect("Label", go.transform);
            Stretch(labelGo.GetComponent<RectTransform>());
            var tmp = labelGo.AddComponent<TextMeshProUGUI>();
            tmp.text = e.canonical.label ?? e.name;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            tmp.fontSize = 18f * ctx.scaleFactor;
            tmp.raycastTarget = false;
            // The prefab/definition mirrors the canonical COMPONENT's label font.
            ApplyFont(tmp, e.canonical != null ? e.canonical.defLabelFont : null, ctx);
            MatchTextWeight(tmp);
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
            if (go.GetComponent<CanvasRenderer>() == null) go.AddComponent<CanvasRenderer>(); // Graphic needs it
            var rr = go.AddComponent<FigForgeRoundedRect>();
            rr.raycastTarget = true;
            bool gradient = sh.fill2 != null;
            ApplyShapeToRR(rr, sh, ctx.scaleFactor, out var fill, out var fill2, out var dir);

            var btn = go.AddComponent<Button>();
            btn.targetGraphic = rr;
            btn.transition = Selectable.Transition.None; // fills driven by FigForgeButtonStateColors

            // Per-state full-fill swap (works for solid AND gradient buttons): the
            // normal state is the shape's fill (gradient or solid); a captured
            // hover/press colour is applied as a SOLID (fill2==fill, dir=0) so a
            // gradient button flattens to the Figma rollover/pressed colour, while
            // an uncaptured state keeps the normal fill.
            var sc = e.canonical.stateColors;
            var states = go.AddComponent<FigForgeButtonStateColors>();
            SetStates(states, new FigForgeFill(fill, fill2, dir), sc);

            var labelGo = NewRect("Label", go.transform);
            Stretch(labelGo.GetComponent<RectTransform>());
            var tmp = labelGo.AddComponent<TextMeshProUGUI>();
            tmp.text = e.canonical.label ?? e.name;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            tmp.fontSize = 18f * ctx.scaleFactor;
            tmp.raycastTarget = false;
            ApplyFont(tmp, e.canonical.defLabelFont, ctx);
            MatchTextWeight(tmp);
            return go;
        }

        // Push a CanonicalShape onto a FigForgeRoundedRect (fill/gradient/border/
        // corners/align). Outputs the resolved base fill so callers can keep
        // FigForgeButtonStateColors' normal state in sync.
        static void ApplyShapeToRR(FigForgeRoundedRect rr, CanonicalShape sh, float sf,
                                   out Color fill, out Color fill2, out Vector2 dir)
        {
            ApplyShapeValues(sh, sf, out fill, out fill2, out dir, out var border, out var borderWidth, out var corners, out var borderAlign);
            rr.Configure(fill, fill2, dir, border, borderWidth, corners, borderAlign);
            ApplyShadow(rr, sh.shadow, sf); // null/transparent → no-op
        }

        static void ApplyShapeValues(CanonicalShape sh, float sf, out Color fill, out Color fill2, out Vector2 dir,
                                     out Color border, out float borderWidth, out Vector4 corners, out float borderAlign)
        {
            fill = ToColor(sh.fill);
            bool gradient = sh.fill2 != null;
            fill2 = gradient ? ToColor(sh.fill2) : fill;
            dir = gradient ? GradientDir(sh.gradientTransform) : Vector2.zero;
            border = sh.borderColor != null ? ToColor(sh.borderColor) : new Color(0, 0, 0, 0);
            float br = sh.cornerRadius * sf;
            borderWidth = StrokePx(sh.borderWidth, sf);
            corners = new Vector4(br, br, br, br);
            borderAlign = BorderAlignFactor(sh.borderAlign);
        }

        static FigForgeShapeStyle ShapeStyle(CanonicalShape sh, BuildContext ctx)
        {
            ApplyShapeValues(sh, ctx.scaleFactor, out var fill, out var fill2, out var dir, out var border, out var borderWidth, out var corners, out var borderAlign);
            var style = new FigForgeShapeStyle
            {
                fill = new FigForgeFill(fill, fill2, dir),
                borderColor = border,
                borderWidth = borderWidth,
                corners = corners,
                borderAlign = borderAlign,
                shadowColor = new Color(0, 0, 0, 0),
                shadowOffset = Vector2.zero,
                shadowBlur = 0f,
                shadowSpread = 0f,
            };
            if (sh.shadow != null && sh.shadow.color != null)
            {
                style.shadowColor = ToColor(sh.shadow.color);
                style.shadowOffset = new Vector2(sh.shadow.offsetX * ctx.scaleFactor, -sh.shadow.offsetY * ctx.scaleFactor);
                style.shadowBlur = sh.shadow.blur * ctx.scaleFactor;
                style.shadowSpread = sh.shadow.spread * ctx.scaleFactor;
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
            var rr = inst.GetComponentInChildren<FigForgeRoundedRect>(true);
            if (rr == null) return;
            ApplyShapeToRR(rr, sh, ctx.scaleFactor, out var fill, out var fill2, out var dir);
            var states = rr.GetComponent<FigForgeButtonStateColors>();
            if (states != null)
            {
                // Re-derive ALL three states exactly like BuildShapeButton: the new base
                // fill drives 'normal', while an explicit Figma rollover/pressed colour
                // wins for hover/press. Only fall back to the base fill when the component
                // has no rollover/pressed — so a gradient instance no longer clobbers a
                // real hover/press colour with its regular gradient.
                SetStates(states, new FigForgeFill(fill, fill2, dir), sc);
            }
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
        // canonical button: swap just the highlighted/pressed fills of its
        // FigForgeButtonStateColors to THIS instance's Figma rollover/pressed colour
        // (as a SOLID — fill2==fill, dir=0 — matching BuildShapeButton, so a gradient
        // button flattens to the rollover colour on hover). The normal/base fill is
        // left as-is. Runs after ApplyInstanceShape so it wins. No-op for non-SDF
        // buttons. Creates prefab overrides on just this instance.
        static void ApplyInstanceStateColors(GameObject inst, CanonicalStateColors sc)
        {
            var states = inst.GetComponentInChildren<FigForgeButtonStateColors>(true);
            if (states == null) return;
            if (sc.highlighted != null) states.highlighted = FigForgeFill.Solid(ToColor(sc.highlighted));
            if (sc.pressed != null) states.pressed = FigForgeFill.Solid(ToColor(sc.pressed));
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

        // Background Graphic from a CanonicalShape (crisp SDF) or a transparent Image.
        // Add the CanvasRenderer up front: Toggle.isOn cross-fades graphic.canvasRenderer
        // the instant we set it, before RequireComponent would otherwise add one.
        static Graphic AddShapeGraphic(GameObject go, CanonicalShape sh, BuildContext ctx)
        {
            if (go.GetComponent<CanvasRenderer>() == null) go.AddComponent<CanvasRenderer>();
            if (sh == null) { var img = go.AddComponent<Image>(); img.color = new Color(1, 1, 1, 0); return img; }
            EnsureSdfShaderIncluded();
            var rr = go.AddComponent<FigForgeRoundedRect>();
            ApplyShapeToRR(rr, sh, ctx.scaleFactor, out _, out _, out _);
            return rr;
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
            var toggle = go.AddComponent<Toggle>();
            toggle.transition = Selectable.Transition.None;

            var bgGo = NewRect("Background", go.transform);
            AnchorPart(bgGo.GetComponent<RectTransform>(), c.parts, "Background");
            var bg = AddShapeGraphic(bgGo, c.shape, ctx);
            toggle.targetGraphic = bg;

            if (c.checkShape != null)
            {
                var ckGo = NewRect("Checkmark", go.transform);
                AnchorPart(ckGo.GetComponent<RectTransform>(), c.parts, "Checkmark");
                toggle.graphic = AddShapeGraphic(ckGo, c.checkShape, ctx);
            }

            TextMeshProUGUI label = null;
            if ((c.parts != null && c.parts.ContainsKey("Label")) || !string.IsNullOrEmpty(c.label))
            {
                label = AddControlLabel(go, "Label", c.label, c.parts, "Label", ctx, TextAlignmentOptions.MidlineLeft);
                // Keep the captured left edge but extend to the control's right edge so a
                // left-aligned label has the full remaining width (no truncation).
                var lrt = label.GetComponent<RectTransform>();
                lrt.anchorMax = new Vector2(1f, lrt.anchorMax.y);
                lrt.offsetMax = new Vector2(-6f * ctx.scaleFactor, lrt.offsetMax.y);
            }

            // Leave isOn at the default (off): the per-instance value is applied by
            // FigForgeBindings.Apply AFTER the ToggleGroup is wired, so a radio prefab
            // doesn't bake one instance's "on" and clobber its group-mates.
            toggle.isOn = false;

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
            var input = go.AddComponent<TMP_InputField>();
            input.transition = Selectable.Transition.None;
            input.lineType = TMP_InputField.LineType.SingleLine;
            input.contentType = TMP_InputField.ContentType.Standard;
            input.customCaretColor = true;
            input.caretColor = new Color(0.1f, 0.1f, 0.12f, 1f);
            input.selectionColor = new Color(0.49f, 0.36f, 1f, 0.28f);

            var bgGo = NewRect("Background", go.transform);
            AnchorPart(bgGo.GetComponent<RectTransform>(), c.parts, "Background");
            var bg = AddShapeGraphic(bgGo, c.shape, ctx);
            input.targetGraphic = bg;

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
            placeholder.alignment = TextAlignmentOptions.MidlineLeft;
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
            text.alignment = TextAlignmentOptions.MidlineLeft;
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
            var dd = go.AddComponent<TMP_Dropdown>();
            dd.transition = Selectable.Transition.None;

            var bgGo = NewRect("Background", go.transform);
            AnchorPart(bgGo.GetComponent<RectTransform>(), c.parts, "Background");
            var bg = AddShapeGraphic(bgGo, c.shape, ctx);
            dd.targetGraphic = bg;
            ApplyDropdownBackgroundStates(bg, c, ctx);

            var caption = AddControlLabel(go, "Label", c.value, c.parts, "Label", ctx, TextAlignmentOptions.MidlineLeft);
            dd.captionText = caption;
            if (c.parts != null && c.parts.ContainsKey("Arrow"))
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
            if (bg is FigForgeRoundedRect rr && c.shape != null)
            {
                ApplyShapeToRR(rr, c.shape, ctx.scaleFactor, out var fill, out var fill2, out var dir);
                var states = bg.gameObject.AddComponent<FigForgeButtonStateColors>();
                SetStates(states, new FigForgeFill(fill, fill2, dir), new CanonicalStateColors
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
                var rr = (FigForgeRoundedRect)AddShapeGraphic(itemBg, c.optionShape, ctx);
                ApplyDropdownOptionStates(item, rr, itemToggle, c, ctx);
                itemGraphic = rr;
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

        static void ApplyDropdownOptionStates(GameObject item, FigForgeRoundedRect rr, Toggle itemToggle, CanonicalRef c, BuildContext ctx)
        {
            if (rr == null || c.optionShape == null) return;
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
            states.target = rr;
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
            var bg = AddShapeGraphic(go, c.shape, ctx); // rounded container background

            var scroll = go.AddComponent<ScrollRect>();
            scroll.horizontal = false; scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Elastic; scroll.scrollSensitivity = 20f;

            var viewport = NewRect("Viewport", go.transform);
            Stretch(viewport.GetComponent<RectTransform>());
            if (viewport.GetComponent<CanvasRenderer>() == null) viewport.AddComponent<CanvasRenderer>();
            viewport.AddComponent<Image>().color = new Color(1, 1, 1, 0.004f); // near-invisible drag/clip target
            viewport.AddComponent<RectMask2D>();
            scroll.viewport = viewport.GetComponent<RectTransform>();

            var content = NewRect("Content", viewport.transform);
            var crt = content.GetComponent<RectTransform>();
            crt.anchorMin = new Vector2(0, 1); crt.anchorMax = new Vector2(1, 1); crt.pivot = new Vector2(0.5f, 1); crt.sizeDelta = Vector2.zero;
            var vlg = content.AddComponent<VerticalLayoutGroup>();
            vlg.childControlWidth = true; vlg.childControlHeight = false; vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false; vlg.spacing = 0;
            content.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scroll.content = crt;

            float rowH = (c.itemHeight > 0.01f ? c.itemHeight : 44f) * sf;
            int count = c.count > 0 ? c.count : 5;
            bool hasHover = c.itemRollover != null;
            Color hover = hasHover ? ToColor(c.itemRollover) : Color.white;
            string labelTmpl = string.IsNullOrEmpty(c.label) ? "Item" : c.label;
            var list = go.AddComponent<FigForgeList>();
            list.Configure(crt, rowH, labelTmpl, ToListRowStyle(c.itemShape, sf), hover, hasHover);
            list.CreatePreviewRows(count);

            var bind = go.AddComponent<FigForgeBindings>(); bind.background = bg;
            return go;
        }

        static FigForgeListRowStyle ToListRowStyle(CanonicalShape sh, float sf)
        {
            var style = new FigForgeListRowStyle { enabled = sh != null };
            if (sh == null) return style;
            ApplyShapeValues(sh, sf, out style.fill, out style.fill2, out style.gradientDir,
                out style.borderColor, out style.borderWidth, out style.corners, out style.borderAlign);
            if (sh.shadow != null && sh.shadow.color != null)
            {
                style.shadowColor = ToColor(sh.shadow.color);
                style.shadowOffset = new Vector2(sh.shadow.offsetX * sf, -sh.shadow.offsetY * sf);
                style.shadowBlur = sh.shadow.blur * sf;
                style.shadowSpread = sh.shadow.spread * sf;
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
            go.AddComponent<Button>();
            var label = NewRect("Label", go.transform);
            Stretch(label.GetComponent<RectTransform>());
            var tmp = label.AddComponent<TextMeshProUGUI>();
            tmp.text = e.canonical?.label ?? e.name;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            tmp.fontSize = 18f * ctx.scaleFactor;
            return go;
        }

        static void StampLabel(GameObject inst, string label)
        {
            if (string.IsNullOrEmpty(label)) return;
            var tmp = inst.GetComponentInChildren<TMP_Text>(true);
            if (tmp != null) { tmp.text = label; return; }
            var ui = inst.GetComponentInChildren<Text>(true);
            if (ui != null) ui.text = label;
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
            if (kind == "list") return null;
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
                : BuildPlaceholderButton(e, null, ctx);
            if (temp == null) return candidate; // generation failed — keep whatever we had
            temp.name = SafeAsset(refName);

            // Wire binding slots so per-instance label/value apply onto the prefab.
            var bind = temp.GetComponent<FigForgeBindings>() ?? temp.AddComponent<FigForgeBindings>();
            bind.label = temp.GetComponentInChildren<TMP_Text>(true);
            bind.control = temp.GetComponent<Selectable>();
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
        const int CanonicalSchema = 16;

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
                  .Append(";gt=").Append(SigF(sh.gradientTransform))
                  .Append(";bc=").Append(SigF(sh.borderColor)).Append(";bw=").Append(sh.borderWidth.ToString("0.###"))
                  .Append(";ba=").Append(sh.borderAlign ?? "");
            if (sh != null && sh.shadow != null)
            {
                var sd = sh.shadow;
                sb.Append(";shc=").Append(SigF(sd.color)).Append(";sho=").Append(sd.offsetX.ToString("0.###")).Append(',').Append(sd.offsetY.ToString("0.###"))
                  .Append(";shb=").Append(sd.blur.ToString("0.###")).Append(";shs=").Append(sd.spread.ToString("0.###"));
            }
            var sc = c.stateColors;
            if (sc != null)
                sb.Append(";sn=").Append(SigF(sc.normal)).Append(";sh=").Append(SigF(sc.highlighted)).Append(";sp=").Append(SigF(sc.pressed));
            if (kind == "input")
                sb.Append(";ph=").Append(c.placeholder ?? "").Append(";iv=").Append(c.value ?? "");
            if (c.checkShape != null)
            {
                var cs = c.checkShape;
                sb.Append(";ckcr=").Append(cs.cornerRadius.ToString("0.###"))
                  .Append(";ckf=").Append(SigF(cs.fill)).Append(";ckf2=").Append(SigF(cs.fill2))
                  .Append(";ckgt=").Append(SigF(cs.gradientTransform))
                  .Append(";ckbc=").Append(SigF(cs.borderColor)).Append(";ckbw=").Append(cs.borderWidth.ToString("0.###"))
                  .Append(";ckba=").Append(cs.borderAlign ?? "");
            }
            if (c.optionShape != null)
            {
                var os = c.optionShape;
                sb.Append(";oh=").Append(c.optionHeight.ToString("0.###"))
                  .Append(";ocr=").Append(os.cornerRadius.ToString("0.###"))
                  .Append(";of=").Append(SigF(os.fill)).Append(";of2=").Append(SigF(os.fill2))
                  .Append(";ogt=").Append(SigF(os.gradientTransform))
                  .Append(";obc=").Append(SigF(os.borderColor)).Append(";obw=").Append(os.borderWidth.ToString("0.###"))
                  .Append(";oba=").Append(os.borderAlign ?? "");
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
                sb.Append(";ih=").Append(c.itemHeight.ToString("0.###")).Append(";icount=").Append(c.count)
                  .Append(";icr=").Append(ish.cornerRadius.ToString("0.###"))
                  .Append(";if=").Append(SigF(ish.fill)).Append(";if2=").Append(SigF(ish.fill2))
                  .Append(";igt=").Append(SigF(ish.gradientTransform))
                  .Append(";ibc=").Append(SigF(ish.borderColor)).Append(";ibw=").Append(ish.borderWidth.ToString("0.###"))
                  .Append(";iba=").Append(ish.borderAlign ?? "");
            }
            sb.Append(";iro=").Append(SigF(c.itemRollover));
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
            bool hasStates = c.states != null && (c.states.normal != null || c.states.highlighted != null || c.states.pressed != null);
            sb.Append(";hasShape=").Append(sh != null ? 1 : 0).Append(";hasStates=").Append(hasStates ? 1 : 0);
            if (c.defLabelFont != null)
                sb.Append(";lf=").Append(c.defLabelFont.family ?? "").Append('/').Append(c.defLabelFont.style ?? "");
            return sb.ToString();
        }

        static void AppendShapeSig(System.Text.StringBuilder sb, string prefix, CanonicalShape sh)
        {
            if (sh == null) return;
            sb.Append(';').Append(prefix).Append("cr=").Append(sh.cornerRadius.ToString("0.###"))
              .Append(';').Append(prefix).Append("f=").Append(SigF(sh.fill))
              .Append(';').Append(prefix).Append("f2=").Append(SigF(sh.fill2))
              .Append(';').Append(prefix).Append("gt=").Append(SigF(sh.gradientTransform))
              .Append(';').Append(prefix).Append("bc=").Append(SigF(sh.borderColor))
              .Append(';').Append(prefix).Append("bw=").Append(sh.borderWidth.ToString("0.###"))
              .Append(';').Append(prefix).Append("ba=").Append(sh.borderAlign ?? "");
            if (sh.shadow != null)
            {
                sb.Append(';').Append(prefix).Append("sh=").Append(SigF(sh.shadow.color)).Append(',')
                  .Append(sh.shadow.offsetX.ToString("0.###")).Append(',')
                  .Append(sh.shadow.offsetY.ToString("0.###")).Append(',')
                  .Append(sh.shadow.blur.ToString("0.###")).Append(',')
                  .Append(sh.shadow.spread.ToString("0.###"));
            }
        }

        static bool CanShareCanonicalBySignature(ElementData e, string kind)
        {
            return e.canonical != null;
        }

        static string SigF(float[] a)
            => a == null ? "_" : string.Join(",", System.Array.ConvertAll(a, x => x.ToString("0.###")));

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
