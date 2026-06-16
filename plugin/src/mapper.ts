// =============================================================================
// FigForge — constraint mapper
//
// Converts a Figma node's layout (rect within its parent + per-axis constraints)
// into a Unity RectTransform expressed as anchorMin/anchorMax + offsetMin/offsetMax.
//
// Coordinate conversion: Figma is top-left origin with +Y down; Unity uGUI is
// bottom-left origin with +Y up. We flip Y when computing the parent-space rect.
//
// Constraint-driven anchors (a key fidelity feature): instead of pinning every
// child to a centred (0.5,0.5) anchor, we honour each axis's Figma constraint so
// stretched / right-pinned / proportional children stay correct as the canvas
// resizes.
// =============================================================================

import type { Rect, UnityTransform, Vec2 } from './types';

type Axis = 'MIN' | 'MAX' | 'CENTER' | 'STRETCH' | 'SCALE';

interface AxisAnchor {
  min: number;
  max: number;
}

/** Horizontal constraint → normalized anchor pair (0=left, 1=right). */
function horizontalAnchor(c: Axis, left: number, right: number, pw: number): AxisAnchor {
  switch (c) {
    case 'MIN':
      return { min: 0, max: 0 };
    case 'MAX':
      return { min: 1, max: 1 };
    case 'CENTER':
      return { min: 0.5, max: 0.5 };
    case 'STRETCH':
      return { min: 0, max: 1 };
    case 'SCALE':
      return pw > 0 ? { min: left / pw, max: right / pw } : { min: 0, max: 0 };
  }
}

/**
 * Vertical constraint → normalized anchor pair, already Y-flipped for Unity.
 * Figma MIN = top edge → Unity anchor ~1; Figma MAX = bottom → Unity anchor ~0.
 */
function verticalAnchor(c: Axis, bottom: number, top: number, ph: number): AxisAnchor {
  switch (c) {
    case 'MIN':
      return { min: 1, max: 1 }; // pinned to top
    case 'MAX':
      return { min: 0, max: 0 }; // pinned to bottom
    case 'CENTER':
      return { min: 0.5, max: 0.5 };
    case 'STRETCH':
      return { min: 0, max: 1 };
    case 'SCALE':
      return ph > 0 ? { min: bottom / ph, max: top / ph } : { min: 0, max: 0 };
  }
}

function normalizeConstraint(value: string | undefined): Axis {
  switch ((value || '').toUpperCase()) {
    case 'MIN':
      return 'MIN';
    case 'MAX':
      return 'MAX';
    case 'CENTER':
      return 'CENTER';
    case 'STRETCH':
      return 'STRETCH';
    case 'SCALE':
      return 'SCALE';
    default:
      return 'MIN';
  }
}

// A 1px background seam appears when two independently rendered UI quads meet on
// fractional coordinates. We close it by snapping every layout edge to the SAME
// absolute integer pixel grid (see mapTransform): two nodes that abut in Figma —
// even across different parent subtrees — then resolve to the exact same Unity
// edge, and equal edges scale identically under any CanvasScaler factor, so the
// seam can never open between them.
function snapPixel(value: number): number {
  return Math.round(value);
}

function snapSize(value: number): number {
  const snapped = snapPixel(value);
  return value > 0 && snapped <= 0 ? value : snapped;
}

export interface MapInput {
  rect: Rect; // child rect in parent-local Figma coords (top-left origin)
  parent: { w: number; h: number };
  abs?: Vec2; // node absolute top-left in Figma canvas space — enables grid snap
  parentAbs?: Vec2; // immediate parent absolute top-left, same space as `abs`
  horizontal: string | undefined; // Figma constraints.horizontal
  vertical: string | undefined; // Figma constraints.vertical
  rotation: number; // degrees (already flip-corrected by the caller)
  flipX?: boolean; // mirror on X (negative-determinant transform)
  flipY?: boolean; // mirror on Y
}

export function mapTransform(input: MapInput): UnityTransform {
  const { rect, parent, rotation } = input;
  const rotated = Math.abs(rotation || 0) > 0.001;
  // Snap on the ABSOLUTE pixel grid (not per-node-local): edges that coincide in
  // Figma round to the same integer regardless of which subtree they live in, so
  // abutting panels share an exact Unity edge and no background seam can open.
  // Rotated nodes stay exact — snapping their axis-aligned span would distort them.
  const snapLayout = !rotated && input.abs != null && input.parentAbs != null;

  let pw: number;
  let ph: number;
  let left: number;
  let right: number;
  let top: number;
  let bottom: number;

  if (snapLayout) {
    const [ax, ay] = input.abs as Vec2;
    const [px, py] = input.parentAbs as Vec2;
    // A flipped node's origin (its abs translation) is the MIRRORED edge — for flipX
    // the RIGHT edge (local 0,0 maps to max-X), for flipY the BOTTOM. Anchor the rect
    // from the opposite edge; localScale then mirrors the content back in place.
    // Without this the node lands a full width/height too far right/down.
    const fax = input.flipX ? ax - rect.w : ax;
    const fay = input.flipY ? ay - rect.h : ay;
    const aLeft = snapPixel(fax);
    const aRight = snapPixel(fax + rect.w);
    const aTop = snapPixel(fay);
    const aBottom = snapPixel(fay + rect.h);
    const pLeft = snapPixel(px);
    const pRight = snapPixel(px + parent.w);
    const pTop = snapPixel(py);
    const pBottom = snapPixel(py + parent.h);

    pw = pRight > pLeft ? pRight - pLeft : parent.w;
    ph = pBottom > pTop ? pBottom - pTop : parent.h;

    // Child rect edges relative to the SNAPPED parent origin (Unity parent-space).
    left = aLeft - pLeft;
    right = aRight - pLeft;
    top = ph - (aTop - pTop); // figma top edge, Y-flipped
    bottom = ph - (aBottom - pTop); // figma bottom edge, Y-flipped
  } else {
    pw = parent.w;
    ph = parent.h;
    // Flipped origin sits on the mirrored edge (see snap path) — anchor from the
    // opposite edge so the rect spans the node's real bounds before localScale mirrors.
    const fx = input.flipX ? rect.x - rect.w : rect.x;
    const fy = input.flipY ? rect.y - rect.h : rect.y;
    left = fx;
    right = fx + rect.w;
    top = ph - fy; // figma top edge
    bottom = ph - (fy + rect.h); // figma bottom edge
  }

  const h = horizontalAnchor(normalizeConstraint(input.horizontal), left, right, pw);
  const v = verticalAnchor(normalizeConstraint(input.vertical), bottom, top, ph);

  const anchorMin: Vec2 = [h.min, v.min];
  const anchorMax: Vec2 = [h.max, v.max];

  // offset = childEdge - anchorReference(parentSize * anchor)
  const offsetMin: Vec2 = [left - h.min * pw, bottom - v.min * ph];
  const offsetMax: Vec2 = [right - h.max * pw, top - v.max * ph];

  // Rotation pivot: Figma's x/y are the translation column of relativeTransform —
  // the node's own local origin (its UNROTATED top-left corner) in parent coords —
  // so Figma rotates the w×h rect about that top-left corner. Unity rotates about
  // rt.pivot, so rotated nodes must pivot at top-left ([0,1] in Unity's pivot
  // space: x=0 left, y=1 top). This is placement-safe because the rect is defined
  // via offsetMin/offsetMax against the anchors, which pin the edges regardless of
  // pivot — the pivot only sets the rotation/scale centre. Unrotated nodes keep
  // the historical centre pivot so nothing else shifts behaviour.
  return {
    anchorMin,
    anchorMax,
    pivot: rotated ? [0, 1] : [0.5, 0.5],
    offsetMin,
    offsetMax,
    rotationZ: rotation || 0, // Figma degrees, +CCW on screen (see HierarchyBuilder/UxmlBuilder)
    flipX: input.flipX || undefined,
    flipY: input.flipY || undefined,
  };
}

/** Root frame: centred on the Canvas at its own size. */
export function rootTransform(w: number, h: number): UnityTransform {
  const sw = snapSize(w);
  const sh = snapSize(h);
  // Split each half-extent with floor/ceil so an ODD size still lands BOTH edges
  // on whole pixels. A plain −sw/2 is ±X.5 for odd sizes, which shifts the entire
  // tree half a pixel off the grid; floor/ceil keeps the exact size (span == sw)
  // with integer edges, so descendants stay pixel-aligned.
  const halfW = Math.floor(sw / 2);
  const halfH = Math.floor(sh / 2);
  return {
    anchorMin: [0.5, 0.5],
    anchorMax: [0.5, 0.5],
    pivot: [0.5, 0.5],
    offsetMin: [-halfW, -halfH],
    offsetMax: [sw - halfW, sh - halfH],
    rotationZ: 0,
  };
}
