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
        [Tooltip("Optional explicit column header names. When left empty, GetColumnNames() " +
                 "scrapes the captured Header strip's Cell1..CellM texts; set this to override " +
                 "(e.g. when there's no designed Header, or the captured titles are placeholders).")]
        public string[] columnNames;
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

        [Tooltip("Fired with the new row index whenever the selected row changes.")]
        public FigForgeSelectionEvent onSelectionChanged = new FigForgeSelectionEvent();
        int _selected = -1;

        readonly List<List<string>> _rows = new List<List<string>>();

        /// <summary>The table's rows. Normally the data model you set via SetRows/AddRow; if
        /// that's empty but the table is showing design-time preview rows, returns those rows'
        /// visible cell text instead — so Rows always matches what's on screen (and Rows.Count
        /// == RowCount). Once you SetRows, it's the data model verbatim.</summary>
        public IReadOnlyList<IReadOnlyList<string>> Rows
            => (_rows.Count > 0 || content == null) ? (IReadOnlyList<IReadOnlyList<string>>)_rows : ReadRenderedRows();

        // Scrape every rendered row into cell lists — the preview-row fallback for Rows,
        // mirroring GetRow's per-row fallback. Only hit when the model is empty but rows render.
        IReadOnlyList<IReadOnlyList<string>> ReadRenderedRows()
        {
            int n = content.childCount;
            var rows = new List<IReadOnlyList<string>>(n);
            for (int i = 0; i < n; i++)
                rows.Add(ReadRenderedRow(i) ?? new List<string>());
            return rows;
        }

        /// <summary>Show/hide the whole control — `table.isVisible = false`. Drives
        /// GameObject.SetActive, so a hidden control stops rendering, receiving input,
        /// and contributing to layout.</summary>
        public bool isVisible
        {
            get => gameObject.activeSelf;
            set => gameObject.SetActive(value);
        }

        // Set the rows from cell text grids (n rows × m cells). Stored rows keep their
        // input width as-is; rendering normalizes each row to `columns` — short rows pad
        // blank and extra cells beyond `columns` are not rendered.
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

        // --- Granular row/column accessors ------------------------------------
        // The accessors below address the data model set via SetRows/AddRow/… —
        // NOT preview rows (CreatePreviewRows holds no backing data). Reads return
        // null for out-of-range; single-cell/row writes patch the rendered text IN
        // PLACE (no Rebuild, so scroll + selection survive). Add/Insert/Remove change
        // the row count, so they Rebuild.

        /// <summary>Number of rows the table currently shows — the range Select accepts and
        /// GetRow/GetCell address. This is the rendered row count (so design-time preview rows
        /// count too); it equals the data-model size once you SetRows/AddRow.</summary>
        public int RowCount => content != null ? content.childCount : _rows.Count;

        /// <summary>Columns the table renders to. Short rows pad blank; cells past this
        /// are dropped on render. (Read-only view of the `columns` field, floored at 1.)</summary>
        public int ColumnCount => Mathf.Max(1, columns);

        /// <summary>Column header names, length == ColumnCount. Uses the explicit
        /// `columnNames` override where set, otherwise scrapes the captured Header strip's
        /// Cell1..CellM texts; any name still missing falls back to "Column {n}". So a row
        /// can be printed against its headers without re-declaring them.</summary>
        public string[] GetColumnNames()
        {
            int cols = ColumnCount;
            var names = new string[cols];
            for (int c = 0; c < cols; c++)
            {
                string n = (columnNames != null && c < columnNames.Length) ? columnNames[c] : null;
                if (string.IsNullOrEmpty(n)) n = GetHeaderCellText(c);
                names[c] = !string.IsNullOrEmpty(n) ? n : "Column " + (c + 1);
            }
            return names;
        }

        /// <summary>One column's header name, or null if `col` is out of range. See
        /// GetColumnNames for the override/scrape/fallback resolution.</summary>
        public string GetColumnName(int col)
            => (col >= 0 && col < ColumnCount) ? GetColumnNames()[col] : null;

        // The captured Header is a render-only 'Header' child pinned at the top; the plugin
        // builds its column titles as TMP cells named Cell1..CellM (same naming as the data
        // rows). Read one here so GetColumnNames works without the caller re-declaring the
        // headers. Null for a header-less table or a missing/blank cell.
        string GetHeaderCellText(int col)
        {
            var headerT = FindByName(transform, "Header");
            if (headerT == null) return null;
            var cellT = FindByName(headerT, "Cell" + (col + 1));
            var tmp = cellT != null ? cellT.GetComponent<TMP_Text>() : null;
            return tmp != null ? tmp.text : null;
        }

        /// <summary>The currently-selected row's cells, or null when nothing is selected.
        /// Handy in an onSelectionChanged handler: `var row = table.SelectedRow;`.</summary>
        public IReadOnlyList<string> SelectedRow => GetRow(_selected);

        /// <summary>Read one cell's text. Returns "" for a valid cell a short row never
        /// filled, and null when row/col is out of range.</summary>
        public string GetCell(int row, int col)
        {
            if (col < 0 || col >= ColumnCount) return null;
            var cells = GetRow(row);
            if (cells == null) return null;
            return col < cells.Count ? (cells[col] ?? "") : "";
        }

        /// <summary>Read a whole row's cells (read-only), or null if `row` is out of range.
        /// Reads the data model (SetRows/AddRow); for a row that's rendered but has no backing
        /// data — design-time preview rows — it falls back to the row's visible cell text, so a
        /// validly-selected row never returns null.</summary>
        public IReadOnlyList<string> GetRow(int row)
        {
            if (row < 0) return null;
            if (row < _rows.Count) return _rows[row];
            return ReadRenderedRow(row); // preview rows: scrape what's on screen
        }

        // Reconstruct a row's cells from the rendered Cell1..CellM TMP text. Used when the
        // data model doesn't cover `row` (preview rows render but never populate _rows).
        // Null when the row isn't rendered either.
        IReadOnlyList<string> ReadRenderedRow(int row)
        {
            if (content == null || row >= content.childCount) return null;
            int cols = ColumnCount;
            var cells = new List<string>(cols);
            for (int c = 0; c < cols; c++)
            {
                var tmp = GetCellText(row, c);
                cells.Add(tmp != null ? tmp.text : "");
            }
            return cells;
        }

        /// <summary>Set one cell's text. Updates the model AND patches the live cell in
        /// place (no Rebuild). No-op if row/col is out of range; a short stored row is
        /// padded up to `col` so any column in [0, ColumnCount) is writable.</summary>
        public void SetCell(int row, int col, string text)
        {
            if (row < 0 || row >= _rows.Count || col < 0 || col >= ColumnCount) return;
            var cells = _rows[row];
            while (cells.Count <= col) cells.Add("");
            cells[col] = text ?? "";
            var tmp = GetCellText(row, col);
            if (tmp != null) tmp.text = cells[col];
        }

        /// <summary>Replace one row's cells, re-rendering just that row's text in place
        /// (no Rebuild). No-op if `row` is out of range.</summary>
        public void SetRow(int row, IEnumerable<string> cells)
        {
            if (row < 0 || row >= _rows.Count) return;
            var newCells = cells != null ? new List<string>(cells) : new List<string>();
            _rows[row] = newCells;
            int cols = ColumnCount;
            for (int c = 0; c < cols; c++)
            {
                var tmp = GetCellText(row, c);
                if (tmp != null) tmp.text = c < newCells.Count ? (newCells[c] ?? "") : "";
            }
        }

        /// <summary>The live TMP cell for a rendered row — use to restyle a cell at
        /// runtime (colour, font, alignment). Null if the row/col isn't rendered. For
        /// text changes prefer SetCell (keeps the model in sync).</summary>
        public TMP_Text GetCellText(int row, int col)
        {
            if (content == null || row < 0 || row >= content.childCount || col < 0 || col >= ColumnCount) return null;
            var cellT = FindByName(content.GetChild(row), "Cell" + (col + 1));
            return cellT != null ? cellT.GetComponent<TMP_Text>() : null;
        }

        /// <summary>The live FigForgeTableRow for a rendered row (state fills, selection,
        /// GameObject), or null if not rendered.</summary>
        public FigForgeTableRow GetRowObject(int row)
        {
            if (content == null || row < 0 || row >= content.childCount) return null;
            return content.GetChild(row).GetComponent<FigForgeTableRow>();
        }

        /// <summary>Append a row. Row count changed, so the table Rebuilds.</summary>
        public void AddRow(params string[] cells) => AddRow((IEnumerable<string>)cells);

        public void AddRow(IEnumerable<string> cells)
        {
            _rows.Add(cells != null ? new List<string>(cells) : new List<string>());
            Rebuild();
        }

        /// <summary>Insert a row at `index` (clamped to 0..RowCount). Rebuilds. Note this
        /// shifts later indices — a held SelectedIndex now points at a different row.</summary>
        public void InsertRow(int index, IEnumerable<string> cells)
        {
            index = Mathf.Clamp(index, 0, _rows.Count);
            _rows.Insert(index, cells != null ? new List<string>(cells) : new List<string>());
            Rebuild();
        }

        /// <summary>Remove the row at `index`. No-op if out of range. Rebuilds. Clears the
        /// selection if the removed row was selected (Rebuild clamps a now-stale index).</summary>
        public void RemoveRow(int index)
        {
            if (index < 0 || index >= _rows.Count) return;
            _rows.RemoveAt(index);
            if (_selected == index) _selected = -1;
            else if (_selected > index) _selected--;
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
            // Re-apply the stored selection to the freshly-built rows: without this
            // SelectedIndex keeps its old value but no row is highlighted. Clamp first —
            // a shrunk table may no longer contain the old index, in which case clear it.
            if (_selected >= _rows.Count) _selected = -1;
            ApplySelectionVisual(_selected);
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
                var r = content.GetChild(i).GetComponent<FigForgeTableRow>();
                if (r != null) r.SetSelected(r.index == index);
            }
        }

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

            // Normalize to `columns`, matching the styled fallback: short rows pad blank,
            // extra cells beyond the column count are dropped (the template only has
            // Cell1..CellColumns slots anyway). Mirrors the SetRows doc contract.
            int cols = Mathf.Max(1, columns);
            for (int c = 0; c < cols; c++)
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
            FigForgeFill rowRegularFill;
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

            // Wire selection so styled rows behave like template rows: a FigForgeTableRow
            // bound to the same background recolours per state AND lets ApplySelectionVisual
            // paint the highlight, with its OnPointerClick driving single-select. This row
            // component now owns ALL states (it replaces FigForgeButtonStateColors here),
            // so there's a single writer to the graphic — rollover/pressed/selected all use
            // the rollover colour, matching the previous flat/styled visual output.
            var rolloverFill = hasItemRollover ? FigForgeFill.Solid(itemRollover) : rowRegularFill;
            var fr = row.AddComponent<FigForgeTableRow>();
            fr.owner = this; fr.index = index;
            fr.regular = rowRegularFill;
            fr.rollover = rolloverFill; fr.pressed = rolloverFill; fr.selected = rolloverFill;
            fr.hasRollover = hasItemRollover; fr.hasPressed = hasItemRollover; fr.hasSelected = hasItemRollover;
            fr.Bind(rowBg);

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
