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
            // ---- Canonical element: instantiate a library prefab instead -------
            if (e.canonical != null)
            {
                var prefab = ctx.canonical != null ? ctx.canonical.Resolve(e.canonical.kind, e.canonical.Ref) : null;
                GameObject inst;
                if (prefab != null)
                {
                    // Optional override: a hand-made prefab in the CanonicalLibrary.
                    inst = (GameObject)UnityEditor.PrefabUtility.InstantiatePrefab(prefab, parent);
                    inst.name = e.name;
                }
                else if (e.canonical.kind == "button" && e.canonical.states != null)
                {
                    // Built entirely from the Figma component's state layers.
                    inst = BuildStateButton(e, parent, ctx);
                }
                else
                {
                    ctx.log($"canonical {e.canonical.kind} '{e.canonical.Ref}' has no states or library prefab → placeholder");
                    inst = BuildPlaceholderButton(e, parent, ctx);
                }
                ApplyTransform(inst.GetComponent<RectTransform>() ?? inst.AddComponent<RectTransform>(), e, ctx);

                // Fill the prefab's binding slots (label/value/options); else stamp label.
                var bindings = inst.GetComponentInChildren<FigForgeBindings>(true);
                if (bindings != null) bindings.Apply(e.canonical.label, e.canonical.value, e.canonical.options);
                else StampLabel(inst, e.canonical.label);

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

            if (e.clipsContent) go.AddComponent<RectMask2D>();
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
            var t = e.transform;
            float sf = ctx.scaleFactor;
            rt.anchorMin = V(t.anchorMin, 0.5f);
            rt.anchorMax = V(t.anchorMax, 0.5f);
            rt.pivot = V(t.pivot, 0.5f);
            rt.offsetMin = V(t.offsetMin, 0f) * sf;
            rt.offsetMax = V(t.offsetMax, 0f) * sf;
            rt.localScale = Vector3.one;
            if (Mathf.Abs(t.rotationZ) > 0.001f)
                rt.localEulerAngles = new Vector3(0, 0, t.rotationZ);
        }

        static void ApplyVisual(GameObject go, ElementData e, BuildContext ctx, bool hasAsset)
        {
            var style = e.style;
            bool needGraphic = hasAsset || (style != null && (style.fill != null || style.stroke != null));
            if (!needGraphic) return;

            var img = go.AddComponent<Image>();
            img.raycastTarget = !ctx.disableRaycasts;

            if (hasAsset)
            {
                img.sprite = ctx.sprites[e.asset];
                if (e.nineSlice != null) { img.type = Image.Type.Sliced; img.pixelsPerUnitMultiplier = 1f; }
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

            tmp.alignment = MapAlign(t.alignH, t.alignV);
            if (t.letterSpacing.HasValue) tmp.characterSpacing = t.letterSpacing.Value;
            tmp.enableWordWrapping = true;
            ApplyOpacity(go, e, tmp.color);
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
            var font = ctx.resolveFont?.Invoke("Inter", "Regular");
            if (font != null) tmp.font = font;
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
