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
  type CanonicalKind,
  type CanonicalRef,
  type CanonicalStates,
  type ExportOptions,
  type ExportScale,
  type Fill,
  type GradientKind,
  type Manifest,
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

export interface ExportResult {
  manifest: Manifest;
  assets: { name: string; data: number[] }[];
}

export type ProgressFn = (current: number, total: number, label: string) => void;

const INTERACTIVE_HINTS = ['button', 'btn', 'input', 'field', 'toggle', 'checkbox', 'switch'];

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

function firstFill(node: SceneNode, options: ExportOptions): Fill | undefined {
  const fills = (node as unknown as { fills?: Paint[] | symbol }).fills;
  if (!Array.isArray(fills)) return undefined;
  const paint = fills.find((f) => !isEmptyPaint(f));
  if (!paint) return undefined;

  if (paint.type === 'SOLID') {
    return { kind: 'solid', color: toRGBA(paint.color, paint.opacity) };
  }
  if (paint.type.startsWith('GRADIENT') && options.emitGradients) {
    const g = paint as GradientPaint;
    return {
      kind: 'gradient',
      gradient: gradientKind(paint.type),
      stops: g.gradientStops.map((s) => ({
        position: s.position,
        color: [s.color.r, s.color.g, s.color.b, s.color.a] as RGBA,
      })),
      transform: g.gradientTransform ? ([] as number[]).concat(...g.gradientTransform) : undefined,
    };
  }
  return undefined;
}

function extractStroke(node: SceneNode): Stroke | undefined {
  if (!hasVisibleStroke(node)) return undefined;
  const strokes = (node as unknown as { strokes?: Paint[] }).strokes || [];
  const paint = strokes.find((f) => !isEmptyPaint(f) && f.type === 'SOLID') as SolidPaint | undefined;
  if (!paint) return undefined;
  const weight = (node as unknown as { strokeWeight?: number }).strokeWeight ?? 1;
  const alignRaw = (node as unknown as { strokeAlign?: string }).strokeAlign || 'CENTER';
  const dashes = (node as unknown as { dashPattern?: number[] }).dashPattern || [];
  return {
    color: toRGBA(paint.color, paint.opacity),
    weight: typeof weight === 'number' ? weight : 1,
    align: alignRaw === 'INSIDE' ? 'inside' : alignRaw === 'OUTSIDE' ? 'outside' : 'center',
    dashed: dashes.length > 0,
  };
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

function buildStyle(node: SceneNode, options: ExportOptions, hasAsset: boolean): Style | undefined {
  const opacity = (node as unknown as { opacity?: number }).opacity ?? 1;
  const fill = firstFill(node, options);
  const stroke = options.rasterizeStrokes ? undefined : extractStroke(node);
  const { radius, corners } = cornerData(node);

  // No real fill → transparent, NOT opaque white. Fabricated white is the cause
  // of stray white boxes on fill-less styled containers. (FigmaTest fix.)
  const resolvedFill: Fill | undefined =
    fill || (!hasAsset && (stroke || radius > 0) ? { kind: 'solid', color: [0, 0, 0, 0] } : fill);

  if (!resolvedFill && !stroke && radius === 0 && opacity === 1) return undefined;
  return { opacity, cornerRadius: radius, corners, fill: resolvedFill, stroke };
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
  if (kind === 'input') return firstTextLabel(node);
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
  const c: CanonicalRef = {
    kind: ref.kind,
    ref: ref.ref,
    instanceName: ref.instanceName,
    label: firstTextLabel(node) || ref.instanceName,
  };
  const value = canonicalValue(node, ref.kind);
  if (value !== undefined) c.value = value;
  if (ref.kind === 'dropdown') {
    const opts = gatherTexts(node);
    if (opts.length) c.options = opts;
  }
  // This instance's label font (used as a per-instance override when it differs).
  const labelNode = firstTextNode(node);
  if (labelNode && labelNode.fontName !== figma.mixed) {
    const fn = labelNode.fontName as FontName;
    c.labelFont = { family: fn.family, style: fn.style };
  }
  // The canonical COMPONENT's label font — the generated prefab/definition uses
  // this, so the prefab mirrors the component (not whatever an instance overrode).
  const comp = node.type === 'INSTANCE' ? (node as InstanceNode).mainComponent : null;
  const defNode = comp ? firstTextNode(comp) : (node.type === 'COMPONENT' ? labelNode : undefined);
  if (defNode && defNode.fontName !== figma.mixed) {
    const fn = defNode.fontName as FontName;
    c.defLabelFont = { family: fn.family, style: fn.style };
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

  // First solid-fill colour of a node → RGBA (null if no solid fill).
  function solidRGBA(node: SceneNode): RGBA | null {
    const fills = (node as unknown as { fills?: Paint[] | symbol }).fills;
    if (!Array.isArray(fills)) return null;
    const s = fills.find((f) => !isEmptyPaint(f) && f.type === 'SOLID') as SolidPaint | undefined;
    return s ? toRGBA(s.color, s.opacity) : null;
  }

  // The renderable fill of a button state layer: a solid colour, or a 2-stop
  // linear gradient (the SDF shader renders both crisply). Other gradients /
  // image fills → null, so the button keeps the exported-PNG path.
  function shapeFill(node: SceneNode): { fill: RGBA; fill2?: RGBA; gradientTransform?: number[] } | null {
    const fills = (node as unknown as { fills?: Paint[] | symbol }).fills;
    if (!Array.isArray(fills)) return null;
    const paint = fills.find((f) => !isEmptyPaint(f));
    if (!paint) return null;
    if (paint.type === 'SOLID') {
      const s = paint as SolidPaint;
      return { fill: toRGBA(s.color, s.opacity) };
    }
    if (paint.type === 'GRADIENT_LINEAR') {
      const g = paint as GradientPaint;
      const stops = g.gradientStops;
      if (stops.length >= 2) {
        // 2 stops = exact; 3+ stops are approximated by first→last (the SDF
        // shader is a 2-colour lerp). Better than baking a PNG for the crisp look.
        const c0 = stops[0].color, c1 = stops[stops.length - 1].color;
        return {
          fill: [c0.r, c0.g, c0.b, c0.a] as RGBA,
          fill2: [c1.r, c1.g, c1.b, c1.a] as RGBA,
          gradientTransform: g.gradientTransform ? ([] as number[]).concat(...g.gradientTransform) : undefined,
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

  // Procedural background shape from a button master's state layers: solid OR
  // linear gradient (SDF shader). Other fills → null → exported-PNG path.
  function captureButtonShape(master: SceneNode) {
    if (!('children' in master)) return null;
    const kids = (master as ChildrenMixin).children as SceneNode[];
    const reg = kids.find((c) => c.name.toLowerCase() === 'regular');
    if (!reg) { shapeDiag.push(`'${master.name}': no layer named 'regular'`); return null; }
    const sf = shapeFill(reg);
    if (!sf) { shapeDiag.push(`'${master.name}': regular fill = ${fillDiag(reg)}`); return null; } // unsupported fill → PNG path
    const radius = typeof (reg as unknown as { cornerRadius?: number }).cornerRadius === 'number'
      ? (reg as unknown as { cornerRadius: number }).cornerRadius : 0;
    const shape: {
      cornerRadius: number; fill: RGBA; fill2?: RGBA; gradientTransform?: number[];
      borderColor?: RGBA; borderWidth?: number; borderAlign?: 'inside' | 'outside' | 'center';
    } = { cornerRadius: radius, fill: sf.fill };
    if (sf.fill2) { shape.fill2 = sf.fill2; shape.gradientTransform = sf.gradientTransform; }
    const strokes = (reg as unknown as { strokes?: Paint[] }).strokes;
    const sw = (reg as unknown as { strokeWeight?: number }).strokeWeight;
    if (Array.isArray(strokes) && typeof sw === 'number' && sw > 0) {
      const sc = strokes.find((s) => !isEmptyPaint(s) && s.type === 'SOLID') as SolidPaint | undefined;
      if (sc) {
        shape.borderColor = toRGBA(sc.color, sc.opacity);
        shape.borderWidth = sw;
        const al = (reg as unknown as { strokeAlign?: string }).strokeAlign || 'CENTER';
        shape.borderAlign = al === 'INSIDE' ? 'inside' : al === 'OUTSIDE' ? 'outside' : 'center';
      }
    }
    const stateColors: { normal?: RGBA; highlighted?: RGBA; pressed?: RGBA } = { normal: sf.fill };
    const ro = kids.find((c) => c.name.toLowerCase() === 'rollover'); const rc = ro ? solidRGBA(ro) : null; if (rc) stateColors.highlighted = rc;
    const pr = kids.find((c) => c.name.toLowerCase() === 'pressed'); const pc = pr ? solidRGBA(pr) : null; if (pc) stateColors.pressed = pc;
    return { shape, stateColors };
  }

  const stateByNode = new Map<string, CanonicalStates>();
  const shapeByNode = new Map<string, ReturnType<typeof captureButtonShape>>();
  for (const p of plans) {
    if (!p.canonicalRef || p.canonicalRef.kind !== 'button') continue;
    const master =
      p.node.type === 'INSTANCE'
        ? ((p.node as InstanceNode).mainComponent as SceneNode | null)
        : p.node;
    if (!master) continue;
    const states = await exportStates(master);
    if (states) stateByNode.set(p.node.id, states);
    const sh = captureButtonShape(master);
    if (sh) shapeByNode.set(p.node.id, sh);
  }
  if (shapeDiag.length) {
    // Tell the designer exactly why a button used a baked PNG instead of the crisp shader.
    try { figma.notify(`FigForge: ${shapeDiag.length} button(s) → PNG. ${shapeDiag[0]}`, { timeout: 7000 }); } catch { /* headless */ }
    for (const d of shapeDiag) console.warn('[FigForge] button→PNG:', d);
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
      if (sh) { canonical.shape = sh.shape; canonical.stateColors = sh.stateColors; }
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
    canonicalRefs: [...canonicalRefs],
  };

  return { manifest, assets };
}

// ---------------------------------------------------------------------------
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
