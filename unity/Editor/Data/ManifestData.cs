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
        public string version;
        public string generator;
        public string exportedAt;
        public ScreenInfo screen;
        public List<ElementData> elements = new List<ElementData>();
        public List<AssetEntry> assets = new List<AssetEntry>();
        public List<FontEntry> fonts = new List<FontEntry>();
        public List<string> canonicalRefs = new List<string>();
    }

    public class Size { public float w; public float h; }

    public class ScreenInfo
    {
        public string id;
        public string name;
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
        public float weight;
        public string align;             // inside|outside|center
        public bool dashed;
    }

    public class ShadowData
    {
        public float[] color;            // rgba 0..1
        public float offsetX, offsetY;   // Figma px (+y down)
        public float blur;               // Figma effect radius
        public float spread;
        public bool inner;               // false = drop shadow (rendered)
    }

    public class StyleData
    {
        public float opacity = 1f;
        public float cornerRadius;
        public float[] corners;          // tl,tr,br,bl
        public Fill fill;
        public Stroke stroke;
        public List<ShadowData> shadows;
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
    public class CanonicalShape
    {
        public float cornerRadius;
        public float[] fill;              // solid colour, or gradient stop 0
        public float[] fill2;             // gradient stop 1 (null = solid fill)
        public float[] gradientTransform; // gradient affine (null = solid fill)
        public float[] borderColor;
        public float borderWidth;
        public string borderAlign;        // inside|outside|center (null = inside)
        public ShadowData shadow;         // first drop shadow on the regular layer
    }
    public class CanonicalStateColors { public float[] normal; public float[] highlighted; public float[] pressed; }

    public class CanonicalRef
    {
        public string kind;              // button | toggle | input | dropdown | slider
        [JsonProperty("ref")] public string Ref;
        public string instanceName;
        public string label;
        public string value;             // initial state (toggle on/off, slider value, input text)
        public List<string> options;     // dropdown options
        public CanonicalStates states;   // per-state sprite filenames (button)
        public CanonicalLabelFont labelFont;    // THIS instance's label font (per-instance override when it differs)
        public CanonicalLabelFont defLabelFont; // the canonical COMPONENT's label font (the prefab/definition uses this)
        public CanonicalShape shape;            // procedural background (SDF shader) — overrides the state PNGs when present
        public CanonicalShape instanceShape;    // THIS instance's background when it differs from the component (per-instance override)
        public CanonicalStateColors stateColors; // the COMPONENT's per-state hover/press fills (drives the prefab)
        public CanonicalStateColors instanceStateColors; // THIS instance's hover/press fills when they differ from the component
        // --- control-specific (toggle/radio/dropdown/list) ---
        public CanonicalShape checkShape; // toggle/radio: the "on" indicator (Toggle.graphic), shown when value=on
        public List<string> items;        // list: the text of each row (first row is the template)
        public CanonicalShape itemShape;  // list: the row background shape (from the 'Item' template's Regular)
        public float[] itemRollover;      // list: the row hover colour (from the 'Item' template's Rollover)
        public float itemHeight;          // list: row height in Figma px (drives Unity row sizing)
        public int count;                 // list: number of rows to generate
        public Dictionary<string, float[]> parts; // normalized anchors [minX,minY,maxX,maxY] of named sub-layers
    }

    public class NavLink
    {
        public string target;            // destination screen name (sanitized)
        public string trigger;           // e.g. "click"
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
