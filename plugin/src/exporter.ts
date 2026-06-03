// =============================================================================
// FigForge — exporter
//
// Walks the selected root, builds the manifest (transforms via the mapper,
// style/text/canonical metadata), and rasterizes exportable nodes to PNGs with
// hash dedup, ancestor-clip suppression, and the FigmaTest robustness fixes:
//   • failed exportAsync drops the dangling asset ref → element falls back to
//     its style.fill colored panel instead of a missing-sprite white box.
//   • fill-less styled containers default to transparent, not opaque white.
// =============================================================================

import {
  DEFAULT_EXPORT_OPTIONS,
  MANIFEST_SCHEMA,
  MANIFEST_VERSION,
  type ButtonShape,
  type CanonicalKind,
  type CanonicalRef,
  type CanonicalStateShapes,
  type CanonicalStates,
  type ExportOptions,
  type ExportScale,
  type Fill,
  type GradientKind,
  type Manifest,
  type Shadow,
  type ManifestAsset,
  type ManifestElement,
  type ManifestFont,
  type NavLink,
  type RGBA,
  type Stroke,
  type Style,
  type TextProps,
} from './types';
import { generateFileName, sanitize } from './naming';
import {
  detectCanonical,
  hasMeaningfulFill,
  hasVisibleStroke,
  isEmptyPaint,
  isExportable,
} from './traverser';
import { mapTransform, rootTransform } from './mapper';
import { buildVectorDrawing } from './vector';

export interface ExportResult {
  manifest: Manifest;
  assets: { name: string; data: number[] }[];
}

export type ProgressFn = (current: number, total: number, label: string) => void;

const INTERACTIVE_HINTS = ['button', 'btn', 'input', 'field', 'toggle', 'checkbox', 'switch'];
const DEFAULT_FONT_FACE_DILATE = 0.15;

// ---------------------------------------------------------------------------
// Paint → manifest helpers
// ---------------------------------------------------------------------------
function toRGBA(color: RGB | RGBA_ | undefined, opacity: number | undefined): RGBA {
  if (!color) return [0, 0, 0, 0];
  const a = typeof opacity === 'number' ? opacity : (color as RGBA_).a ?? 1;
  return [color.r, color.g, color.b, a];
}
interface RGB {
  r: number;
  g: number;
  b: number;
}
interface RGBA_ extends RGB {
  a?: number;
}

function gradientKind(type: string): GradientKind {
  switch (type) {
    case 'GRADIENT_RADIAL':
      return 'radial';
    case 'GRADIENT_ANGULAR':
      return 'angular';
    case 'GRADIENT_DIAMOND':
      return 'diamond';
    default:
      return 'linear';
  }
}

function gradientStopRGBA(stop: GradientPaint['gradientStops'][number], opacity: number | undefined): RGBA {
  const a = stop.color.a * (typeof opacity === 'number' ? opacity : 1);
  return [stop.color.r, stop.color.g, stop.color.b, a];
}

function sameRGBA(a: RGBA, b: RGBA): boolean {
  const eps = 0.0005;
  return Math.abs(a[0] - b[0]) <= eps
    && Math.abs(a[1] - b[1]) <= eps
    && Math.abs(a[2] - b[2]) <= eps
    && Math.abs(a[3] - b[3]) <= eps;
}

function singleColorGradient(stops: GradientPaint['gradientStops'] | undefined, opacity: number | undefined): RGBA | undefined {
  if (!stops || stops.length === 0) return undefined;
  const first = gradientStopRGBA(stops[0], opacity);
  return stops.every((s) => sameRGBA(first, gradientStopRGBA(s, opacity))) ? first : undefined;
}

function paintToFill(paint: Paint, options: ExportOptions): Fill | undefined {
  if (paint.type === 'SOLID') {
    return { kind: 'solid', color: toRGBA(paint.color, paint.opacity) };
  }
  if (paint.type.startsWith('GRADIENT') && options.emitGradients) {
    const g = paint as GradientPaint;
    const solid = singleColorGradient(g.gradientStops, paint.opacity);
    if (solid) return { kind: 'solid', color: solid };
    return {
      kind: 'gradient',
      gradient: gradientKind(paint.type),
      stops: g.gradientStops.map((s) => ({
        position: s.position,
        color: gradientStopRGBA(s, paint.opacity),
      })),
      transform: g.gradientTransform ? ([] as number[]).concat(...g.gradientTransform) : undefined,
    };
  }
  return undefined;
}

function extractFills(node: SceneNode, options: ExportOptions): Fill[] {
  const fills = (node as unknown as { fills?: Paint[] | symbol }).fills;
  if (!Array.isArray(fills)) return [];
  return fills.map((f) => isEmptyPaint(f) ? undefined : paintToFill(f, options)).filter((f): f is Fill => !!f);
}

function firstFill(node: SceneNode, options: ExportOptions): Fill | undefined {
  return extractFills(node, options)[0];
}

function extractStrokes(node: SceneNode, options: ExportOptions): Stroke[] {
  if (!hasVisibleStroke(node)) return [];
  const strokes = (node as unknown as { strokes?: Paint[] }).strokes || [];
  const weight = (node as unknown as { strokeWeight?: number }).strokeWeight ?? 1;
  const alignRaw = (node as unknown as { strokeAlign?: string }).strokeAlign || 'CENTER';
  const dashes = (node as unknown as { dashPattern?: number[] }).dashPattern || [];
  const align = alignRaw === 'INSIDE' ? 'inside' : alignRaw === 'OUTSIDE' ? 'outside' : 'center';
  return strokes
    .map((p) => isEmptyPaint(p) ? undefined : paintToFill(p, options))
    .filter((f): f is Fill => !!f)
    .map((fill) => ({
      color: fill.kind === 'solid' ? fill.color : fill.kind === 'gradient' && fill.stops.length ? fill.stops[0].color : [0, 0, 0, 1] as RGBA,
      fill,
      weight: typeof weight === 'number' ? weight : 1,
      align,
      dashed: dashes.length > 0,
      dashPattern: dashes.map((v) => Number.isFinite(v) ? Math.max(0, v) : 0),
    }));
}

function extractStroke(node: SceneNode, options: ExportOptions): Stroke | undefined {
  return extractStrokes(node, options)[0];
}

function nodeBlendMode(node: SceneNode): string {
  const raw = (node as unknown as { blendMode?: string }).blendMode || 'NORMAL';
  return raw.toLowerCase().replace(/_([a-z])/g, (_, c: string) => c.toUpperCase());
}

function cornerData(node: SceneNode): { radius: number; corners?: [number, number, number, number] } {
  const n = node as unknown as {
    cornerRadius?: number | symbol;
    topLeftRadius?: number;
    topRightRadius?: number;
    bottomRightRadius?: number;
    bottomLeftRadius?: number;
  };
  if (typeof n.cornerRadius === 'number') return { radius: n.cornerRadius };
  const tl = n.topLeftRadius ?? 0;
  const tr = n.topRightRadius ?? 0;
  const br = n.bottomRightRadius ?? 0;
  const bl = n.bottomLeftRadius ?? 0;
  return { radius: Math.max(tl, tr, br, bl), corners: [tl, tr, br, bl] };
}

// Visible Figma effects on a node → procedural effect data. Kept in the manifest
// field named `shadows` for compatibility with older imports.
function extractShadows(node: SceneNode): Shadow[] {
  const effects = (node as unknown as { effects?: readonly Effect[] }).effects;
  if (!Array.isArray(effects)) return [];
  const out: Shadow[] = [];
  for (const e of effects) {
    if (e.visible === false) continue;
    if (e.type === 'DROP_SHADOW' || e.type === 'INNER_SHADOW') {
      const ds = e as DropShadowEffect & { spread?: number };
      out.push({
        kind: e.type === 'INNER_SHADOW' ? 'innerShadow' : 'dropShadow',
        color: [ds.color.r, ds.color.g, ds.color.b, ds.color.a],
        offsetX: ds.offset?.x ?? 0,
        offsetY: ds.offset?.y ?? 0,
        blur: ds.radius ?? 0,
        spread: ds.spread ?? 0,
        inner: e.type === 'INNER_SHADOW',
      });
      continue;
    }
    if (e.type === 'LAYER_BLUR') {
      const lb = e as unknown as {
        radius?: number;
        start?: number;
        end?: number;
        startRadius?: number;
        endRadius?: number;
        blurStart?: number;
        blurEnd?: number;
        mode?: string;
        blurType?: string;
      };
      const start = lb.startRadius ?? lb.blurStart ?? lb.start ?? lb.radius ?? 0;
      const end = lb.endRadius ?? lb.blurEnd ?? lb.end ?? lb.radius ?? start;
      const mode = `${lb.mode ?? lb.blurType ?? ''}`.toLowerCase();
      const progressive = mode.includes('progress') || Math.abs(end - start) > 0.001;
      out.push({
        kind: 'layerBlur',
        color: [0, 0, 0, 0],
        offsetX: 0,
        offsetY: 0,
        blur: lb.radius ?? start,
        spread: 0,
        inner: false,
        blurMode: progressive ? 'progressive' : 'uniform',
        startBlur: start,
        endBlur: end,
      });
    }
  }
  return out;
}

function buildStyle(node: SceneNode, options: ExportOptions, hasAsset: boolean): Style | undefined {
  const opacity = (node as unknown as { opacity?: number }).opacity ?? 1;
  const blendMode = nodeBlendMode(node);
  const fills = extractFills(node, options);
  const fill = fills[0];
  const strokes = options.rasterizeStrokes ? [] : extractStrokes(node, options);
  const stroke = strokes[0];
  const { radius, corners } = cornerData(node);
  const effects = extractShadows(node);

  // No real fill → transparent, NOT opaque white. Fabricated white is the cause
  // of stray white boxes on fill-less styled containers. (FigmaTest fix.) A shadow
  // also needs a (transparent) SDF panel to render against.
  const resolvedFill: Fill | undefined =
    fill || (!hasAsset && (stroke || radius > 0 || effects.length > 0) ? { kind: 'solid', color: [0, 0, 0, 0] } : fill);

  if (!resolvedFill && !stroke && radius === 0 && opacity === 1 && effects.length === 0) return undefined;
  return {
    opacity,
    blendMode,
    cornerRadius: radius,
    corners,
    fill: resolvedFill,
    fills: fills.length ? fills : undefined,
    stroke,
    strokes: strokes.length ? strokes : undefined,
    effects: effects.length ? effects : undefined,
  };
}

function alignH(v: string | undefined): TextProps['alignH'] {
  switch (v) {
    case 'CENTER':
      return 'center';
    case 'RIGHT':
      return 'right';
    case 'JUSTIFIED':
      return 'justified';
    default:
      return 'left';
  }
}
function alignV(v: string | undefined): TextProps['alignV'] {
  switch (v) {
    case 'CENTER':
      return 'middle';
    case 'BOTTOM':
      return 'bottom';
    default:
      return 'top';
  }
}

function buildText(node: TextNode): TextProps {
  const fontName = node.fontName;
  const family = fontName !== figma.mixed ? fontName.family : 'Inter';
  const style = fontName !== figma.mixed ? fontName.style : 'Regular';
  const size = node.fontSize !== figma.mixed ? node.fontSize : 16;
  const fills = Array.isArray(node.fills) ? node.fills : [];
  const solid = fills.find((f) => !isEmptyPaint(f) && f.type === 'SOLID') as SolidPaint | undefined;
  const lh = node.lineHeight !== figma.mixed && node.lineHeight.unit !== 'AUTO'
    ? (node.lineHeight as { value: number }).value
    : undefined;
  const ls = node.letterSpacing !== figma.mixed ? node.letterSpacing.value : undefined;
  // A stroke on the text layer → a TMP outline (color + px weight).
  const strokes = Array.isArray(node.strokes) ? node.strokes : [];
  const strokePaint = strokes.find((f) => !isEmptyPaint(f) && f.type === 'SOLID') as SolidPaint | undefined;
  const strokeWeight = typeof node.strokeWeight === 'number' ? node.strokeWeight : 0;
  const outline = strokePaint && strokeWeight > 0
    ? { color: toRGBA(strokePaint.color, strokePaint.opacity), weight: strokeWeight }
    : undefined;
  return {
    content: node.characters,
    fontFamily: family,
    fontStyle: style,
    fontSize: size,
    color: solid ? toRGBA(solid.color, solid.opacity) : [0, 0, 0, 1],
    alignH: alignH(node.textAlignHorizontal),
    alignV: alignV(node.textAlignVertical),
    lineHeight: lh,
    letterSpacing: ls,
    autoResize: node.textAutoResize,
    outline,
  };
}

// ---------------------------------------------------------------------------
// PNG dimensions (IHDR) + FNV-1a dedup hash
// ---------------------------------------------------------------------------
function pngSize(bytes: Uint8Array): { w: number; h: number } {
  if (bytes.length < 24) return { w: 0, h: 0 };
  const dv = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  return { w: dv.getUint32(16), h: dv.getUint32(20) };
}
function fnv1a(bytes: Uint8Array): string {
  let h = 0x811c9dc5;
  for (let i = 0; i < bytes.length; i++) {
    h ^= bytes[i];
    h = (h + ((h << 1) + (h << 4) + (h << 7) + (h << 8) + (h << 24))) >>> 0;
  }
  return h.toString(16);
}

function exportConstraint(scale: ExportScale): ExportSettingsImage['constraint'] {
  switch (scale.type) {
    case 'width':
      return { type: 'WIDTH', value: scale.value };
    case 'height':
      return { type: 'HEIGHT', value: scale.value };
    default:
      return { type: 'SCALE', value: scale.value };
  }
}
function scaleNumber(scale: ExportScale, frameW: number, frameH: number): number {
  if (scale.type === 'width') return frameW > 0 ? scale.value / frameW : 1;
  if (scale.type === 'height') return frameH > 0 ? scale.value / frameH : 1;
  return scale.value;
}

interface Plan {
  node: SceneNode;
  parentId: string | null;
  merged: boolean;
  canonicalRef: CanonicalRef | null;
  exportable: boolean;
  children: SceneNode[];
}

const VECTOR_SHAPE_TYPES = new Set<string>([
  'VECTOR',
  'BOOLEAN_OPERATION',
  'LINE',
  'ELLIPSE',
  'POLYGON',
  'STAR',
]);

const ICON_CONTAINER_TYPES = new Set<string>(['GROUP', 'FRAME', 'COMPONENT', 'INSTANCE']);

// True when `node` is a container whose subtree is purely vector-ish glyph content
// (e.g. a multi-path checkmark or two-tone arrow grouped under a FRAME/GROUP): at
// least one vector leaf, and every leaf is a vector type — no TEXT, no rectangles
// or other rasterized leaves. Such a container can't be rebuilt as a rounded rect,
// so it rasterizes to a single PNG. A vector node IS a leaf here (don't descend
// into a BOOLEAN_OPERATION's operands).
function isIconOnlyContainer(node: SceneNode): boolean {
  if (!('children' in node) || !ICON_CONTAINER_TYPES.has(node.type)) return false;
  let vectorLeaves = 0;
  let ok = true;
  const walk = (n: SceneNode): void => {
    if (!ok) return;
    if (n.type === 'TEXT') { ok = false; return; }
    if (VECTOR_SHAPE_TYPES.has(n.type)) { vectorLeaves++; return; }
    if ('children' in n) {
      const kids = (n as ChildrenMixin).children as SceneNode[];
      if (kids.length === 0) { ok = false; return; } // empty non-vector wrapper → not an icon
      for (const c of kids) walk(c);
      return;
    }
    ok = false; // a non-vector leaf (RECTANGLE, etc.) → not purely vector
  };
  for (const c of (node as ChildrenMixin).children as SceneNode[]) walk(c);
  return ok && vectorLeaves > 0;
}

function interactive(name: string): boolean {
  const n = name.toLowerCase();
  return INTERACTIVE_HINTS.some((h) => n.includes(h));
}

function firstTextLabel(node: SceneNode): string | undefined {
  if (node.type === 'TEXT') return (node as TextNode).characters;
  if ('children' in node) {
    for (const c of (node as ChildrenMixin).children) {
      const l = firstTextLabel(c);
      if (l) return l;
    }
  }
  return undefined;
}

function childByExactName(node: SceneNode, name: string): SceneNode | undefined {
  return 'children' in node
    ? ((node as ChildrenMixin).children as SceneNode[]).find((c) => c.name.toLowerCase() === name.toLowerCase())
    : undefined;
}

function firstTextUnderNamedChild(node: SceneNode, names: string[]): string | undefined {
  for (const name of names) {
    const child = childByExactName(node, name);
    const text = child ? firstTextLabel(child) : undefined;
    if (text !== undefined) return text;
  }
  return undefined;
}

function inputValueText(node: SceneNode): string | undefined {
  const value = firstTextUnderNamedChild(node, ['Text', 'Value']);
  if (value === undefined) return undefined;
  return value.trim().length === 0 ? '' : value;
}

/** First TEXT node under a node — used to read a canonical instance's label font. */
function firstTextNode(node: SceneNode): TextNode | undefined {
  if (node.type === 'TEXT') return node as TextNode;
  if ('children' in node) {
    for (const c of (node as ChildrenMixin).children) {
      const n = firstTextNode(c);
      if (n) return n;
    }
  }
  return undefined;
}

/** All text strings under a node — heuristic source of dropdown options. */
function gatherTexts(node: SceneNode): string[] {
  const out: string[] = [];
  const walk = (n: SceneNode) => {
    if (n.type === 'TEXT') {
      const t = (n as TextNode).characters.trim();
      if (t) out.push(t);
    }
    if ('children' in n) for (const c of (n as ChildrenMixin).children) walk(c);
  };
  if ('children' in node) for (const c of (node as ChildrenMixin).children) walk(c);
  return out;
}

/** Initial control state from instance variant properties, where detectable. */
function canonicalValue(node: SceneNode, kind: CanonicalKind): string | undefined {
  if (kind === 'input') return inputValueText(node);
  if (kind === 'toggle') {
    const props = (node as unknown as {
      componentProperties?: Record<string, { value: unknown }>;
    }).componentProperties;
    if (props) {
      for (const k of Object.keys(props)) {
        const kl = k.toLowerCase();
        if (kl.includes('state') || kl.includes('check') || kl.includes('select') || kl === 'on') {
          const v = String(props[k].value).toLowerCase();
          if (['on', 'true', 'checked', 'selected'].includes(v)) return 'true';
          if (['off', 'false', 'unchecked'].includes(v)) return 'false';
        }
      }
    }
  }
  return undefined;
}

function buildCanonical(ref: CanonicalRef | null, node: SceneNode): CanonicalRef | undefined {
  if (!ref) return undefined;
  const textLabel = firstTextLabel(node);
  const c: CanonicalRef = {
    kind: ref.kind,
    ref: ref.ref,
    instanceName: ref.instanceName,
    label: textLabel || ref.instanceName,
  };
  if (ref.kind === 'input') {
    const placeholder = firstTextUnderNamedChild(node, ['Placeholder', 'Label']) || textLabel;
    if (placeholder !== undefined) {
      c.placeholder = placeholder;
      c.label = placeholder;
    }
  }
  const value = canonicalValue(node, ref.kind);
  if (value !== undefined) c.value = value;
  if (ref.kind === 'dropdown') {
    if (c.value === undefined && textLabel) c.value = textLabel;
    const opts = gatherTexts(node);
    if (opts.length) c.options = opts;
  }
  // This instance's label font (used as a per-instance override when it differs).
  const labelNode = firstTextNode(node);
  if (labelNode && labelNode.fontName !== figma.mixed) {
    const fn = labelNode.fontName as FontName;
    c.labelFont = { family: fn.family, style: fn.style };
  }
  if (labelNode && typeof labelNode.fontSize === 'number') {
    c.labelFontSize = labelNode.fontSize;
  }
  // The canonical COMPONENT's label font — the generated prefab/definition uses
  // this, so the prefab mirrors the component (not whatever an instance overrode).
  const comp = node.type === 'INSTANCE' ? (node as InstanceNode).mainComponent : null;
  const defNode = comp ? firstTextNode(comp) : (node.type === 'COMPONENT' ? labelNode : undefined);
  if (defNode && defNode.fontName !== figma.mixed) {
    const fn = defNode.fontName as FontName;
    c.defLabelFont = { family: fn.family, style: fn.style };
  }
  if (defNode && typeof defNode.fontSize === 'number') {
    c.defLabelFontSize = defNode.fontSize;
  }
  return c;
}

/** Figma prototype "Navigate to" reaction → nav data (no behaviour). */
function navFor(node: SceneNode): NavLink | undefined {
  const reactions = (node as unknown as { reactions?: any[] }).reactions;
  if (!Array.isArray(reactions)) return undefined;
  for (const r of reactions) {
    const actions = Array.isArray(r.actions) ? r.actions : r.action ? [r.action] : [];
    for (const a of actions) {
      if (a && a.type === 'NODE' && a.destinationId && (!a.navigation || a.navigation === 'NAVIGATE')) {
        const dest = figma.getNodeById(a.destinationId);
        if (dest) {
          const trig = r.trigger && r.trigger.type === 'ON_CLICK' ? 'click' : 'click';
          return { target: sanitize(dest.name), trigger: trig };
        }
      }
    }
  }
  return undefined;
}

/**
 * Build the manifest + PNG assets for a selected root node.
 */
export async function exportDesign(
  root: SceneNode,
  scale: ExportScale,
  options: ExportOptions = DEFAULT_EXPORT_OPTIONS,
  excludedIds: Set<string> = new Set(),
  mergedIds: Set<string> = new Set(),
  forcedPngIds: Set<string> = new Set(),
  onProgress?: ProgressFn
): Promise<ExportResult> {
  const frameW = (root as unknown as { width: number }).width;
  const frameH = (root as unknown as { height: number }).height;
  const scaleNum = scaleNumber(scale, frameW, frameH);
  const fontFaceDilate = clampNumber(options.fontFaceDilate, 0, 1, DEFAULT_FONT_FACE_DILATE);

  // ---- 1. Plan the tree (which nodes become elements, which merge/rasterize) -
  const plans: Plan[] = [];
  const planById = new Map<string, Plan>();

  function plan(node: SceneNode, parentId: string | null, insideMerge: boolean) {
    if ((node as unknown as { visible?: boolean }).visible === false) return;
    if (excludedIds.has(node.id)) return;

    const canonicalRef = detectCanonical(node);
    const isMergeRoot =
      !insideMerge &&
      (mergedIds.has(node.id) ||
        (options.autoMerge && (node as unknown as { locked?: boolean }).locked === true));

    // Inside a merge, descendants are flattened into the merge root's PNG.
    if (insideMerge) return;

    const forced = forcedPngIds.has(node.id);
    const exportable = !canonicalRef && (forced || isExportable(node) || isMergeRoot);

    const children: SceneNode[] =
      !canonicalRef && !isMergeRoot && 'children' in node
        ? (node as ChildrenMixin).children.slice() as SceneNode[]
        : [];

    const p: Plan = { node, parentId, merged: isMergeRoot, canonicalRef, exportable, children };
    plans.push(p);
    planById.set(node.id, p);

    for (const c of children) plan(c, node.id, isMergeRoot);
  }
  plan(root, null, false);

  // ---- 2. Rasterize exportable nodes (hide siblings/descendants to isolate) --
  const assets: { name: string; data: number[] }[] = [];
  const assetEntries: ManifestAsset[] = [];
  const hashToFile = new Map<string, string>();
  const assetByNode = new Map<string, { file: string; w: number; h: number }>();
  const failedExportIds = new Set<string>();

  const exportPlans = plans.filter((p) => p.exportable);
  let done = 0;

  for (const p of exportPlans) {
    onProgress?.(done++, exportPlans.length, sanitize(p.node.name));

    // Hide exportable descendants so they don't double-render into this PNG.
    const hidden: SceneNode[] = [];
    if (!p.merged) {
      for (const other of plans) {
        if (other === p || !other.exportable) continue;
        if (isDescendant(other.node, p.node)) {
          if ((other.node as unknown as { visible?: boolean }).visible !== false) {
            hidden.push(other.node);
          }
        }
      }
    }
    const restore = hidden.map((n) => {
      const before = (n as unknown as { visible: boolean }).visible;
      (n as unknown as { visible: boolean }).visible = false;
      return { n, before };
    });

    try {
      const bytes = await (p.node as unknown as {
        exportAsync: (s: ExportSettings) => Promise<Uint8Array>;
      }).exportAsync({ format: 'PNG', constraint: exportConstraint(scale) });

      const hash = fnv1a(bytes);
      let file = hashToFile.get(hash);
      const dims = pngSize(bytes);
      if (!file) {
        file = generateFileName(root.name, p.node.name, scaleNum);
        // disambiguate collisions
        let n = 1;
        while (assets.some((a) => a.name === file)) file = file.replace('.png', `_${n++}.png`);
        hashToFile.set(hash, file);
        assets.push({ name: file, data: Array.from(bytes) });
        assetEntries.push({ file, nodeId: p.node.id, scale: scaleNum });
      }
      assetByNode.set(p.node.id, { file, w: dims.w, h: dims.h });
    } catch {
      // Container whose children were all hidden → empty render → exportAsync
      // throws. Drop the dangling asset so Unity uses style.fill instead of a
      // missing sprite (white box). (FigmaTest fix.)
      failedExportIds.add(p.node.id);
    } finally {
      for (const r of restore) (r.n as unknown as { visible: boolean }).visible = r.before;
    }
  }

  // ---- 2b. Render canonical Button states (Regular/Rollover/Pressed) ---------
  // The states live as named layers on the master component; Rollover/Pressed
  // are usually hidden, so we toggle each visible just for its own export.
  const STATE_LAYERS: [keyof CanonicalStates, string][] = [
    ['normal', 'regular'],
    ['highlighted', 'rollover'],
    ['pressed', 'pressed'],
  ];
  async function exportStates(master: SceneNode): Promise<CanonicalStates | undefined> {
    if (!('children' in master)) return undefined;
    const kids = (master as ChildrenMixin).children as SceneNode[];
    const out: CanonicalStates = {};
    for (const [key, layerName] of STATE_LAYERS) {
      const layer = kids.find((c) => c.name.toLowerCase() === layerName);
      if (!layer || !('exportAsync' in layer)) continue;
      const prev = (layer as unknown as { visible: boolean }).visible;
      (layer as unknown as { visible: boolean }).visible = true;
      try {
        const bytes = await (layer as unknown as {
          exportAsync: (s: ExportSettings) => Promise<Uint8Array>;
        }).exportAsync({ format: 'PNG', constraint: exportConstraint(scale) });
        const hash = fnv1a(bytes);
        let file = hashToFile.get(hash);
        if (!file) {
          file = generateFileName(root.name, `${master.name}_${layerName}`, scaleNum);
          let n = 1;
          while (assets.some((a) => a.name === file)) file = file.replace('.png', `_${n++}.png`);
          hashToFile.set(hash, file);
          assets.push({ name: file, data: Array.from(bytes) });
          assetEntries.push({ file, nodeId: layer.id, scale: scaleNum });
        }
        out[key] = file;
      } catch {
        /* skip unrenderable state */
      } finally {
        (layer as unknown as { visible: boolean }).visible = prev;
      }
    }
    return out.normal || out.highlighted || out.pressed ? out : undefined;
  }

  async function exportNodeAsset(node: SceneNode, nameHint: string): Promise<string | undefined> {
    if (!('exportAsync' in node)) return undefined;
    // A hidden node (e.g. a default-off Checkmark, or a vector inside a hidden
    // Rollover/Pressed state) exports as a blank PNG unless it is made visible
    // first — same reason exportStates/exportCompositeState toggle visibility.
    const vis = node as unknown as { visible?: boolean };
    const prevVisible = vis.visible;
    if (prevVisible === false) vis.visible = true;
    try {
      const bytes = await (node as unknown as {
        exportAsync: (s: ExportSettings) => Promise<Uint8Array>;
      }).exportAsync({ format: 'PNG', constraint: exportConstraint(scale) });
      const hash = fnv1a(bytes);
      let file = hashToFile.get(hash);
      if (!file) {
        file = generateFileName(root.name, nameHint, scaleNum);
        let n = 1;
        while (assets.some((a) => a.name === file)) file = file.replace('.png', `_${n++}.png`);
        hashToFile.set(hash, file);
        assets.push({ name: file, data: Array.from(bytes) });
        assetEntries.push({ file, nodeId: node.id, scale: scaleNum });
      }
      return file;
    } catch {
      return undefined;
    } finally {
      if (prevVisible === false) vis.visible = prevVisible;
    }
  }

  async function exportCompositeState(container: SceneNode, stateName?: string): Promise<string | undefined> {
    if (!('children' in container)) return exportNodeAsset(container, `${container.name}_${stateName || 'asset'}`);
    const kids = (container as ChildrenMixin).children as SceneNode[];
    const stateNames = new Set(['regular', 'rollover', 'pressed']);
    const target = stateName ? kids.find((c) => c.name.toLowerCase() === stateName.toLowerCase()) : undefined;
    if (stateName && !target) return undefined;
    const restore = kids
      .filter((c) => stateNames.has(c.name.toLowerCase()))
      .map((c) => ({ node: c, visible: (c as unknown as { visible: boolean }).visible }));
    if (target) {
      for (const r of restore) (r.node as unknown as { visible: boolean }).visible = r.node.id === target.id;
    }
    try {
      return await exportNodeAsset(container, `${container.name}_${stateName || 'asset'}`);
    } finally {
      for (const r of restore) (r.node as unknown as { visible: boolean }).visible = r.visible;
    }
  }

  // First solid-fill colour of a node → RGBA (null if no solid fill).
  function solidRGBA(node: SceneNode): RGBA | null {
    const fills = (node as unknown as { fills?: Paint[] | symbol }).fills;
    if (!Array.isArray(fills)) return null;
    const s = fills.find((f) => !isEmptyPaint(f) && f.type === 'SOLID') as SolidPaint | undefined;
    return s ? toRGBA(s.color, s.opacity) : null;
  }

  // The SOLID colour a button STATE layer (rollover/pressed) renders as, honoring
  // overrides AND nesting: the layer's own solid fill, else the first descendant
  // shape's solid fill — a state is often a frame wrapping the coloured background,
  // so reading only the layer's own fill grabs the (often default) frame colour
  // instead of the real one. Called on an INSTANCE's state layer it picks up that
  // instance's overridden colour. null = no solid fill (e.g. a gradient state).
  function stateSolid(node: SceneNode): RGBA | null {
    const own = solidRGBA(node);
    if (own) return own;
    if ('children' in node) {
      for (const c of (node as ChildrenMixin).children as SceneNode[]) {
        const f = stateSolid(c);
        if (f) return f;
      }
    }
    return null;
  }

  // The renderable fill of a button state layer: a solid colour, or a Figma
  // gradient with any number of stops (the SDF shader samples a generated ramp).
  // Other gradients / image fills → null, so the button keeps the exported-PNG path.
  function shapeFill(node: SceneNode): { fill: RGBA; gradient?: Extract<Fill, { kind: 'gradient' }> } | null {
    const fills = (node as unknown as { fills?: Paint[] | symbol }).fills;
    if (!Array.isArray(fills)) return null;
    const paint = fills.find((f) => !isEmptyPaint(f));
    if (!paint) return null;
    if (paint.type === 'SOLID') {
      const s = paint as SolidPaint;
      return { fill: toRGBA(s.color, s.opacity) };
    }
    if (paint.type.startsWith('GRADIENT') && options.emitGradients) {
      const g = paint as GradientPaint;
      const stops = g.gradientStops;
      if (stops.length >= 2) {
        const solid = singleColorGradient(stops, paint.opacity);
        if (solid) return { fill: solid };
        return {
          fill: gradientStopRGBA(stops[0], paint.opacity),
          gradient: {
            kind: 'gradient',
            gradient: gradientKind(paint.type),
            stops: stops.map((s) => ({
              position: s.position,
              color: gradientStopRGBA(s, paint.opacity),
            })),
            transform: g.gradientTransform ? ([] as number[]).concat(...g.gradientTransform) : undefined,
          },
        };
      }
    }
    return null;
  }

  // Human-readable reason a 'regular' layer's fill can't drive the SDF shader —
  // surfaced as a Figma toast so a PNG-fallback button is debuggable at a glance.
  function fillDiag(node: SceneNode): string {
    const fills = (node as unknown as { fills?: Paint[] | symbol }).fills;
    if (!Array.isArray(fills)) return 'no fills array (is it a frame/group?)';
    const paint = fills.find((f) => !isEmptyPaint(f));
    if (!paint) return 'no visible fill';
    if (paint.type.startsWith('GRADIENT')) {
      const n = ((paint as GradientPaint).gradientStops || []).length;
      return `${paint.type} (${n} stop${n === 1 ? '' : 's'})`;
    }
    return paint.type;
  }

  const shapeDiag: string[] = []; // why a button fell back to PNG (surfaced as a toast)

  // Capture a procedural ButtonShape from ONE Figma node: its own rounded rect
  // geometry, paint stack, stroke stack, and effect stack. This intentionally does
  // not borrow from siblings/parents for canonical button states; each Figma node
  // owns its own visual stack.
  // Shared by every canonical control (button background, toggle box, list row, …).
  function shapeOf(node: SceneNode): ButtonShape | null {
    const fills = extractFills(node, options);
    const sf = shapeFill(node);
    const opacity = (node as unknown as { opacity?: number }).opacity ?? 1;
    const blendMode = nodeBlendMode(node);
    const radius = typeof (node as unknown as { cornerRadius?: number }).cornerRadius === 'number'
      ? (node as unknown as { cornerRadius: number }).cornerRadius : 0;
    const ownEffects = extractShadows(node);
    const strokesAll = options.rasterizeStrokes ? [] : extractStrokes(node, options);
    if (!sf && fills.length === 0 && strokesAll.length === 0 && ownEffects.length === 0 && radius <= 0) return null;
    const shape: ButtonShape = { cornerRadius: radius, opacity, blendMode, fill: sf?.fill ?? [0, 0, 0, 0] };
    if (sf?.gradient) shape.gradient = sf.gradient;
    if (fills.length) shape.fills = fills;
    if (strokesAll.length) {
      shape.strokes = strokesAll;
      shape.stroke = strokesAll[0];
    }
    if (ownEffects.length) shape.effects = ownEffects;
    return shape;
  }

  async function shapeOfWithAsset(node: SceneNode): Promise<ButtonShape | null> {
    let shape = shapeOf(node);
    if (shape && VECTOR_SHAPE_TYPES.has(node.type)) {
      // Crisp procedural mesh when the path geometry is representable; the PNG is
      // still exported as the fallback Unity uses if the mesh is absent.
      const vector = buildVectorDrawing(node);
      if (vector) shape.vector = vector;
      const asset = await exportNodeAsset(node, `${node.name}_shape`);
      if (asset) shape.asset = asset;
      return shape;
    }
    // An icon-only container (multi-path glyph grouped under a FRAME/GROUP) can't be
    // rebuilt as a rounded rect — rasterize the WHOLE subtree as one sprite instead
    // of letting stateShape grab only its first vector child. Skip when the container
    // carries its own paint or corner radius: that's a styled panel, keep it procedural.
    if (isIconOnlyContainer(node) && !hasVisualPaint(shape) && !((shape?.cornerRadius ?? 0) > 0.001)) {
      const asset = await exportNodeAsset(node, `${node.name}_icon`);
      if (asset) {
        if (!shape) {
          const opacity = (node as unknown as { opacity?: number }).opacity ?? 1;
          shape = { cornerRadius: 0, opacity, blendMode: nodeBlendMode(node), fill: [0, 0, 0, 0] };
        }
        shape.asset = asset;
      }
    }
    return shape;
  }

  function mergeShapeFallbacks(shape: ButtonShape | null, fallbacks: SceneNode[]): ButtonShape | null {
    if (!shape) return null;
    for (const src of fallbacks) {
      const radius = typeof (src as unknown as { cornerRadius?: number }).cornerRadius === 'number'
        ? (src as unknown as { cornerRadius: number }).cornerRadius
        : 0;
      if (shape.cornerRadius === 0 && radius > 0) shape.cornerRadius = radius;

      if (!shape.stroke) {
        const strokes = extractStrokes(src, options);
        if (strokes.length) {
          shape.stroke = strokes[0];
          shape.strokes = strokes;
        }
      }

      if (!shape.effects) {
        const effects = extractShadows(src);
        if (effects.length) shape.effects = effects;
      }
    }
    return shape;
  }

  function visualShapeCandidate(node: SceneNode): boolean {
    return node.type !== 'TEXT' && node.type !== 'SLICE';
  }

  async function stateShape(node: SceneNode): Promise<ButtonShape | null> {
    if (visualShapeCandidate(node)) {
      const own = await shapeOfWithAsset(node);
      if (own) return own;
    }
    if ('children' in node) {
      const kids = (node as ChildrenMixin).children as SceneNode[];
      for (const c of kids) {
        if (c.name.toLowerCase() === 'label' || c.name.toLowerCase() === 'hitarea') continue;
        const sh = await stateShape(c);
        if (sh) return sh;
      }
    }
    return null;
  }

  function sameShadow(a: Shadow | undefined, b: Shadow | undefined): boolean {
    if (!a || !b) return false;
    return JSON.stringify(a) === JSON.stringify(b);
  }

  function stripRootShadowFromState(state: ButtonShape | null, root: ButtonShape | null): ButtonShape | null {
    if (!state || !root) return state;
    const rootShadow = root.effects?.[0];
    if (!rootShadow) return state;
    const stateShadow = state.effects?.[0];
    if (!sameShadow(stateShadow, rootShadow)) return state;
    if (state.effects && state.effects.every((s) => sameShadow(s, rootShadow))) delete state.effects;
    return state;
  }

  function visibleColor(rgba: RGBA | undefined): boolean {
    return !!rgba && rgba[3] > 0.001;
  }

  function visibleFill(fill: Fill | undefined): boolean {
    if (!fill) return false;
    if (fill.kind === 'image') return true;
    if (fill.kind === 'gradient') return fill.stops.length === 0 || fill.stops.some((s) => visibleColor(s.color));
    return visibleColor(fill.color);
  }

  function visibleStroke(stroke: Stroke | undefined): boolean {
    return !!stroke && stroke.weight > 0.001 && (visibleFill(stroke.fill) || visibleColor(stroke.color));
  }

  function hasVisualPaint(shape: ButtonShape | null): boolean {
    return !!shape && (
      (shape.fills != null && shape.fills.some(visibleFill))
      || (shape.strokes != null && shape.strokes.some(visibleStroke))
      || visibleStroke(shape.stroke)
      || visibleColor(shape.fill)
    );
  }

  function rootShadowShape(root: ButtonShape | null, regular: ButtonShape): ButtonShape | null {
    if (!root) return null;
    const rootEffects = root.effects ?? [];
    if (!rootEffects.length) return root;
    if (hasVisualPaint(root) || root.cornerRadius > 0.001) return root;
    return {
      cornerRadius: regular.cornerRadius,
      opacity: root.opacity,
      blendMode: root.blendMode,
      effects: rootEffects,
    };
  }

  const childByName = (master: SceneNode, name: string): SceneNode | undefined =>
    'children' in master
      ? ((master as ChildrenMixin).children as SceneNode[]).find((c) => c.name.toLowerCase() === name.toLowerCase())
      : undefined;

  // Normalized Unity anchors [minX,minY,maxX,maxY] of named children within `master`
  // (Figma top-down → Unity bottom-up), so composite controls position precisely.
  function partsOf(master: SceneNode, names: string[]): Record<string, number[]> {
    const out: Record<string, number[]> = {};
    const W = (master as unknown as { width?: number }).width || 1;
    const H = (master as unknown as { height?: number }).height || 1;
    for (const name of names) {
      const c = childByName(master, name);
      if (!c) continue;
      const x = (c as unknown as { x?: number }).x ?? 0, y = (c as unknown as { y?: number }).y ?? 0;
      const w = (c as unknown as { width?: number }).width ?? 0, h = (c as unknown as { height?: number }).height ?? 0;
      out[name] = [x / W, 1 - (y + h) / H, (x + w) / W, 1 - y / H];
    }
    return out;
  }

  // Procedural background shape from a button master's state layers: solid OR
  // linear gradient (SDF shader). Other fills → null → exported-PNG path.
  async function captureButtonShape(master: SceneNode, silent = false) {
    if (!('children' in master)) return null;
    const kids = (master as ChildrenMixin).children as SceneNode[];
    const reg = kids.find((c) => c.name.toLowerCase() === 'regular');
    if (!reg) { if (!silent) shapeDiag.push(`'${master.name}': no layer named 'regular'`); return null; }
    const rawRootShape = shapeOf(master);
    const shape = stripRootShadowFromState(await stateShape(reg), rawRootShape);
    if (!shape) { if (!silent) shapeDiag.push(`'${master.name}': regular fill = ${fillDiag(reg)}`); return null; } // unsupported fill → PNG path
    const rootShape = rootShadowShape(rawRootShape, shape);
    const stateColors: { normal?: RGBA; highlighted?: RGBA; pressed?: RGBA } = { normal: shape.fill };
    const stateShapes: CanonicalStateShapes = { normal: shape };
    const ro = kids.find((c) => c.name.toLowerCase() === 'rollover');
    const roShape = stripRootShadowFromState(ro ? await stateShape(ro) : null, rootShape);
    const rc = roShape?.fill ?? (ro ? stateSolid(ro) : null);
    if (roShape) stateShapes.highlighted = roShape;
    if (rc) stateColors.highlighted = rc;
    const pr = kids.find((c) => c.name.toLowerCase() === 'pressed');
    const prShape = stripRootShadowFromState(pr ? await stateShape(pr) : null, rootShape);
    const pc = prShape?.fill ?? (pr ? stateSolid(pr) : null);
    if (prShape) stateShapes.pressed = prShape;
    if (pc) stateColors.pressed = pc;
    return { shape, rootShape, stateColors, stateShapes, parts: partsOf(master, ['Regular', 'RollOver', 'Pressed', 'HitArea', 'Label']) };
  }

  // Text content of a node (its own characters, or the first descendant text).
  function textOf(node: SceneNode | undefined): string | undefined {
    if (!node) return undefined;
    if (node.type === 'TEXT') return (node as TextNode).characters;
    if ('children' in node) {
      for (const c of (node as ChildrenMixin).children as SceneNode[]) { const t = textOf(c); if (t != null) return t; }
    }
    return undefined;
  }

  // Capture a toggle/radio: Background box (UGUI Toggle.targetGraphic), the Checkmark
  // shown when on (Toggle.graphic), the initial value, and the label.
  async function captureToggle(master: SceneNode, tagValue?: string) {
    const bg = childByName(master, 'Background');
    const shape = bg ? await shapeOfWithAsset(bg) : null;
    if (!shape) return null;
    const ckNode = childByName(master, 'Checkmark');
    const checkShape = ckNode ? await shapeOfWithAsset(ckNode) : undefined;
    const ckVisible = ckNode ? (ckNode as unknown as { visible?: boolean }).visible !== false : false;
    const value = tagValue === 'on' || tagValue === 'off' ? tagValue : (ckVisible ? 'on' : 'off');
    return { shape, checkShape: checkShape ?? undefined, value, label: textOf(childByName(master, 'Label')),
      // 'HitArea' (optional): if the component defines a HitArea layer, Unity uses it
      // as the clickable region; otherwise the whole component frame is clickable.
      parts: partsOf(master, ['Background', 'Checkmark', 'Label', 'HitArea']) };
  }

  // Capture an input field: Background (TMP_InputField.targetGraphic), Placeholder,
  // optional initial Text value, and normalized child layout.
  async function captureInput(master: SceneNode) {
    const bg = childByName(master, 'Background');
    const shape = bg ? await shapeOfWithAsset(bg) : null;
    const placeholder = textOf(childByName(master, 'Placeholder')) ?? textOf(childByName(master, 'Label'));
    const rawValue = textOf(childByName(master, 'Text')) ?? textOf(childByName(master, 'Value'));
    const value = rawValue !== undefined && rawValue.trim().length === 0 ? '' : rawValue;
    return {
      shape: shape ?? undefined,
      label: placeholder,
      placeholder,
      value,
      parts: partsOf(master, ['Background', 'Placeholder', 'Text', 'Value']),
    };
  }

  // Capture a dropdown: Background, the option list (each text in the 'Options' frame),
  // and the selected value.
  async function captureDropdown(master: SceneNode, tagValue?: string) {
    const bg = childByName(master, 'Background');
    const shape = bg ? await shapeOfWithAsset(bg) : null;
    const arrow = childByName(master, 'Arrow');
    const arrowReg = arrow ? childByName(arrow, 'Regular') : undefined;
    const arrowRoll = arrow ? childByName(arrow, 'Rollover') : undefined;
    const arrowPress = arrow ? childByName(arrow, 'Pressed') : undefined;
    const arrowColor = arrowReg ? (stateSolid(arrowReg) ?? undefined) : undefined;
    const arrowRollover = arrowRoll ? (stateSolid(arrowRoll) ?? undefined) : undefined;
    const arrowPressed = arrowPress ? (stateSolid(arrowPress) ?? undefined) : undefined;
    const arrowAsset = arrow ? await exportCompositeState(arrow, arrowReg ? 'Regular' : undefined) : undefined;
    const arrowRolloverAsset = arrow ? await exportCompositeState(arrow, 'Rollover') : undefined;
    const arrowPressedAsset = arrow ? await exportCompositeState(arrow, 'Pressed') : undefined;
    const bgRoll = childByName(master, 'BgRollover');
    const bgPress = childByName(master, 'BgPressed');
    const bgRollover = bgRoll ? (stateSolid(bgRoll) ?? undefined) : undefined;
    const bgPressed = bgPress ? (stateSolid(bgPress) ?? undefined) : undefined;
    const optsFrame = childByName(master, 'Options');
    const popupShape = (optsFrame ? shapeOf(optsFrame) : null) ?? shape ?? undefined;
    const optionNodes = optsFrame && 'children' in optsFrame
      ? ((optsFrame as ChildrenMixin).children as SceneNode[])
      : [];
    const options = optionNodes.map((c) => textOf(c)).filter((t): t is string => !!t);
    const optionInst = optionNodes.find((c) => c.name.toLowerCase() === 'option' && c.type === 'INSTANCE') as InstanceNode | undefined;
    const optionMaster =
      (optionInst && optionInst.mainComponent ? optionInst.mainComponent as SceneNode : null)
      ?? childByName(master, 'DropdownOption')
      ?? optionNodes.find((c) => c.name.toLowerCase() === 'dropdownoption');
    const optionSource = (optionInst as SceneNode | undefined) ?? optionMaster;
    const optionFallbacks = [optionSource, optionMaster].filter((n, i, arr): n is SceneNode => !!n && arr.findIndex((x) => x && x.id === n.id) === i);
    const optReg = optionSource
      ? (childByName(optionSource, 'Regular') ?? (optionMaster ? childByName(optionMaster, 'Regular') : undefined) ?? optionSource)
      : undefined;
    const optionShape = optReg ? (mergeShapeFallbacks(await shapeOfWithAsset(optReg), optionFallbacks) ?? undefined) : undefined;
    const optRoll = optionSource
      ? (childByName(optionSource, 'Rollover') ?? (optionMaster ? childByName(optionMaster, 'Rollover') : undefined))
      : undefined;
    const optPressed = optionSource
      ? (childByName(optionSource, 'Pressed') ?? (optionMaster ? childByName(optionMaster, 'Pressed') : undefined))
      : undefined;
    const optSelected = optionSource
      ? (childByName(optionSource, 'Selected') ?? (optionMaster ? childByName(optionMaster, 'Selected') : undefined))
      : undefined;
    const optionRolloverShape = optRoll ? (mergeShapeFallbacks(await shapeOfWithAsset(optRoll), optionFallbacks) ?? undefined) : undefined;
    const optionPressedShape = optPressed ? (mergeShapeFallbacks(await shapeOfWithAsset(optPressed), optionFallbacks) ?? undefined) : undefined;
    const optionSelectedShape = optSelected ? (mergeShapeFallbacks(await shapeOfWithAsset(optSelected), optionFallbacks) ?? undefined) : undefined;
    const optionRollover = optRoll ? (stateSolid(optRoll) ?? undefined) : undefined;
    const optionPressed = optPressed ? (stateSolid(optPressed) ?? undefined) : undefined;
    const optionSelected = optSelected ? (stateSolid(optSelected) ?? undefined) : undefined;
    const optionHeight =
      ((optionInst ?? optionNodes[0]) as unknown as { height?: number } | undefined)?.height
      ?? (optionMaster ? ((optionMaster as unknown as { height?: number }).height ?? undefined) : undefined);
    const value = tagValue || textOf(childByName(master, 'Label')) || options[0];
    return { shape: shape ?? undefined, options, value, label: textOf(childByName(master, 'Label')),
      optionShape, popupShape, optionRolloverShape, optionPressedShape, optionSelectedShape,
      optionRollover, optionPressed, optionHeight,
      optionSelected,
      arrowAsset, arrowRolloverAsset, arrowPressedAsset,
      arrowColor, arrowRollover, arrowPressed,
      bgRollover, bgPressed,
      parts: partsOf(master, ['Background', 'Label', 'Arrow']) };
  }

  // Capture a list: rounded Background + ONE 'Item' template row (Regular fill,
  // Rollover hover colour, Label). Row height comes from the template; the caller
  // derives the row COUNT from the placed list's height ÷ row height.
  async function captureList(master: SceneNode) {
    const bg = childByName(master, 'Background');
    const shape = bg ? await shapeOfWithAsset(bg) : undefined;
    const item = childByName(master, 'Item');
    if (!item) return null;
    const reg = childByName(item, 'Regular') ?? item;
    const itemShape = await shapeOfWithAsset(reg) ?? undefined;
    const rollNode = childByName(item, 'Rollover');
    const itemRollover = rollNode ? (stateSolid(rollNode) ?? undefined) : undefined;
    const itemHeight = (item as unknown as { height?: number }).height ?? 44;
    return { shape: shape ?? undefined, itemShape, itemRollover, itemHeight, label: textOf(childByName(item, 'Label')),
      parts: partsOf(master, ['Background']) };
  }

  const stateByNode = new Map<string, CanonicalStates>();
  const shapeByNode = new Map<string, Awaited<ReturnType<typeof captureButtonShape>>>();
  const instShapeByNode = new Map<string, ButtonShape>(); // per-instance shape override (differs from component)
  const instRootShapeByNode = new Map<string, ButtonShape>(); // per-instance root visual/effect override
  const instStateColorsByNode = new Map<string, { normal?: RGBA; highlighted?: RGBA; pressed?: RGBA }>(); // per-instance rollover/pressed override
  const instStateShapesByNode = new Map<string, CanonicalStateShapes>(); // per-instance full state-shape override
  const stateDiag: string[] = []; // what hover/press colour was captured, and from where (surfaced as a toast)
  const shadowCapDiag: string[] = []; // whether each button's drop shadow was captured (surfaced as a toast)
  const fmtC = (c?: RGBA) => (c ? `(${c.slice(0, 3).map((n) => Math.round(n * 255)).join(',')})` : 'none');
  // Captured control-specific data (toggle/radio/dropdown/list) merged into the canonical at assembly.
  const controlByNode = new Map<string, Partial<CanonicalRef>>();
  for (const p of plans) {
    if (!p.canonicalRef || p.canonicalRef.kind !== 'button') continue;
    const master =
      p.node.type === 'INSTANCE'
        ? ((p.node as InstanceNode).mainComponent as SceneNode | null)
        : p.node;
    if (!master) continue;
    const states = await exportStates(master);
    if (states) stateByNode.set(p.node.id, states);
    const sh = await captureButtonShape(master);
    if (sh) shapeByNode.set(p.node.id, sh);
    if (sh) {
      const shc = sh.shape.effects?.find((effect) => effect.kind === 'dropShadow' || (!effect.kind && !effect.inner));
      shadowCapDiag.push(shc
        ? `'${p.canonicalRef.ref}' shadow ${fmtC(shc.color)} off(${shc.offsetX},${shc.offsetY}) blur ${shc.blur} spread ${shc.spread}`
        : `'${p.canonicalRef.ref}' NO shadow found (root/regular/children)`);
    }
    // Per-instance override: read THIS instance's own 'regular'/'rollover'/'pressed'
    // layers (with overrides) and keep them when they differ from the component, so
    // an instance-level stroke/fill/corner OR hover/press colour tweak applies to
    // just that button (the canonical prefab keeps the component definition).
    if (sh && p.node.type === 'INSTANCE') {
      const inst = await captureButtonShape(p.node, true);
      if (inst && JSON.stringify(inst.shape) !== JSON.stringify(sh.shape)) instShapeByNode.set(p.node.id, inst.shape);
      if (inst && JSON.stringify(inst.rootShape) !== JSON.stringify(sh.rootShape) && inst.rootShape) {
        instRootShapeByNode.set(p.node.id, inst.rootShape);
      }
      if (inst && JSON.stringify(inst.stateShapes) !== JSON.stringify(sh.stateShapes)) {
        instStateShapesByNode.set(p.node.id, inst.stateShapes);
      }
      if (inst && JSON.stringify(inst.stateColors) !== JSON.stringify(sh.stateColors)) {
        const colorOverride = { ...inst.stateColors };
        if (inst.stateShapes.highlighted && JSON.stringify(inst.stateShapes.highlighted) !== JSON.stringify(sh.stateShapes.highlighted)) {
          delete colorOverride.highlighted;
        }
        if (inst.stateShapes.pressed && JSON.stringify(inst.stateShapes.pressed) !== JSON.stringify(sh.stateShapes.pressed)) {
          delete colorOverride.pressed;
        }
        if (colorOverride.normal || colorOverride.highlighted || colorOverride.pressed) {
          instStateColorsByNode.set(p.node.id, colorOverride);
        }
        stateDiag.push(`'${p.canonicalRef.ref}' hover ${fmtC(inst.stateColors.highlighted)}/press ${fmtC(inst.stateColors.pressed)} (instance override; component ${fmtC(sh.stateColors.highlighted)}/${fmtC(sh.stateColors.pressed)})`);
      } else if (sh) {
        stateDiag.push(`'${p.canonicalRef.ref}' hover ${fmtC(sh.stateColors.highlighted)}/press ${fmtC(sh.stateColors.pressed)} (component)`);
      }
    }
  }
  // Capture the non-button canonical controls (toggle/radio/dropdown/list).
  for (const p of plans) {
    const ref = p.canonicalRef;
    if (!ref || ref.kind === 'button') continue;
    const master = p.node.type === 'INSTANCE' ? ((p.node as InstanceNode).mainComponent as SceneNode | null) : p.node;
    if (!master) continue;
    if (ref.kind === 'toggle' || ref.kind === 'radio') {
      const t = await captureToggle(master, ref.value);
      if (t) controlByNode.set(p.node.id, { shape: t.shape, checkShape: t.checkShape, value: t.value, label: t.label, parts: t.parts });
    } else if (ref.kind === 'input') {
      const i = await captureInput(master);
      controlByNode.set(p.node.id, {
        shape: i.shape,
        label: i.label,
        placeholder: i.placeholder,
        value: i.value,
        parts: i.parts,
      });
    } else if (ref.kind === 'dropdown') {
      const d = await captureDropdown(master, ref.value);
      controlByNode.set(p.node.id, {
        shape: d.shape, options: d.options,
        optionShape: d.optionShape,
        popupShape: d.popupShape,
        optionRolloverShape: d.optionRolloverShape,
        optionPressedShape: d.optionPressedShape,
        optionSelectedShape: d.optionSelectedShape,
        optionRollover: d.optionRollover,
        optionPressed: d.optionPressed, optionSelected: d.optionSelected,
        optionHeight: d.optionHeight,
        arrowAsset: d.arrowAsset, arrowRolloverAsset: d.arrowRolloverAsset, arrowPressedAsset: d.arrowPressedAsset,
        arrowColor: d.arrowColor, arrowRollover: d.arrowRollover, arrowPressed: d.arrowPressed,
        bgRollover: d.bgRollover, bgPressed: d.bgPressed,
        parts: d.parts,
      });
    } else if (ref.kind === 'list') {
      const l = await captureList(master);
      if (l) {
        const ih = l.itemHeight || 44;
        const instH = (p.node as unknown as { height?: number }).height || ih;
        const count = Math.max(1, Math.round(instH / ih)); // resize the list = set its length
        controlByNode.set(p.node.id, { shape: l.shape, itemShape: l.itemShape, itemRollover: l.itemRollover, itemHeight: ih, count, label: l.label, parts: l.parts });
      }
    }
  }
  if (stateDiag.length) {
    // Surface exactly which hover/press colour was captured (and whether it came
    // from the instance or the component) so a mismatch with Figma is debuggable.
    // Prefer an instance-override line — that's the interesting/likely-wrong case.
    const head = stateDiag.find((d) => d.includes('override')) ?? stateDiag[0];
    try { figma.notify(`FigForge: ${head}`, { timeout: 8000 }); } catch { /* headless */ }
    for (const d of stateDiag) console.warn('[FigForge] button state:', d);
  }
  if (shapeDiag.length) {
    // Tell the designer exactly why a button used a baked PNG instead of the crisp shader.
    try { figma.notify(`FigForge: ${shapeDiag.length} button(s) → PNG. ${shapeDiag[0]}`, { timeout: 7000 }); } catch { /* headless */ }
    for (const d of shapeDiag) console.warn('[FigForge] button→PNG:', d);
  }
  if (shadowCapDiag.length) {
    // Surface whether each button's drop shadow was captured (and its params) so a
    // missing shadow is debuggable at a glance. Prefer a "found" line in the toast.
    const head = shadowCapDiag.find((d) => !d.includes('NO shadow')) ?? shadowCapDiag[0];
    try { figma.notify(`FigForge shadow: ${head}`, { timeout: 8000 }); } catch { /* headless */ }
    for (const d of shadowCapDiag) console.warn('[FigForge] shadow:', d);
  }

  // ---- 3. Assemble manifest elements ----------------------------------------
  const elements: ManifestElement[] = [];
  const fontMap = new Map<string, Set<string>>();
  const canonicalRefs = new Set<string>();

  for (const p of plans) {
    const node = p.node;
    const isRoot = p.parentId === null;
    const parentSize = parentDims(node, planById);

    const asset = !failedExportIds.has(node.id) ? assetByNode.get(node.id) : undefined;
    const hasAsset = !!asset;

    const transform = isRoot
      ? rootTransform(frameW, frameH)
      : mapTransform({
          rect: {
            x: (node as unknown as { x: number }).x,
            y: (node as unknown as { y: number }).y,
            w: (node as unknown as { width: number }).width,
            h: (node as unknown as { height: number }).height,
          },
          parent: parentSize,
          horizontal: (node as unknown as { constraints?: Constraints }).constraints?.horizontal,
          vertical: (node as unknown as { constraints?: Constraints }).constraints?.vertical,
          rotation: (node as unknown as { rotation?: number }).rotation ?? 0,
        });

    const components: string[] = [];
    let text: TextProps | undefined;
    let style: Style | undefined;

    if (p.canonicalRef) {
      canonicalRefs.add(p.canonicalRef.ref);
    } else if (node.type === 'TEXT' && !hasAsset) {
      text = buildText(node as TextNode);
      const styles = fontMap.get(text.fontFamily) || new Set<string>();
      styles.add(text.fontStyle);
      fontMap.set(text.fontFamily, styles);
      components.push('TextMeshProUGUI');
    } else {
      style = buildStyle(node, options, hasAsset);
      if (hasAsset || style?.fill || style?.stroke) components.push('Image');
    }
    if (interactive(node.name) && !p.canonicalRef) components.push('Button');

    const canonical = buildCanonical(p.canonicalRef, node);
    if (canonical && stateByNode.has(node.id)) canonical.states = stateByNode.get(node.id);
    if (canonical && shapeByNode.has(node.id)) {
      const sh = shapeByNode.get(node.id);
      if (sh) {
        canonical.shape = sh.shape;
        if (sh.rootShape) canonical.rootShape = sh.rootShape;
        canonical.stateColors = sh.stateColors;
        canonical.stateShapes = sh.stateShapes;
        canonical.parts = sh.parts;
      }
    }
    if (canonical && instShapeByNode.has(node.id)) canonical.instanceShape = instShapeByNode.get(node.id);
    if (canonical && instRootShapeByNode.has(node.id)) canonical.instanceRootShape = instRootShapeByNode.get(node.id);
    if (canonical && instStateColorsByNode.has(node.id)) canonical.instanceStateColors = instStateColorsByNode.get(node.id);
    if (canonical && instStateShapesByNode.has(node.id)) canonical.instanceStateShapes = instStateShapesByNode.get(node.id);
    if (canonical && controlByNode.has(node.id)) {
      const instanceDropdownLabel = canonical.kind === 'dropdown' ? canonical.label : undefined;
      const instanceDropdownValue = canonical.kind === 'dropdown' ? canonical.value : undefined;
      const instanceInputLabel = canonical.kind === 'input' ? canonical.label : undefined;
      const instanceInputPlaceholder = canonical.kind === 'input' ? canonical.placeholder : undefined;
      const instanceInputValue = canonical.kind === 'input' ? canonical.value : undefined;
      Object.assign(canonical, controlByNode.get(node.id));
      if (canonical.kind === 'dropdown') {
        if (instanceDropdownLabel !== undefined) canonical.label = instanceDropdownLabel;
        if (instanceDropdownValue !== undefined) canonical.value = instanceDropdownValue;
      } else if (canonical.kind === 'input') {
        if (instanceInputLabel !== undefined) canonical.label = instanceInputLabel;
        if (instanceInputPlaceholder !== undefined) canonical.placeholder = instanceInputPlaceholder;
        if (instanceInputValue !== undefined) canonical.value = instanceInputValue;
      }
    }
    const nav = navFor(node);

    const element: ManifestElement = {
      id: node.id,
      name: sanitize(node.name),
      displayName: node.name,
      type: node.type,
      parentId: p.parentId,
      rect: {
        x: (node as unknown as { x: number }).x,
        y: (node as unknown as { y: number }).y,
        w: (node as unknown as { width: number }).width,
        h: (node as unknown as { height: number }).height,
      },
      rotation: (node as unknown as { rotation?: number }).rotation ?? 0,
      transform,
      components,
      style,
      text,
      asset: asset ? asset.file : null,
      // Crisp procedural mesh for raw vector nodes; Unity prefers it over the PNG.
      // Gated on hasAsset so it only replaces a PNG that would have rendered here —
      // a merged vector (baked into a parent's PNG) must not also draw its own mesh.
      vector: hasAsset && VECTOR_SHAPE_TYPES.has(node.type) ? (buildVectorDrawing(node) ?? undefined) : undefined,
      assetBounds: asset
        ? {
            x: (node as unknown as { x: number }).x,
            y: (node as unknown as { y: number }).y,
            w: (node as unknown as { width: number }).width,
            h: (node as unknown as { height: number }).height,
            pixelWidth: asset.w,
            pixelHeight: asset.h,
            exportScale: scaleNum,
          }
        : undefined,
      canonical,
      nav,
      interactive: interactive(node.name),
      clipsContent: (node as unknown as { clipsContent?: boolean }).clipsContent === true,
      merged: p.merged,
      children: p.children.map((c) => c.id).filter((id) => planById.has(id)),
    };
    elements.push(element);
  }

  onProgress?.(exportPlans.length, exportPlans.length, 'done');

  const fonts: ManifestFont[] = [...fontMap.entries()].map(([family, styles]) => ({
    family,
    styles: [...styles],
  }));

  const manifest: Manifest = {
    schema: MANIFEST_SCHEMA,
    version: MANIFEST_VERSION,
    generator: 'FigForge',
    exportedAt: new Date().toISOString(),
    screen: {
      id: root.id,
      name: sanitize(root.name),
      figmaSize: { w: frameW, h: frameH },
      referenceResolution: { w: frameW * scaleNum, h: frameH * scaleNum },
      exportScale: scaleNum,
    },
    elements,
    assets: assetEntries,
    fonts,
    settings: {
      fontFaceDilate,
    },
    canonicalRefs: [...canonicalRefs],
  };

  return { manifest, assets };
}

// ---------------------------------------------------------------------------
function clampNumber(value: unknown, min: number, max: number, fallback: number): number {
  return typeof value === 'number' && Number.isFinite(value)
    ? Math.min(max, Math.max(min, value))
    : fallback;
}

function isDescendant(node: SceneNode, ancestor: SceneNode): boolean {
  let p = (node as unknown as { parent?: BaseNode | null }).parent;
  while (p) {
    if (p.id === ancestor.id) return true;
    p = (p as unknown as { parent?: BaseNode | null }).parent;
  }
  return false;
}

function parentDims(
  node: SceneNode,
  planById: Map<string, Plan>
): { w: number; h: number } {
  const parent = (node as unknown as { parent?: BaseNode | null }).parent;
  if (parent && 'width' in parent) {
    return {
      w: (parent as unknown as { width: number }).width,
      h: (parent as unknown as { height: number }).height,
    };
  }
  return { w: 1, h: 1 };
}
