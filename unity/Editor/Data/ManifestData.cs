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

    public class StyleData
    {
        public float opacity = 1f;
        public float cornerRadius;
        public float[] corners;          // tl,tr,br,bl
        public Fill fill;
        public Stroke stroke;
    }

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
    }

    public class CanonicalStates { public string normal; public string highlighted; public string pressed; }
    public class CanonicalLabelFont { public string family; public string style; }

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
