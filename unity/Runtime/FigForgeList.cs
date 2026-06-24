// =============================================================================
// FigForge — runtime data binding for generated List controls. The importer
// builds design-time preview rows, then runtime code can replace them with real
// data via SetItems.
// =============================================================================

using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace FigForge
{
    /// <summary>Fired with the newly-selected row index when a List/Table selection
    /// changes (single-select). Serializable so it shows in the Inspector.</summary>
    [System.Serializable]
    public class FigForgeSelectionEvent : UnityEvent<int> { }

    /// <summary>One row's data in a FigForgeList — a two-line row (Title + optional
    /// Subtitle). The list's analogue of a dropdown option string.</summary>
    [System.Serializable]
    public struct FigForgeListItem
    {
        public string title;
        public string subtitle;
        public FigForgeListItem(string title, string subtitle = null) { this.title = title; this.subtitle = subtitle; }
        public override string ToString() => title;
    }

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

        // Container corner radii px (tl, tr, br, bl), already canvas-scaled and inset to the
        // mask box. The FIRST row inherits the top pair and the LAST row the bottom pair, so
        // square row fills can't paint over the rounded container corners (the importer
        // zeroes the top pair when a Header strip covers the top corners).
        public Vector4 containerCorners = Vector4.zero;

        // Rich-row mode: a cloned template (the captured Item subtree) + per-state row fills.
        public GameObject rowTemplate;
        public FigForgeFill rowRegular = FigForgeFill.Solid(Color.white);
        public FigForgeFill rowRollover = FigForgeFill.Solid(Color.white);
        public FigForgeFill rowPressed = FigForgeFill.Solid(Color.white);
        public FigForgeFill rowSelected = FigForgeFill.Solid(Color.white);
        public bool rowHasRollover, rowHasPressed, rowHasSelected;

        [Tooltip("Fired with the new row index whenever the selected row changes.")]
        public FigForgeSelectionEvent onSelectionChanged = new FigForgeSelectionEvent();
        int _selected = -1;

        readonly List<FigForgeListItem> _items = new List<FigForgeListItem>();

        /// <summary>The list's items. Normally the data model you set via SetItems/AddItem;
        /// if that's empty but the list is showing design-time preview rows, returns those
        /// rows' visible content instead — so Items always matches what's on screen (and
        /// Items.Count == ItemCount). Once you SetItems, it's the data model verbatim.</summary>
        public IReadOnlyList<FigForgeListItem> Items
            => (_items.Count > 0 || content == null) ? _items : ReadRenderedItems();

        // Scrape every rendered row into items — the preview-row fallback for Items, mirroring
        // GetItem's per-row fallback. Only hit when the data model is empty but rows render.
        IReadOnlyList<FigForgeListItem> ReadRenderedItems()
        {
            int n = content.childCount;
            var items = new List<FigForgeListItem>(n);
            for (int i = 0; i < n; i++)
                items.Add(ReadRenderedItem(i) ?? default);
            return items;
        }

        /// <summary>Show/hide the whole control — `list.isVisible = false`. Drives
        /// GameObject.SetActive, so a hidden control stops rendering, receiving input,
        /// and contributing to layout.</summary>
        public bool isVisible
        {
            get => gameObject.activeSelf;
            set => gameObject.SetActive(value);
        }

        // Set the rows from structured items (title + subtitle).
        public void SetItems(IList<FigForgeListItem> items)
        {
            _items.Clear();
            if (items != null) _items.AddRange(items);
            Rebuild();
        }

        public void SetItems(IEnumerable<FigForgeListItem> items)
        {
            _items.Clear();
            if (items != null) _items.AddRange(items);
            Rebuild();
        }

        // Title-only convenience overloads (subtitle blank).
        public void SetItems(IList<string> titles)
        {
            _items.Clear();
            if (titles != null)
                for (int i = 0; i < titles.Count; i++)
                    _items.Add(new FigForgeListItem(titles[i] ?? ""));
            Rebuild();
        }

        public void SetItems(IEnumerable<string> titles)
        {
            _items.Clear();
            if (titles != null)
                foreach (var t in titles)
                    _items.Add(new FigForgeListItem(t ?? ""));
            Rebuild();
        }

        public void SetItems(IEnumerable titles)
        {
            _items.Clear();
            if (titles != null)
                foreach (var t in titles)
                    _items.Add(new FigForgeListItem(t != null ? t.ToString() : ""));
            Rebuild();
        }

        // --- Granular item accessors ------------------------------------------
        // List-shaped counterpart to the Table's cell accessors: a List row is one
        // FigForgeListItem (Title + optional Subtitle), not a cell grid. Reads address the
        // data model set via SetItems/AddItem/…, and fall back to a row's visible text when
        // it's rendered but has no backing data (design-time preview rows), so a validly-
        // selected row never returns null. Single-item writes patch the rendered text IN
        // PLACE (no Rebuild, so scroll + selection survive); Add/Insert/Remove Rebuild.

        /// <summary>Number of rows the list currently shows — the range Select accepts and
        /// GetItem addresses. This is the rendered row count (so design-time preview rows
        /// count too); it equals the data-model size once you SetItems/AddItem.</summary>
        public int ItemCount => content != null ? content.childCount : _items.Count;

        /// <summary>The currently-selected item, or null when nothing is selected. Handy in
        /// an onSelectionChanged handler: `var item = list.SelectedItem;`.</summary>
        public FigForgeListItem? SelectedItem => GetItem(_selected);

        /// <summary>Read one item, or null when `index` is out of range. Reads the data model
        /// (SetItems/AddItem); for a rendered row with no backing data — preview rows — it
        /// falls back to the row's visible Title/Subtitle text.</summary>
        public FigForgeListItem? GetItem(int index)
        {
            if (index < 0) return null;
            if (index < _items.Count) return _items[index];
            return ReadRenderedItem(index); // preview rows: scrape what's on screen
        }

        /// <summary>Read an item's title — "" if blank, null if `index` is out of range.</summary>
        public string GetTitle(int index)
        {
            var it = GetItem(index);
            return it.HasValue ? (it.Value.title ?? "") : null;
        }

        /// <summary>Read an item's subtitle — "" if blank, null if `index` is out of range.</summary>
        public string GetSubtitle(int index)
        {
            var it = GetItem(index);
            return it.HasValue ? (it.Value.subtitle ?? "") : null;
        }

        // Reconstruct an item from the rendered Title/Subtitle (or styled-row Label) text.
        // Used when the data model doesn't cover `index` (preview rows render but never
        // populate _items). Null when the row isn't rendered either.
        FigForgeListItem? ReadRenderedItem(int index)
        {
            if (content == null || index >= content.childCount) return null;
            var titleT = GetTitleText(index);
            var subT = GetSubtitleText(index);
            return new FigForgeListItem(titleT != null ? titleT.text : "", subT != null ? subT.text : null);
        }

        /// <summary>Replace one item, re-rendering its Title (and Subtitle, on captured rows
        /// that have one) in place — no Rebuild. No-op if `index` is out of range.</summary>
        public void SetItem(int index, FigForgeListItem item)
        {
            if (index < 0 || index >= _items.Count) return;
            _items[index] = item;
            var titleT = GetTitleText(index);
            if (titleT != null) titleT.text = item.title ?? "";
            var subT = GetSubtitleText(index);
            if (subT != null) subT.text = item.subtitle ?? "";
        }

        /// <summary>Replace one item by title (+ optional subtitle) — convenience overload.</summary>
        public void SetItem(int index, string title, string subtitle = null)
            => SetItem(index, new FigForgeListItem(title, subtitle));

        /// <summary>The live Title TMP for a rendered row — use to restyle (colour, font).
        /// Null if the row isn't rendered. Falls back to the styled-row "Label". For text
        /// changes prefer SetItem (keeps the model in sync).</summary>
        public TMP_Text GetTitleText(int index)
        {
            if (content == null || index < 0 || index >= content.childCount) return null;
            var row = content.GetChild(index);
            // Captured rows name it "Title"; the styled fallback renders the title in "Label".
            var t = FindByName(row, "Title");
            if (t == null) t = FindByName(row, "Label");
            return t != null ? t.GetComponent<TMP_Text>() : null;
        }

        /// <summary>The live Subtitle TMP for a rendered row — null if the row isn't rendered
        /// or has no Subtitle (the styled fallback renders title only).</summary>
        public TMP_Text GetSubtitleText(int index)
        {
            if (content == null || index < 0 || index >= content.childCount) return null;
            var t = FindByName(content.GetChild(index), "Subtitle");
            return t != null ? t.GetComponent<TMP_Text>() : null;
        }

        /// <summary>The live FigForgeListRow for a rendered row (state fills, selection,
        /// GameObject), or null if not rendered.</summary>
        public FigForgeListRow GetRowObject(int index)
        {
            if (content == null || index < 0 || index >= content.childCount) return null;
            return content.GetChild(index).GetComponent<FigForgeListRow>();
        }

        /// <summary>Append an item. Row count changed, so the list Rebuilds.</summary>
        public void AddItem(FigForgeListItem item) { _items.Add(item); Rebuild(); }

        /// <summary>Append a title (+ optional subtitle).</summary>
        public void AddItem(string title, string subtitle = null) { _items.Add(new FigForgeListItem(title, subtitle)); Rebuild(); }

        /// <summary>Insert an item at `index` (clamped to 0..ItemCount). Rebuilds. Note this
        /// shifts later indices — a held SelectedIndex now points at a different row.</summary>
        public void InsertItem(int index, FigForgeListItem item)
        {
            index = Mathf.Clamp(index, 0, _items.Count);
            _items.Insert(index, item);
            Rebuild();
        }

        /// <summary>Remove the item at `index`. No-op if out of range. Rebuilds, and fixes up
        /// the selection (clears it if the removed row was selected, else shifts it down).</summary>
        public void RemoveItem(int index)
        {
            if (index < 0 || index >= _items.Count) return;
            _items.RemoveAt(index);
            if (_selected == index) _selected = -1;
            else if (_selected > index) _selected--;
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

        // Play-mode safety net: scenes imported before the childControlHeight fix never
        // call Rebuild when runtime code doesn't SetItems, leaving their serialized rows
        // zero-height (invisible). Healing on enable makes them lay out correctly.
        void OnEnable()
        {
            if (content != null) HealContentLayout();
        }

        public void Rebuild()
        {
            if (content == null) return;
            HealContentLayout();
            ClearRows();
            for (int i = 0; i < _items.Count; i++)
                CreateRow(i, _items[i], _items.Count);
            // Re-apply the stored selection to the freshly-built rows: without this
            // SelectedIndex keeps its old value but no row is highlighted. Clamp first —
            // a shrunk list may no longer contain the old index, in which case clear it.
            if (_selected >= _items.Count) _selected = -1;
            ApplySelectionVisual(_selected);
        }

        // Rows size themselves via LayoutElement preferredHeight, which a layout group only
        // honours on a CONTROLLED axis. Scenes imported before the fix have a content
        // VerticalLayoutGroup with childControlHeight=false — there a stretch-anchored
        // template clone (sizeDelta.y = 0) lays out zero-height and the viewport mask hides
        // every row. Repair the flag here so SetItems works without a re-import.
        void HealContentLayout()
        {
            var vlg = content.GetComponent<VerticalLayoutGroup>();
            if (vlg != null && !vlg.childControlHeight) vlg.childControlHeight = true;
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
                CreateRow(i, new FigForgeListItem(labelPrefix + " " + (i + 1)), count);
        }

        // Single-select: mark `index` selected, clear the rest. Fires
        // onSelectionChanged only when the selection actually moves.
        public void Select(int index)
        {
            // Reject out-of-range indices: only -1 (clear) plus 0..rowCount-1 are valid.
            // An out-of-range value would set a phantom selection and fire the event for
            // a row that doesn't exist.
            int count = content != null ? content.childCount : 0;
            if (index < -1 || index >= count) return;

            bool changed = _selected != index;
            _selected = index;
            ApplySelectionVisual(index);
            if (changed) onSelectionChanged.Invoke(index);
        }

        public int SelectedIndex => _selected;

        // Highlight the row whose index matches `index`, clear the rest. Visual only —
        // callers fire onSelectionChanged themselves when the selection actually moves.
        void ApplySelectionVisual(int index)
        {
            if (content == null) return;
            for (int i = 0; i < content.childCount; i++)
            {
                var r = content.GetChild(i).GetComponent<FigForgeListRow>();
                if (r != null) r.SetSelected(r.index == index);
            }
        }

        void CreateRow(int index, FigForgeListItem item, int count)
        {
            if (rowTemplate != null) { CreateTemplateRow(index, item, count); return; }
            CreateStyledRow(index, item, count);
        }

        // First row takes the container's top corner radii, last row the bottom pair —
        // so square row fills follow the rounded container instead of painting over it.
        void ApplyRowCorners(Graphic g, int index, int count)
        {
            if (g == null) return;
            var cc = new Vector4(
                index == 0 ? containerCorners.x : 0f,
                index == 0 ? containerCorners.y : 0f,
                index == count - 1 ? containerCorners.z : 0f,
                index == count - 1 ? containerCorners.w : 0f);
            if (g is FigForgeLayeredRect lr) lr.SetCorners(cc);
            else if (g is FigForgeRoundedRect rr) rr.SetCorners(cc);
        }

        // Rich row: clone the captured Item subtree, bind Title + Subtitle, wire state
        // colours + single-select via FigForgeListRow, re-enable the HitArea click target.
        void CreateTemplateRow(int index, FigForgeListItem item, int count)
        {
            var row = Instantiate(rowTemplate, content);
            row.name = "Item " + (index + 1);
            row.SetActive(true);
            var le = row.GetComponent<LayoutElement>();
            if (le == null) le = row.AddComponent<LayoutElement>(); // ?? misreads Unity fake-null
            le.minHeight = rowHeight; le.preferredHeight = rowHeight;

            var titleT = FindByName(row.transform, "Title");
            if (titleT != null) { var tmp = titleT.GetComponent<TMP_Text>(); if (tmp != null) tmp.text = item.title ?? ""; }
            var subT = FindByName(row.transform, "Subtitle");
            if (subT != null) { var tmp = subT.GetComponent<TMP_Text>(); if (tmp != null) tmp.text = item.subtitle ?? ""; }

            // Pointer events need a raycastable graphic on the row: the cloned subtree is
            // built render-only (raycasts stripped). Re-enable the HitArea when the design
            // has one; otherwise add a transparent full-bleed Image as the hit surface.
            var hitT = FindByName(row.transform, "HitArea");
            var hit = hitT != null ? hitT.GetComponent<Graphic>() : null;
            if (hit != null) hit.raycastTarget = true;
            else
            {
                var img = row.GetComponent<Image>();
                if (img == null) img = row.AddComponent<Image>(); // ?? misreads Unity fake-null
                img.color = new Color(0, 0, 0, 0);
                img.raycastTarget = true;
            }

            var bgT = FindByName(row.transform, "Regular");
            var bg = bgT != null ? bgT.GetComponent<Graphic>() : null;

            var fr = row.AddComponent<FigForgeListRow>();
            fr.owner = this; fr.index = index;
            fr.regular = rowRegular; fr.rollover = rowRollover; fr.pressed = rowPressed; fr.selected = rowSelected;
            fr.hasRollover = rowHasRollover; fr.hasPressed = rowHasPressed; fr.hasSelected = rowHasSelected;
            if (bg != null) fr.Bind(bg);
            ApplyRowCorners(bg, index, count);
        }

        // Case-insensitive: the exporter sanitizes captured subtree names to lowercase
        // ("Title" → "title"), so an exact match against the design-side casing never hits.
        static Transform FindByName(Transform t, string name)
        {
            foreach (var rt in t.GetComponentsInChildren<Transform>(true))
                if (string.Equals(rt.name, name, System.StringComparison.OrdinalIgnoreCase)) return rt;
            return null;
        }

        void CreateStyledRow(int index, FigForgeListItem item, int count)
        {
            var row = NewRect("Item " + (index + 1), content);
            var le = row.AddComponent<LayoutElement>();
            le.minHeight = rowHeight;
            le.preferredHeight = rowHeight;

            var btn = row.AddComponent<Button>();
            btn.transition = Selectable.Transition.None;

            Graphic rowBg;
            FigForgeFill rowRegularFill;
            if (itemStyle != null && itemStyle.enabled)
            {
                if (row.GetComponent<CanvasRenderer>() == null) row.AddComponent<CanvasRenderer>();
                var rr = row.AddComponent<FigForgeLayeredRect>();
                ApplyStyleToLayeredRect(rr, itemStyle);
                rowBg = rr;
                rowRegularFill = itemStyle.fill;
            }
            else
            {
                var img = row.AddComponent<Image>();
                img.color = new Color(1, 1, 1, 0);
                rowBg = img;
                rowRegularFill = FigForgeFill.Solid(new Color(1, 1, 1, 0));
            }
            btn.targetGraphic = rowBg;
            ApplyRowCorners(rowBg, index, count);

            // Wire selection so styled rows behave like template rows: a FigForgeListRow
            // bound to the same background recolours per state AND lets ApplySelectionVisual
            // paint the highlight, with its OnPointerClick driving single-select. This row
            // component now owns ALL states (it replaces FigForgeButtonStateColors here),
            // so there's a single writer to the graphic — rollover/pressed/selected all use
            // the rollover colour, matching the previous flat/styled visual output.
            var rolloverFill = hasItemRollover ? FigForgeFill.Solid(itemRollover) : rowRegularFill;
            var fr = row.AddComponent<FigForgeListRow>();
            fr.owner = this; fr.index = index;
            fr.regular = rowRegularFill;
            fr.rollover = rolloverFill; fr.pressed = rolloverFill; fr.selected = rolloverFill;
            fr.hasRollover = hasItemRollover; fr.hasPressed = hasItemRollover; fr.hasSelected = hasItemRollover;
            fr.Bind(rowBg);

            var lblGo = NewRect("Label", row.transform);
            var lrt = lblGo.GetComponent<RectTransform>();
            lrt.anchorMin = Vector2.zero;
            lrt.anchorMax = Vector2.one;
            lrt.offsetMin = new Vector2(16f, 0);
            lrt.offsetMax = new Vector2(-12f, 0);
            var tmp = lblGo.AddComponent<TextMeshProUGUI>();
            tmp.text = item.title ?? "";
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
