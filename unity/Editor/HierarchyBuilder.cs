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
                var prefab = ResolveOrGenerateCanonicalPrefab(e, ctx);
                GameObject inst;
                if (prefab != null)
                {
                    inst = (GameObject)UnityEditor.PrefabUtility.InstantiatePrefab(prefab, parent);
                    inst.name = e.name;
                }
                else if (e.canonical.kind == "button" && e.canonical.shape != null)
                {
                    inst = BuildShapeButton(e, parent, ctx); // fallback: inline SDF shader
                }
                else if (e.canonical.kind == "button" && e.canonical.states != null)
                {
                    inst = BuildStateButton(e, parent, ctx); // fallback: inline state PNGs
                }
                else
                {
                    ctx.log($"canonical {e.canonical.kind} '{e.canonical.Ref}' → placeholder");
                    inst = BuildPlaceholderButton(e, parent, ctx);
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
            return rounded || border;
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
            float bw = s.stroke != null ? Mathf.Max(1f, s.stroke.weight * ctx.scaleFactor) : 0f;
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
            float w = Mathf.Clamp(t.outline.weight / Mathf.Max(1f, t.fontSize) * 1.4f, 0f, 0.5f);
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
            var fill = ToColor(sh.fill);
            bool gradient = sh.fill2 != null;
            Color fill2 = gradient ? ToColor(sh.fill2) : fill;
            Vector2 dir = gradient ? GradientDir(sh.gradientTransform) : Vector2.zero;
            var border = sh.borderColor != null ? ToColor(sh.borderColor) : new Color(0, 0, 0, 0);
            float br = sh.cornerRadius * ctx.scaleFactor;
            rr.Configure(fill, fill2, dir, border, sh.borderWidth * ctx.scaleFactor, new Vector4(br, br, br, br), BorderAlignFactor(sh.borderAlign));

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
            states.normal = fill;        states.normal2 = fill2;      states.normalDir = dir;
            if (sc != null && sc.highlighted != null)
            { var c = ToColor(sc.highlighted); states.highlighted = c; states.highlighted2 = c; states.highlightedDir = Vector2.zero; }
            else
            { states.highlighted = fill; states.highlighted2 = fill2; states.highlightedDir = dir; }
            if (sc != null && sc.pressed != null)
            { var c = ToColor(sc.pressed); states.pressed = c; states.pressed2 = c; states.pressedDir = Vector2.zero; }
            else
            { states.pressed = fill; states.pressed2 = fill2; states.pressedDir = dir; }

            var labelGo = NewRect("Label", go.transform);
            Stretch(labelGo.GetComponent<RectTransform>());
            var tmp = labelGo.AddComponent<TextMeshProUGUI>();
            tmp.text = e.canonical.label ?? e.name;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            tmp.fontSize = 18f * ctx.scaleFactor;
            tmp.raycastTarget = false;
            ApplyFont(tmp, e.canonical.defLabelFont, ctx);
            return go;
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
            if (string.IsNullOrEmpty(refName)) return null;

            // 1. Already mapped in the library (hand-made or previously generated).
            if (ctx.canonical != null)
            {
                var mapped = ctx.canonical.Resolve(kind, refName);
                if (mapped != null) return mapped;
            }

            // 2. A generated prefab already exists on disk — reuse, don't clobber.
            string path = $"{CanonicalFolder}/{SafeAsset(refName)}.prefab";
            var existing = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (existing != null) { RegisterInLibrary(ctx, kind, refName, existing); return existing; }

            // 3. Generate from the canonical definition, save, register.
            GameObject temp = (kind == "button" && e.canonical.shape != null)
                ? BuildShapeButton(e, null, ctx)                          // crisp SDF shader (preferred)
                : (kind == "button" && e.canonical.states != null)
                    ? BuildStateButton(e, null, ctx)                      // exported state PNGs
                    : BuildPlaceholderButton(e, null, ctx);
            if (temp == null) return null;
            temp.name = SafeAsset(refName);

            // Wire binding slots so per-instance label/value apply onto the prefab.
            var bind = temp.GetComponent<FigForgeBindings>() ?? temp.AddComponent<FigForgeBindings>();
            bind.label = temp.GetComponentInChildren<TMP_Text>(true);
            bind.control = temp.GetComponent<Selectable>();

            TextureImportHelper.EnsureFolder(CanonicalFolder);
            var prefab = UnityEditor.PrefabUtility.SaveAsPrefabAsset(temp, path);
            UnityEngine.Object.DestroyImmediate(temp);
            if (prefab != null)
            {
                ctx.log($"generated canonical {kind} '{refName}' → {path}");
                RegisterInLibrary(ctx, kind, refName, prefab);
            }
            return prefab;
        }

        static void RegisterInLibrary(BuildContext ctx, string kind, string refName, GameObject prefab)
        {
            var lib = ctx.canonical ?? LoadOrCreateCanonicalLibrary();
            ctx.canonical = lib; // reuse for the rest of this build
            if (!CanonicalLibrary.TryParseKind(kind, out var k)) return;
            var entry = lib.entries.Find(en => en != null && en.kind == k && en.referenceName == refName);
            if (entry == null) { entry = new CanonicalLibrary.Entry { kind = k, referenceName = refName }; lib.entries.Add(entry); }
            if (entry.prefab == null) entry.prefab = prefab;
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
