// =============================================================================
// FigForge — type contracts
//
// This file defines two things:
//   1. The MANIFEST CONTRACT (exported JSON), mirrored 1:1 by the Unity importer's
//      C# ManifestData. Changing a field here means changing it there too.
//   2. Internal plugin runtime types (UI tree, messages, export options).
// =============================================================================

export const MANIFEST_SCHEMA = 'figforge/manifest';
export const MANIFEST_VERSION = '1.0';

// ---------------------------------------------------------------------------
// Geometry primitives
// ---------------------------------------------------------------------------
export type Vec2 = [number, number];
export type RGBA = [number, number, number, number]; // each channel 0..1

export interface Size {
  w: number;
  h: number;
}

export interface Rect {
  x: number;
  y: number;
  w: number;
  h: number;
}

// ---------------------------------------------------------------------------
// Fills — discriminated union so gradients/images survive into Unity instead
// of being flattened to a single solid colour.
// ---------------------------------------------------------------------------
export type GradientKind = 'linear' | 'radial' | 'angular' | 'diamond';

export interface GradientStop {
  position: number; // 0..1 along the gradient
  color: RGBA;
}

export type Fill =
  | { kind: 'solid'; color: RGBA }
  | {
      kind: 'gradient';
      gradient: GradientKind;
      stops: GradientStop[];
      // 2x3 affine the way Figma reports gradientTransform, row-major.
      transform?: number[];
    }
  | { kind: 'image'; asset: string; scaleMode: string };

export type StrokeAlign = 'inside' | 'outside' | 'center';

export interface Stroke {
  color: RGBA;
  weight: number;
  align: StrokeAlign;
  dashed: boolean;
}

export interface Shadow {
  color: RGBA;
  offsetX: number;
  offsetY: number;
  blur: number;   // Figma effect radius
  spread: number;
  inner: boolean; // false = drop shadow (rendered); true = inner shadow (captured, not yet rendered)
}

export interface Style {
  opacity: number;
  cornerRadius: number; // max corner; per-corner detail in `corners`
  corners?: [number, number, number, number]; // tl, tr, br, bl
  fill?: Fill;
  stroke?: Stroke;
  shadows?: Shadow[];
}

// ---------------------------------------------------------------------------
// Unity RectTransform mapping. Constraint-driven: a stretched axis emits
// offsetMin/offsetMax against (0,0)-(1,1) anchors; a fixed axis emits a
// sizeDelta + anchoredPosition against its real constraint anchor.
// ---------------------------------------------------------------------------
export interface UnityTransform {
  anchorMin: Vec2;
  anchorMax: Vec2;
  pivot: Vec2;
  // offsetMin/offsetMax fully describe the rect for ANY anchor config (fixed or
  // stretched), so the importer can just assign anchors then offsets. Measured
  // in Figma reference pixels (parent bottom-left origin); the importer scales.
  offsetMin: Vec2;
  offsetMax: Vec2;
  rotationZ: number; // degrees, CCW positive (Unity convention)
}

// ---------------------------------------------------------------------------
// Text
// ---------------------------------------------------------------------------
export interface TextProps {
  content: string;
  fontFamily: string;
  fontStyle: string; // "Regular" | "Bold" | "SemiBold" | ...
  fontSize: number;
  color: RGBA;
  alignH: 'left' | 'center' | 'right' | 'justified';
  alignV: 'top' | 'middle' | 'bottom';
  lineHeight?: number;
  letterSpacing?: number;
  autoResize?: string;
  outline?: { color: RGBA; weight: number }; // text stroke → TMP outline
}

// ---------------------------------------------------------------------------
// Canonical UI elements. An element named `Btn_<instance>_<ref>` is rendered
// in Unity as an instance of a named canonical Button definition rather than
// rebuilt from PNG/text. Scoped to buttons for now; `kind` keeps it extensible.
// ---------------------------------------------------------------------------
export type CanonicalKind = 'button' | 'toggle' | 'radio' | 'input' | 'dropdown' | 'slider' | 'list';

/** Exported per-state background sprites for an interactive control. */
export interface CanonicalStates {
  normal?: string;
  highlighted?: string;
  pressed?: string;
}

/** Procedural background of a button (rendered by the SDF shader). */
export interface ButtonShape {
  cornerRadius: number;
  fill?: RGBA; // solid colour, or legacy gradient fallback colour
  gradient?: Extract<Fill, { kind: 'gradient' }>; // linear n-stop gradient background
  fill2?: RGBA; // legacy gradient stop 1
  gradientTransform?: number[]; // legacy gradient affine (Figma row-major)
  stroke?: Stroke;
  // Legacy fields kept so older exports still deserialize in Unity.
  borderColor?: RGBA;
  borderWidth?: number;
  borderAlign?: StrokeAlign; // inside|outside|center (default inside)
  shadow?: Shadow; // first drop shadow on the regular layer
}

export interface CanonicalRef {
  kind: CanonicalKind;
  ref: string; // canonical definition name to instantiate in Unity
  instanceName: string; // the design-specific name (middle token)
  label?: string; // text to stamp onto the instance, if any
  value?: string; // initial state: toggle on/off, slider value, input text
  placeholder?: string; // input placeholder text
  options?: string[]; // dropdown options
  states?: CanonicalStates; // sprite filenames per Button state (from Figma layers)
  labelFont?: { family: string; style: string }; // THIS instance's label font (per-instance override when it differs from the definition)
  defLabelFont?: { family: string; style: string }; // the canonical COMPONENT's label font — the prefab/definition uses this
  // Procedural background shape (solid buttons): the importer renders this with a
  // crisp SDF shader instead of the exported state PNGs. Absent → PNG fallback.
  shape?: ButtonShape; // the COMPONENT's background (drives the generated prefab)
  instanceShape?: ButtonShape; // THIS instance's background, when it differs from the component (per-instance override)
  stateColors?: { normal?: RGBA; highlighted?: RGBA; pressed?: RGBA }; // the COMPONENT's per-state hover/press fills (drives the prefab)
  instanceStateColors?: { normal?: RGBA; highlighted?: RGBA; pressed?: RGBA }; // THIS instance's hover/press fills when they differ from the component
  // --- control-specific (toggle/radio/dropdown/list) ---
  checkShape?: ButtonShape; // toggle/radio: the "on" indicator (UGUI Toggle.graphic), shown when value=on
  items?: string[];         // list: the text of each row (first row is the template)
  itemShape?: ButtonShape;  // list: the row background shape (from the 'Item' template's Regular)
  itemRollover?: RGBA;      // list: the row hover colour (from the 'Item' template's Rollover)
  itemHeight?: number;      // list: row height in Figma px (drives Unity row sizing)
  count?: number;           // list: number of rows to generate (derived from list height ÷ row height)
  // dropdown: the option row is its own component (DropdownOption) with states; these
  // drive the TMP_Dropdown item template (options[] above are the option texts)
  optionShape?: ButtonShape; // DropdownOption Regular shape (corner/fill/border)
  popupShape?: ButtonShape;  // dropdown popup shell/background (Options frame)
  optionRolloverShape?: ButtonShape; // DropdownOption Rollover full shape override
  optionPressedShape?: ButtonShape;  // DropdownOption Pressed full shape override
  optionSelectedShape?: ButtonShape; // DropdownOption Selected/current-value full shape override
  optionRollover?: RGBA;     // DropdownOption Rollover colour (hover)
  optionPressed?: RGBA;      // DropdownOption Pressed colour
  optionSelected?: RGBA;     // DropdownOption Selected colour (current value, distinct from hover)
  optionHeight?: number;     // option row height in Figma px
  // dropdown arrow visual exported from the Arrow frame. The Arrow frame may
  // contain vectors, shapes, text, groups, or Regular/Rollover/Pressed state
  // layers; Unity uses these sprites instead of rebuilding the arrow from text.
  arrowAsset?: string;
  arrowRolloverAsset?: string;
  arrowPressedAsset?: string;
  // dropdown arrow chevron glyph colours per state (Arrow frame Regular/Rollover/Pressed)
  arrowColor?: RGBA;         // Regular chevron colour
  arrowRollover?: RGBA;      // Rollover chevron colour (dropdown hover)
  arrowPressed?: RGBA;       // Pressed chevron colour
  // dropdown CLOSED-control (Background) hover/press fills (the box itself)
  bgRollover?: RGBA;         // closed box hover fill
  bgPressed?: RGBA;          // closed box press fill
  // Normalized Unity anchors [minX,minY,maxX,maxY] of named sub-layers (Background,
  // Checkmark, Label, Arrow, Item) so composite controls lay out precisely at any size.
  parts?: Record<string, number[]>;
}

/** Prototype navigation captured as data (no behaviour wired). */
export interface NavLink {
  target: string; // destination screen name (sanitized)
  trigger: string; // e.g. "click"
}

export interface AssetBounds {
  x: number;
  y: number;
  w: number;
  h: number;
  pixelWidth: number;
  pixelHeight: number;
  exportScale: number;
}

export interface AutoLayout {
  mode: 'horizontal' | 'vertical';
  paddingTop: number;
  paddingRight: number;
  paddingBottom: number;
  paddingLeft: number;
  spacing: number;
  alignH: string;
  alignV: string;
}

export interface NineSlice {
  left: number;
  right: number;
  top: number;
  bottom: number;
}

export interface ManifestElement {
  id: string;
  name: string; // sanitized, used for filenames / GameObject names
  displayName: string; // human label (original-ish), shown in importer tree
  type: string; // Figma node type
  parentId: string | null;
  rect: Rect;
  rotation: number; // raw figma rotation, degrees
  transform: UnityTransform;
  components: string[];
  style?: Style;
  text?: TextProps;
  asset?: string | null; // PNG filename when rasterized
  assetBounds?: AssetBounds;
  nineSlice?: NineSlice;
  canonical?: CanonicalRef;
  nav?: NavLink;
  interactive: boolean;
  clipsContent: boolean;
  merged: boolean;
  autoLayout?: AutoLayout;
  children: string[];
}

export interface ManifestAsset {
  file: string;
  nodeId: string;
  scale: number;
}

export interface ManifestFont {
  family: string;
  styles: string[];
}

export interface ScreenInfo {
  id: string;
  name: string;
  figmaSize: Size;
  referenceResolution: Size; // figmaSize * exportScale
  exportScale: number;
}

export interface Manifest {
  schema: typeof MANIFEST_SCHEMA;
  version: typeof MANIFEST_VERSION;
  generator: 'FigForge';
  exportedAt: string;
  screen: ScreenInfo;
  elements: ManifestElement[];
  assets: ManifestAsset[];
  fonts: ManifestFont[];
  canonicalRefs: string[]; // distinct canonical ref names referenced by elements
}

// ---------------------------------------------------------------------------
// Project bundle (whole-page export): a project.json index + per-screen folders.
// ---------------------------------------------------------------------------
export const PROJECT_SCHEMA = 'figforge/project';

export interface ProjectScreen {
  name: string;
  manifest: string; // path within the bundle, e.g. "Home/manifest.json"
}

export interface Project {
  schema: typeof PROJECT_SCHEMA;
  version: typeof MANIFEST_VERSION;
  generator: 'FigForge';
  name: string;
  exportedAt: string;
  initial: string;
  screens: ProjectScreen[];
}

// ---------------------------------------------------------------------------
// Export options + per-element config (UI → main)
// ---------------------------------------------------------------------------
export type ExportScale =
  | { type: 'scale'; value: number }
  | { type: 'width'; value: number }
  | { type: 'height'; value: number };

export interface ExportOptions {
  autoMerge: boolean;
  rasterizeStrokes: boolean; // when false, strokes become manifest data
  emitGradients: boolean;
  emitImageFills: boolean;
}

export const DEFAULT_EXPORT_SCALE: ExportScale = { type: 'scale', value: 2 };
export const DEFAULT_EXPORT_OPTIONS: ExportOptions = {
  autoMerge: true,
  rasterizeStrokes: false,
  emitGradients: true,
  emitImageFills: true,
};

export interface ElementConfig {
  id: string;
  excluded?: boolean;
  merged?: boolean;
  rasterize?: boolean; // force a TEXT/container to export as a PNG
}

// ---------------------------------------------------------------------------
// UI tree (main → ui)
// ---------------------------------------------------------------------------
export interface TreeNode {
  id: string;
  name: string;
  displayName: string;
  type: string;
  depth: number;
  visible: boolean;
  canExportPng: boolean;
  canMerge: boolean;
  canonicalRef?: string;
  children: TreeNode[];
}

// ---------------------------------------------------------------------------
// Internal element gathered during traversal (pre-manifest)
// ---------------------------------------------------------------------------
export interface RawElement {
  node: SceneNode;
  id: string;
  parentId: string | null;
  depth: number;
  exportable: boolean;
}
