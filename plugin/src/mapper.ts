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

export interface MapInput {
  rect: Rect; // child rect in parent-local Figma coords (top-left origin)
  parent: { w: number; h: number };
  horizontal: string | undefined; // Figma constraints.horizontal
  vertical: string | undefined; // Figma constraints.vertical
  rotation: number; // degrees
}

export function mapTransform(input: MapInput): UnityTransform {
  const { rect, parent, rotation } = input;
  const pw = parent.w;
  const ph = parent.h;
  const rotated = Math.abs(rotation || 0) > 0.001;

  // Child rect edges in Unity parent-space (bottom-left origin, +Y up).
  const left = rect.x;
  const right = rect.x + rect.w;
  const top = ph - rect.y; // figma top edge
  const bottom = ph - (rect.y + rect.h); // figma bottom edge

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
  };
}

/** Root frame: centred on the Canvas at its own size. */
export function rootTransform(w: number, h: number): UnityTransform {
  return {
    anchorMin: [0.5, 0.5],
    anchorMax: [0.5, 0.5],
    pivot: [0.5, 0.5],
    offsetMin: [-w / 2, -h / 2],
    offsetMax: [w / 2, h / 2],
    rotationZ: 0,
  };
}
