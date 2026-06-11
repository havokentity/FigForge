// =============================================================================
// FigForge — runtime data binding for generated Table controls. The importer
// builds design-time preview rows, then runtime code can replace them with real
// data via SetRows. A table is the List's grid sibling: each row is m text
// cells (Cell1..CellM) instead of Title/Subtitle.
// =============================================================================

using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FigForge
{
    [DisallowMultipleComponent]
    [AddComponentMenu("FigForge/Table")]
    public class FigForgeTable : MonoBehaviour
    {
        public RectTransform content;
        public float rowHeight = 40f;
        public int columns = 1;
        public string labelPrefix = "Item";
        public FigForgeListRowStyle itemStyle = new FigForgeListRowStyle();
        public Color itemRollover = Color.white;
        public bool hasItemRollover;

        // Rich-row mode: a cloned template (the captured Row subtree) + per-state row fills.
        public GameObject rowTemplate;
        public FigForgeFill rowRegular = FigForgeFill.Solid(Color.white);
        public FigForgeFill rowRollover = FigForgeFill.Solid(Color.white);
        public FigForgeFill rowPressed = FigForgeFill.Solid(Color.white);
        public FigForgeFill rowSelected = FigForgeFill.Solid(Color.white);
        public bool rowHasRollover, rowHasPressed, rowHasSelected;
        int _selected = -1;

        readonly List<List<string>> _rows = new List<List<string>>();

        public IReadOnlyList<IReadOnlyList<string>> Rows => _rows;

        /// <summary>Show/hide the whole control — `table.Visible = false`. Drives
        /// GameObject.SetActive, so a hidden control stops rendering, receiving input,
        /// and contributing to layout.</summary>
        public bool Visible
        {
            get => gameObject.activeSelf;
            set => gameObject.SetActive(value);
        }

        // Set the rows from cell text grids (n rows × m cells; short rows pad blank).
        public void SetRows(IList<IList<string>> rows)
        {
            _rows.Clear();
            if (rows != null)
                for (int i = 0; i < rows.Count; i++)
                    _rows.Add(rows[i] != null ? new List<string>(rows[i]) : new List<string>());
            Rebuild();
        }

        public void SetRows(IEnumerable<IEnumerable<string>> rows)
        {
            _rows.Clear();
            if (rows != null)
                foreach (var r in rows)
                    _rows.Add(r != null ? new List<string>(r) : new List<string>());
            Rebuild();
        }

        public void SetRows(string[][] rows)
        {
            _rows.Clear();
            if (rows != null)
                foreach (var r in rows)
                    _rows.Add(r != null ? new List<string>(r) : new List<string>());
            Rebuild();
        }

        public void Configure(RectTransform contentRoot, float height, int cols, string label,
                              FigForgeListRowStyle style, Color rollover, bool hasRollover)
        {
            content = contentRoot;
            rowHeight = Mathf.Max(1f, height);
            columns = Mathf.Max(1, cols);
            labelPrefix = string.IsNullOrEmpty(label) ? "Item" : label;
            itemStyle = style ?? new FigForgeListRowStyle();
            itemRollover = rollover;
            hasItemRollover = hasRollover;
        }

        void OnEnable()
        {
            if (content != null) HealContentLayout();
        }

        public void Rebuild()
        {
            if (content == null) return;
            HealContentLayout();
            ClearRows();
            for (int i = 0; i < _rows.Count; i++)
                CreateRow(i, _rows[i]);
        }

        // Rows size themselves via LayoutElement preferredHeight, which a layout group
        // only honours on a CONTROLLED axis (the FigForgeList lesson).
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
            {
                var cells = new List<string>(columns);
                for (int c = 0; c < columns; c++)
                    cells.Add(c == 0 ? labelPrefix + " " + (i + 1) : "R" + (i + 1) + "C" + (c + 1));
                CreateRow(i, cells);
            }
        }

        // Single-select: mark `index` selected, clear the rest.
        public void Select(int index)
        {
            _selected = index;
            if (content == null) return;
            for (int i = 0; i < content.childCount; i++)
            {
                var r = content.GetChild(i).GetComponent<FigForgeTableRow>();
                if (r != null) r.SetSelected(r.index == index);
            }
        }

        public int SelectedIndex => _selected;

        void CreateRow(int index, List<string> cells)
        {
            if (rowTemplate != null) { CreateTemplateRow(index, cells); return; }
            CreateStyledRow(index, cells);
        }

        // Rich row: clone the captured Row subtree, bind Cell1..CellM, wire state
        // colours + single-select via FigForgeTableRow, re-enable the HitArea target.
        void CreateTemplateRow(int index, List<string> cells)
        {
            var row = Instantiate(rowTemplate, content);
            row.name = "Row " + (index + 1);
            row.SetActive(true);
            var le = row.GetComponent<LayoutElement>() ?? row.AddComponent<LayoutElement>();
            le.minHeight = rowHeight; le.preferredHeight = rowHeight;

            for (int c = 0; c < Mathf.Max(columns, cells != null ? cells.Count : 0); c++)
            {
                var cellT = FindByName(row.transform, "Cell" + (c + 1));
                if (cellT == null) continue;
                var tmp = cellT.GetComponent<TMP_Text>();
                if (tmp != null) tmp.text = cells != null && c < cells.Count ? (cells[c] ?? "") : "";
            }

            // Pointer events need a raycastable graphic on the row: the cloned subtree is
            // built render-only (raycasts stripped). Re-enable the HitArea when the design
            // has one; otherwise add a transparent full-bleed Image as the hit surface.
            var hitT = FindByName(row.transform, "HitArea");
            var hit = hitT != null ? hitT.GetComponent<Graphic>() : null;
            if (hit != null) hit.raycastTarget = true;
            else
            {
                var img = row.GetComponent<Image>() ?? row.AddComponent<Image>();
                img.color = new Color(0, 0, 0, 0);
                img.raycastTarget = true;
            }

            var bgT = FindByName(row.transform, "Regular");
            var bg = bgT != null ? bgT.GetComponent<Graphic>() : null;

            var fr = row.AddComponent<FigForgeTableRow>();
            fr.owner = this; fr.index = index;
            fr.regular = rowRegular; fr.rollover = rowRollover; fr.pressed = rowPressed; fr.selected = rowSelected;
            fr.hasRollover = rowHasRollover; fr.hasPressed = rowHasPressed; fr.hasSelected = rowHasSelected;
            if (bg != null) fr.Bind(bg);
        }

        // Case-insensitive: the exporter sanitizes captured subtree names to lowercase
        // ("Cell1" → "cell1"), so an exact match against the design-side casing never hits.
        static Transform FindByName(Transform t, string name)
        {
            foreach (var rt in t.GetComponentsInChildren<Transform>(true))
                if (string.Equals(rt.name, name, System.StringComparison.OrdinalIgnoreCase)) return rt;
            return null;
        }

        // Flat fallback when no Row subtree was captured: a styled (or transparent) row
        // background with m equal-width TMP cells.
        void CreateStyledRow(int index, List<string> cells)
        {
            var row = NewRect("Row " + (index + 1), content);
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
                rr.SetStyle(new FigForgeShapeStyle
                {
                    fill = itemStyle.fill,
                    stroke = itemStyle.stroke,
                    corners = itemStyle.corners,
                    shadowColor = itemStyle.shadowColor,
                    shadowOffset = itemStyle.shadowOffset,
                    shadowBlur = itemStyle.shadowBlur,
                    shadowSpread = itemStyle.shadowSpread,
                });
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

            int cols = Mathf.Max(1, columns);
            for (int c = 0; c < cols; c++)
            {
                var cellGo = NewRect("Cell" + (c + 1), row.transform);
                var crt = cellGo.GetComponent<RectTransform>();
                crt.anchorMin = new Vector2((float)c / cols, 0f);
                crt.anchorMax = new Vector2((float)(c + 1) / cols, 1f);
                crt.offsetMin = new Vector2(c == 0 ? 16f : 6f, 0);
                crt.offsetMax = new Vector2(c == cols - 1 ? -12f : -6f, 0);
                var tmp = cellGo.AddComponent<TextMeshProUGUI>();
                tmp.text = cells != null && c < cells.Count ? (cells[c] ?? "") : "";
                tmp.alignment = TextAlignmentOptions.MidlineLeft;
                tmp.color = new Color(0.1f, 0.1f, 0.12f);
                tmp.fontSize = 13f;
                tmp.raycastTarget = false;
            }
        }

        static GameObject NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }
    }
}
