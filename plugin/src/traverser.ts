// =============================================================================
// FigForge — tree traversal, exportability, paint helpers
// =============================================================================

import type { CanonicalKind, CanonicalRef, TreeNode } from './types';
import { sanitize, parseCanonical } from './naming';

const PLUGIN_DATA_KEY = 'figforge';

function parseTag(data: string): { kind: CanonicalKind; ref: string } | null {
  if (!data) return null;
  try {
    const t = JSON.parse(data);
    if (t && t.kind && t.ref) return { kind: t.kind as CanonicalKind, ref: String(t.ref) };
  } catch {
    /* not a FigForge tag */
  }
  return null;
}

/**
 * Resolve an element's canonical binding. Priority:
 *   1. an INSTANCE of a FigForge-tagged master Component (robust — survives
 *      skinning/renaming),
 *   2. a node carrying the tag itself,
 *   3. the `Btn_<instance>_<ref>` name convention (fallback).
 */
export function detectCanonical(node: SceneNode): CanonicalRef | null {
  if (node.type === 'INSTANCE') {
    const mc = (node as InstanceNode).mainComponent;
    const tag = mc ? parseTag(mc.getPluginData(PLUGIN_DATA_KEY)) : null;
    if (tag) return { kind: tag.kind, ref: sanitize(tag.ref) || tag.ref, instanceName: sanitize(node.name) };
  }
  const selfTag = parseTag(node.getPluginData(PLUGIN_DATA_KEY));
  if (selfTag) return { kind: selfTag.kind, ref: sanitize(selfTag.ref) || selfTag.ref, instanceName: sanitize(node.name) };
  return parseCanonical(node.name);
}

const VECTOR_TYPES = new Set([
  'VECTOR',
  'BOOLEAN_OPERATION',
  'LINE',
  'ELLIPSE',
  'POLYGON',
  'STAR',
]);

const CONTAINER_TYPES = new Set(['FRAME', 'GROUP', 'COMPONENT', 'INSTANCE', 'COMPONENT_SET']);

/**
 * A paint that contributes nothing to the rendered output and must be treated
 * as "no fill": hidden, fully transparent, or an IMAGE paint with no source
 * attached. The image case is a placeholder left in the design — without this
 * guard the node is flagged exportable and bakes a junk/empty PNG (+ Image
 * component) into Unity. Ported from the FigmaTest fixes.
 */
export function isEmptyPaint(paint: Paint | undefined | null): boolean {
  if (!paint) return true;
  if (paint.visible === false) return true;
  if (typeof paint.opacity === 'number' && paint.opacity === 0) return true;
  if (paint.type === 'IMAGE' && !(paint as ImagePaint).imageHash) return true;
  return false;
}

function paints(node: SceneNode, key: 'fills' | 'strokes'): ReadonlyArray<Paint> {
  const v = (node as unknown as Record<string, unknown>)[key];
  if (v === figma.mixed || !Array.isArray(v)) return [];
  return v as ReadonlyArray<Paint>;
}

export function hasMeaningfulFill(node: SceneNode): boolean {
  return paints(node, 'fills').some((f) => !isEmptyPaint(f));
}

export function hasVisibleStroke(node: SceneNode): boolean {
  const w = (node as unknown as { strokeWeight?: number }).strokeWeight;
  if (typeof w === 'number' && w <= 0) return false;
  return paints(node, 'strokes').some((f) => !isEmptyPaint(f));
}

function isVisible(node: SceneNode): boolean {
  return (node as unknown as { visible?: boolean }).visible !== false;
}

function hasChildren(node: SceneNode): node is SceneNode & ChildrenMixin {
  return 'children' in node;
}

/** Container made up solely of vector shapes — treat as a single icon. */
function isIconContainer(node: SceneNode): boolean {
  if (!CONTAINER_TYPES.has(node.type) || !hasChildren(node)) return false;
  const kids = node.children.filter(isVisible);
  if (kids.length === 0) return false;
  return kids.every((c) => VECTOR_TYPES.has(c.type));
}

/**
 * Should this node be rasterized to a PNG (vs. rebuilt structurally)?
 * Vectors always; text only when explicitly forced; containers when they carry
 * their own visible fill/stroke or are icon-only groups.
 */
export function isExportable(node: SceneNode): boolean {
  if (!isVisible(node)) return false;
  if (VECTOR_TYPES.has(node.type)) return true;
  if (node.type === 'TEXT') return false; // structural by default
  if (isIconContainer(node)) return true;
  if (CONTAINER_TYPES.has(node.type) || node.type === 'RECTANGLE') {
    return hasMeaningfulFill(node) || hasVisibleStroke(node);
  }
  return false;
}

export function canMerge(node: SceneNode): boolean {
  return (CONTAINER_TYPES.has(node.type) || node.type === 'RECTANGLE') && hasChildren(node);
}

/** Build the UI layer tree for a selected root node. */
export function buildTree(root: SceneNode, excluded: Set<string>): TreeNode {
  function walk(node: SceneNode, depth: number): TreeNode {
    const canonical = detectCanonical(node);
    const childNodes: TreeNode[] =
      hasChildren(node) && !isIconContainer(node)
        ? node.children.map((c) => walk(c, depth + 1))
        : [];
    return {
      id: node.id,
      name: sanitize(node.name),
      displayName: node.name,
      type: node.type,
      depth,
      visible: isVisible(node) && !excluded.has(node.id),
      canExportPng: node.type === 'TEXT' || isExportable(node),
      canMerge: canMerge(node),
      canonicalRef: canonical ? canonical.ref : undefined,
      children: childNodes,
    };
  }
  return walk(root, 0);
}
