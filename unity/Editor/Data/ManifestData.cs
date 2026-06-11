// =============================================================================
// FigForge — manifest data model. Mirrors the JSON emitted by the Figma plugin
// (plugin/src/types.ts). Field names match the JSON exactly (camelCase) so
// Newtonsoft maps them without attributes, except `ref` which is a C# keyword.
// =============================================================================

using System.Collections.Generic;
using Newtonsoft.Json;

namespace FigForge
{
    public class Manifest
    {
        public string schema;
        public string version;          // checked against ManifestParser.SupportedManifestVersions
        public int canonicalSchema;     // plugin's CANONICAL_SCHEMA (types.ts); 0 = pre-2.0 manifest
        public string generator;
        public string exportedAt;
        public ScreenInfo screen;
        public List<ElementData> elements = new List<ElementData>();
        public List<AssetEntry> assets = new List<AssetEntry>();
        public List<FontEntry> fonts = new List<FontEntry>();
        public ManifestSettings settings = new ManifestSettings();
        public List<string> canonicalRefs = new List<string>();
    }

    public class ManifestSettings
    {
        public float fontFaceDilate = 0.15f;
    }

    public class Size { public float w; public float h; }

    public class ScreenInfo
    {
        public string id;
        public string name;
        public string displayName; // original frame name (casing preserved) — codegen class name source
        public Size figmaSize;
        public Size referenceResolution;
        public float exportScale = 1f;
    }

    public class RectData { public float x, y, w, h; }

    public class UnityTransform
    {
        public float[] anchorMin;        // [x,y]
        public float[] anchorMax;
        public float[] pivot;
        public float[] offsetMin;
        public float[] offsetMax;
        public float rotationZ;
    }

    public class GradientStop
    {
        public float position;
        public float[] color;            // rgba 0..1
    }

    public class Fill
    {
        public string kind;              // "solid" | "gradient" | "image"
        public float[] color;            // solid
        public string gradient;          // gradient: linear|radial|angular|diamond
        public List<GradientStop> stops; // gradient
        public float[] transform;        // gradient affine
        public string asset;             // image
        public string scaleMode;         // image
    }

    public class Stroke
    {
        public float[] color;
        public Fill fill;                // full stroke paint; color is fallback
        public float weight;
        public string align;             // inside|outside|center
        public bool dashed;
        public List<float> dashPattern;
    }

    public class ShadowData
    {
        public string kind;               // dropShadow|innerShadow|layerBlur
        public float[] color;            // rgba 0..1
        public float offsetX, offsetY;   // Figma px (+y down)
        public float blur;               // Figma effect radius
        public float spread;
        public bool inner;               // false = drop shadow (rendered)
        public string blurMode;           // uniform|progressive
        public float? startBlur;
        public float? endBlur;
        public bool showBehind;           // drop shadow visible through translucent fills (Figma showShadowBehindNode, default false)
    }

    public class StyleData
    {
        public float opacity = 1f;
        public string blendMode;
        public float cornerRadius;
        public float[] corners;          // tl,tr,br,bl
        public Fill fill;
        public List<Fill> fills;
        public Stroke stroke;
        public List<Stroke> strokes;
        public List<ShadowData> effects;
    }

    public class OutlineData { public float[] color; public float weight; } // text stroke → TMP outline

    public class TextData
    {
        public string content;
        public string fontFamily;
        public string fontStyle;
        public float fontSize;
        public float[] color;
        public string alignH;            // left|center|right|justified
        public string alignV;            // top|middle|bottom
        public float? lineHeight;
        public float? letterSpacing;
        public string autoResize;
        public OutlineData outline;      // null = no outline
    }

    public class CanonicalStates { public string normal; public string highlighted; public string pressed; }
    public class CanonicalLabelFont { public string family; public string style; }
    public class VectorMesh
    {
        public float[] color;             // rgba 0..1 — base colour for this region
        public List<float> verts;         // x0,y0,x1,y1,… node-local px (top-left origin, +y down)
        public List<int> tris;            // triangle indices into verts
        public List<float> alpha;         // per-vertex alpha (1 = solid core, 0 = AA fringe)
    }

    public class VectorDrawing
    {
        public float[] bounds;            // [w,h] node px the verts live in
        public List<VectorMesh> meshes;   // draw order: fills first, strokes on top
    }

    public class CanonicalShape
    {
        public string asset;              // PNG fallback for vector/icon shapes whose path geometry is not a rounded rect
        public VectorDrawing vector;      // procedural vector mesh (preferred over `asset` when present)
        public float cornerRadius;        // uniform (or max when corners are mixed)
        public float[] corners;           // tl,tr,br,bl — per-corner detail (null = uniform cornerRadius)
        public float opacity = 1f;
        public string blendMode;
        public float[] fill;              // solid colour, or legacy gradient stop 0
        public Fill gradient;             // linear n-stop gradient background
        public float[] fill2;             // legacy gradient stop 1 (null = solid fill)
        public float[] gradientTransform; // legacy gradient affine (null = solid fill)
        public List<Fill> fills;          // all Figma fills, bottom-to-top
        public Stroke stroke;
        public List<Stroke> strokes;      // all Figma strokes, bottom-to-top
        // Legacy fields kept for older manifests.
        public float[] borderColor;
        public float borderWidth;
        public string borderAlign;        // inside|outside|center (null = inside)
        public ShadowData shadow;         // first drop shadow on the regular layer
        public List<ShadowData> shadows;  // all visible drop shadows
        public List<ShadowData> effects;  // all visible Figma effects
    }
    public class CanonicalStateColors { public float[] normal; public float[] highlighted; public float[] pressed; }
    public class CanonicalStateShapes { public CanonicalShape normal; public CanonicalShape highlighted; public CanonicalShape pressed; }

    public class CanonicalRef
    {
        public string kind;              // button | toggle | input | dropdown | slider
        [JsonProperty("ref")] public string Ref;
        public string instanceName;
        public string label;
        public string value;             // initial state (toggle on/off, slider value, input text)
        public string placeholder;       // input placeholder text
        public List<string> options;     // dropdown options
        public CanonicalStates states;   // per-state sprite filenames (button)
        public CanonicalLabelFont labelFont;    // THIS instance's label font (per-instance override when it differs)
        public CanonicalLabelFont defLabelFont; // the canonical COMPONENT's label font (the prefab/definition uses this)
        public float? labelFontSize;     // THIS instance's Label font size
        public float? defLabelFontSize;  // the canonical COMPONENT's Label font size
        public CanonicalShape shape;            // procedural background (SDF shader) — overrides the state PNGs when present
        public CanonicalShape instanceShape;    // THIS instance's background when it differs from the component (per-instance override)
        public CanonicalShape rootShape;        // root component visuals/effects shared behind every state
        public CanonicalShape instanceRootShape; // THIS instance's root visuals/effects when they differ
        public CanonicalStateColors stateColors; // the COMPONENT's per-state hover/press fills (drives the prefab)
        public CanonicalStateColors instanceStateColors; // THIS instance's hover/press fills when they differ from the component
        public CanonicalStateShapes stateShapes; // full Regular/RollOver/Pressed rounded-rect styles
        public CanonicalStateShapes instanceStateShapes; // THIS instance's full state styles when they differ
        // --- control-specific (toggle/radio/dropdown/list) ---
        public CanonicalShape checkShape; // toggle/radio: the "on" indicator (Toggle.graphic), shown when value=on
        public List<string> items;        // list: legacy row texts (back-compat; prefer listItems)
        public List<ListItemData> listItems; // list: per-row data (title + optional subtitle)
        public CanonicalShape itemShape;  // list: the row background shape (from the 'Item' template's Regular)
        public float[] itemRollover;      // list: the row hover colour (from the 'Item' template's Rollover)
        public float[] itemPressed;       // list: the row pressed colour (from the 'Item' template's Pressed)
        public float[] itemSelected;      // list: the selected-row colour (from the 'Item' template's Selected)
        public float itemHeight;          // list: row height in Figma px (drives Unity row sizing)
        public float headerHeight;        // list: header height in Figma px (0 = no header)
        public int count;                 // list: number of rows to generate
        public float scrollbarWidth;      // list: scrollbar width in Figma px (0 = default)
        public CanonicalShape scrollTrackShape; // list: scrollbar track styling ('Scrollbar/Track')
        public CanonicalShape scrollThumbShape; // list: scrollbar thumb styling ('Scrollbar/Thumb')
        public float[] scrollThumbRollover; // list: thumb hover colour ('Scrollbar/ThumbRollover')
        public float[] scrollThumbPressed;  // list: thumb pressed colour ('Scrollbar/ThumbPressed')
        public CanonicalShape maskShape;    // list: the 'Mask' layer's styling — cornerRadius rounds the clip
        // table: shares the list fields above (itemShape/itemRollover/itemPressed/
        // itemSelected/itemHeight/headerHeight/count/scrollbar*/maskShape) verbatim.
        public List<List<string>> tableRows; // table: per-row cell texts (n rows × m columns)
        public int columns;                  // table: column count (CellN texts in the TableRow master)
        // slider: Track/Fill/Thumb styling + thumb state colours. `value` above is the
        // RAW initial value within [minValue..maxValue]; legacy manifests omit the
        // range (both 0 → treat as 0..1, where value was the plain Fill÷Track ratio).
        public CanonicalShape trackShape;   // slider: the rail ('Track')
        public CanonicalShape fillShape;    // slider: the filled portion ('Fill')
        public CanonicalShape thumbShape;   // slider: the draggable thumb ('Thumb')
        public float[] thumbRollover;       // slider: thumb hover colour ('ThumbRollover', hidden layer)
        public float[] thumbPressed;        // slider: thumb pressed colour ('ThumbPressed', hidden layer)
        public float minValue;              // slider: range start (default 0)
        public float maxValue;              // slider: range end (default 0 → legacy 0..1)
        public int slots;                   // slider: discrete slot count (>=2 snaps to slots; 0 = continuous)
        // progress: a slider with no thumb and no input — shares trackShape/fillShape/
        // value above (value = the 0..1 fill ratio, displayed as a percentage).
        public bool indeterminate;          // progress: true = animated ('Indeterminate' layer present in the master)
        public string progressStyle;        // progress: bar (default) | ring | segments — from the Track layer's type
        public float ringStart;             // ring: arc start in degrees from 12 o'clock, clockwise
        public float arcSpan;               // ring: total arc span in degrees (360 = closed ring, 270 = gauge; 0 = legacy → 360)
        public float ringThickness;         // ring: stroke thickness as a fraction of the outer radius (1 = pie wedge; 0 = legacy → 0.25)
        public int segments;                // segments: total block count
        public float segmentGap;            // segments: gap between blocks in Figma px
        public CanonicalShape optionShape; // dropdown: option row Regular shape
        public CanonicalShape popupShape;  // dropdown popup shell/background (Options frame)
        public CanonicalShape optionRolloverShape; // dropdown: option row Rollover full shape
        public CanonicalShape optionPressedShape;  // dropdown: option row Pressed full shape
        public CanonicalShape optionSelectedShape; // dropdown: option row Selected/current-value full shape
        public float[] optionRollover;     // dropdown: option row Rollover colour
        public float[] optionPressed;      // dropdown: option row Pressed colour
        public float[] optionSelected;     // dropdown: option row Selected/current-value colour
        public float optionHeight;         // dropdown: option row height in Figma px
        public string arrowAsset;          // dropdown: Arrow frame rendered as a sprite (supports nested vectors/text/shapes)
        public string arrowRolloverAsset;  // dropdown: Arrow/Rollover rendered as a sprite
        public string arrowPressedAsset;   // dropdown: Arrow/Pressed rendered as a sprite
        public float[] arrowColor;         // dropdown: closed chevron Regular colour
        public float[] arrowRollover;      // dropdown: closed chevron Rollover colour
        public float[] arrowPressed;       // dropdown: closed chevron Pressed colour
        public float[] bgRollover;         // dropdown: closed background Rollover fill
        public float[] bgPressed;          // dropdown: closed background Pressed fill
        public Dictionary<string, float[]> parts; // normalized anchors [minX,minY,maxX,maxY] of named sub-layers
        // Full render-only Figma subtree per named part. When present, Unity renders
        // the part as its complete nested subtree (all fills/strokes/shadows/vectors/
        // masks/text) instead of the flat `shape`/`asset` fallback.
        public Dictionary<string, ElementSubtree> partTrees;
    }

    public class NavLink
    {
        public string target;            // destination screen name (sanitized)
        public string trigger;           // e.g. "click"
    }

    /// <summary>One row's data in a canonical List (two-line: title + optional subtitle).</summary>
    public class ListItemData
    {
        public string title;
        public string subtitle;
    }

    public class AssetBounds
    {
        public float x, y, w, h;
        public int pixelWidth, pixelHeight;
        public float exportScale = 1f;
    }

    public class NineSlice { public int left, right, top, bottom; }

    public class AutoLayout
    {
        public string mode;              // horizontal|vertical
        public float paddingTop, paddingRight, paddingBottom, paddingLeft, spacing;
        public string alignH, alignV;
    }

    public class ElementData
    {
        public string id;
        public string name;
        public string displayName;
        public string type;
        public string parentId;
        public RectData rect;
        public float rotation;
        public UnityTransform transform;
        public List<string> components = new List<string>();
        public StyleData style;
        public TextData text;
        public string asset;
        public VectorDrawing vector;      // procedural vector mesh (preferred over `asset` when present)
        public AssetBounds assetBounds;
        public NineSlice nineSlice;
        public CanonicalRef canonical;
        public NavLink nav;
        public bool interactive;
        public bool clipsContent;
        public bool merged;
        public AutoLayout autoLayout;
        public List<string> children = new List<string>();
    }

    /// <summary>A self-contained, render-only element tree for one canonical control
    /// part (e.g. a toggle Checkmark or dropdown Arrow). `elements` is a flat list with
    /// ids local to this subtree; `root` is the top element, which Unity stretches to
    /// fill the part's anchored container.</summary>
    public class ElementSubtree
    {
        public string root;
        public List<ElementData> elements = new List<ElementData>();
    }

    public class AssetEntry { public string file; public string nodeId; public float scale = 1f; }
    public class FontEntry { public string family; public List<string> styles = new List<string>(); }

    // ---- project bundle (whole-page export) -------------------------------
    public class ProjectScreen
    {
        public string name;
        public string manifest;
        public string section;       // enclosing Figma section ('' if none)
        public string role = "screen"; // "screen" | "shell"
    }

    public class ProjectData
    {
        public string schema;            // "figforge/project"
        public string version;
        public string generator;
        public string name;
        public string exportedAt;
        public string initial;
        public List<ProjectScreen> screens = new List<ProjectScreen>();
    }
}
