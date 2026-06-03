// =============================================================================
// FigForge — runtime data binding for generated List controls. The importer
// builds design-time preview rows, then runtime code can replace them with real
// data via SetItems.
// =============================================================================

using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FigForge
{
    [System.Serializable]
    public class FigForgeListRowStyle
    {
        public bool enabled;
        public FigForgeFill fill = FigForgeFill.Solid(Color.white);
        public FigForgeStroke stroke = FigForgeStroke.None;
        public Vector4 corners = Vector4.zero;
        public Color shadowColor = new Color(0, 0, 0, 0);
        public Vector2 shadowOffset = Vector2.zero;
        public float shadowBlur;
        public float shadowSpread;
    }

    [DisallowMultipleComponent]
    [AddComponentMenu("FigForge/List")]
    public class FigForgeList : MonoBehaviour
    {
        public RectTransform content;
        public float rowHeight = 44f;
        public string labelPrefix = "Item";
        public FigForgeListRowStyle itemStyle = new FigForgeListRowStyle();
        public Color itemRollover = Color.white;
        public bool hasItemRollover;

        // Rich-row mode: a cloned template (the captured Item subtree) + per-state row fills.
        public GameObject rowTemplate;
        public FigForgeFill rowRegular = FigForgeFill.Solid(Color.white);
        public FigForgeFill rowRollover = FigForgeFill.Solid(Color.white);
        public FigForgeFill rowPressed = FigForgeFill.Solid(Color.white);
        public FigForgeFill rowSelected = FigForgeFill.Solid(Color.white);
        public bool rowHasRollover, rowHasPressed, rowHasSelected;
        int _selected = -1;

        readonly List<string> _items = new List<string>();

        public IReadOnlyList<string> Items => _items;

        public void SetItems(IList<string> items)
        {
            _items.Clear();
            if (items != null)
                for (int i = 0; i < items.Count; i++)
                    _items.Add(items[i] ?? "");
            Rebuild();
        }

        public void SetItems(IEnumerable<string> items)
        {
            _items.Clear();
            if (items != null)
                foreach (var item in items)
                    _items.Add(item ?? "");
            Rebuild();
        }

        public void SetItems(IEnumerable items)
        {
            _items.Clear();
            if (items != null)
                foreach (var item in items)
                    _items.Add(item != null ? item.ToString() : "");
            Rebuild();
        }

        public void Configure(RectTransform contentRoot, float height, string label, FigForgeListRowStyle style, Color rollover, bool hasRollover)
        {
            content = contentRoot;
            rowHeight = Mathf.Max(1f, height);
            labelPrefix = string.IsNullOrEmpty(label) ? "Item" : label;
            itemStyle = style ?? new FigForgeListRowStyle();
            itemRollover = rollover;
            hasItemRollover = hasRollover;
        }

        public void Rebuild()
        {
            if (content == null) return;
            ClearRows();
            for (int i = 0; i < _items.Count; i++)
                CreateRow(i, _items[i]);
        }

        public void ClearRows()
        {
            if (content == null) return;
            for (int i = content.childCount - 1; i >= 0; i--)
            {
                var child = content.GetChild(i);
                if (Application.isPlaying) Destroy(child.gameObject);
                else DestroyImmediate(child.gameObject);
            }
        }

        public void CreatePreviewRows(int count)
        {
            if (content == null) return;
            ClearRows();
            count = Mathf.Max(0, count);
            for (int i = 0; i < count; i++)
                CreateRow(i, labelPrefix + " " + (i + 1));
        }

        // Single-select: mark `index` selected, clear the rest.
        public void Select(int index)
        {
            _selected = index;
            if (content == null) return;
            for (int i = 0; i < content.childCount; i++)
            {
                var r = content.GetChild(i).GetComponent<FigForgeListRow>();
                if (r != null) r.SetSelected(r.index == index);
            }
        }

        public int SelectedIndex => _selected;

        void CreateRow(int index, string label)
        {
            if (rowTemplate != null) { CreateTemplateRow(index, label); return; }
            CreateStyledRow(index, label);
        }

        // Rich row: clone the captured Item subtree, bind the Title, wire state colours +
        // single-select via FigForgeListRow, and re-enable the HitArea as the click target.
        void CreateTemplateRow(int index, string label)
        {
            var row = Instantiate(rowTemplate, content);
            row.name = "Item " + (index + 1);
            row.SetActive(true);
            var le = row.GetComponent<LayoutElement>() ?? row.AddComponent<LayoutElement>();
            le.minHeight = rowHeight; le.preferredHeight = rowHeight;

            var titleT = FindByName(row.transform, "Title");
            if (titleT != null) { var tmp = titleT.GetComponent<TMP_Text>(); if (tmp != null) tmp.text = label ?? ""; }

            var hitT = FindByName(row.transform, "HitArea");
            if (hitT != null) { var hg = hitT.GetComponent<Graphic>(); if (hg != null) hg.raycastTarget = true; }

            var bgT = FindByName(row.transform, "Regular");
            var bg = bgT != null ? bgT.GetComponent<Graphic>() : null;

            var fr = row.AddComponent<FigForgeListRow>();
            fr.owner = this; fr.index = index;
            fr.regular = rowRegular; fr.rollover = rowRollover; fr.pressed = rowPressed; fr.selected = rowSelected;
            fr.hasRollover = rowHasRollover; fr.hasPressed = rowHasPressed; fr.hasSelected = rowHasSelected;
            if (bg != null) fr.Bind(bg);
        }

        static Transform FindByName(Transform t, string name)
        {
            foreach (var rt in t.GetComponentsInChildren<Transform>(true)) if (rt.name == name) return rt;
            return null;
        }

        void CreateStyledRow(int index, string label)
        {
            var row = NewRect("Item " + (index + 1), content);
            var le = row.AddComponent<LayoutElement>();
            le.minHeight = rowHeight;
            le.preferredHeight = rowHeight;

            var btn = row.AddComponent<Button>();
            btn.transition = Selectable.Transition.None;

            Graphic rowBg;
            if (itemStyle != null && itemStyle.enabled)
            {
                if (row.GetComponent<CanvasRenderer>() == null) row.AddComponent<CanvasRenderer>();
                var rr = row.AddComponent<FigForgeLayeredRect>();
                ApplyStyleToLayeredRect(rr, itemStyle);
                var states = row.AddComponent<FigForgeButtonStateColors>();
                states.normal = itemStyle.fill;
                states.highlighted = hasItemRollover ? FigForgeFill.Solid(itemRollover) : itemStyle.fill;
                states.pressed = hasItemRollover ? FigForgeFill.Solid(itemRollover) : itemStyle.fill;
                rowBg = rr;
            }
            else
            {
                var img = row.AddComponent<Image>();
                img.color = new Color(1, 1, 1, 0);
                rowBg = img;
            }
            btn.targetGraphic = rowBg;

            var lblGo = NewRect("Label", row.transform);
            var lrt = lblGo.GetComponent<RectTransform>();
            lrt.anchorMin = Vector2.zero;
            lrt.anchorMax = Vector2.one;
            lrt.offsetMin = new Vector2(16f, 0);
            lrt.offsetMax = new Vector2(-12f, 0);
            var tmp = lblGo.AddComponent<TextMeshProUGUI>();
            tmp.text = label ?? "";
            tmp.alignment = TextAlignmentOptions.MidlineLeft;
            tmp.color = new Color(0.1f, 0.1f, 0.12f);
            tmp.fontSize = 14f;
            tmp.raycastTarget = false;
        }

        static GameObject NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }

        static void ApplyStyleToRR(FigForgeRoundedRect rr, FigForgeListRowStyle style)
        {
            rr.Configure(style.fill, style.stroke, style.corners);
            if (style.shadowColor.a > 0.001f)
                rr.SetShadow(style.shadowColor, style.shadowOffset, style.shadowBlur, style.shadowSpread);
        }

        static void ApplyStyleToLayeredRect(FigForgeLayeredRect rr, FigForgeListRowStyle style)
        {
            rr.SetStyle(new FigForgeShapeStyle
            {
                fill = style.fill,
                stroke = style.stroke,
                corners = style.corners,
                shadowColor = style.shadowColor,
                shadowOffset = style.shadowOffset,
                shadowBlur = style.shadowBlur,
                shadowSpread = style.shadowSpread,
            });
        }
    }
}
