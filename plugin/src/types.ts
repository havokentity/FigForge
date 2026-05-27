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

export interface Style {
  opacity: number;
  cornerRadius: number; // max corner; per-corner detail in `corners`
  corners?: [number, number, number, number]; // tl, tr, br, bl
  fill?: Fill;
  stroke?: Stroke;
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
}

// ---------------------------------------------------------------------------
// Canonical UI elements. An element named `Btn_<instance>_<ref>` is rendered
// in Unity as an instance of a named canonical Button definition rather than
// rebuilt from PNG/text. Scoped to buttons for now; `kind` keeps it extensible.
// ---------------------------------------------------------------------------
export type CanonicalKind = 'button';

export interface CanonicalRef {
  kind: CanonicalKind;
  ref: string; // canonical definition name to instantiate in Unity
  instanceName: string; // the design-specific name (middle token)
  label?: string; // text to stamp onto the instance, if any
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
