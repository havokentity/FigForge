// =============================================================================
// FigForge — vector geometry → triangle mesh
//
// Turns a Figma vector node's resolved fill/stroke outlines into flat triangle
// meshes the Unity importer can stream straight into a CanvasRenderer (no SDF,
// no texture). The hard work — bézier flattening, hole/winding triangulation,
// anti-alias fringe — happens HERE, once, so the C# side stays trivial.
//
// Figma's fillGeometry/strokeGeometry are a tiny, fully-specified SVG subset:
// absolute M / L / C / Q / Z only (no arcs, no relative, no shorthand), and
// strokeGeometry is already expanded to a fillable outline (caps/joins/dashes
// baked in) — so a stroke is just another filled region.
//
// Anything we can't represent faithfully (gradient/image/multi paints, huge
// meshes, empty geometry) returns null → the caller keeps the PNG fallback.
// =============================================================================
import earcut from 'earcut';
import type { RGBA, VectorDrawing, VectorMesh } from './types';

const AA_PX = 0.75;          // anti-alias feather width, node-local px
const FLATTEN_TOL = 0.15;    // max bézier chord deviation, node-local px
const MAX_TOTAL_VERTS = 24000; // safety cap → above this, fall back to PNG
// Control-polygon extent below which a cubic segment is DEGENERATE (all four
// points effectively coincident) and flattens to a single point. Far below the
// 0.15px FLATTEN_TOL, so no normally-sized curve can hit it. See flattenCubic.
const DEGENERATE_EPS = 1e-3; // node-local px
// Per-ring flattening budget, in array slots (2 per point). Set just past
// MAX_TOTAL_VERTS points: any ring that large is already guaranteed to trip
// the total-verts cap and fall back to PNG, so truncating there changes no
// surviving output — it only spares earcut its quadratic worst case on a
// quarter-million-point degenerate ring (the multi-second hang).
const MAX_RING_SLOTS = 2 * (MAX_TOTAL_VERTS + 1);

// First (and only) visible SOLID paint as RGBA, or a signal:
//   null         → no visible paint (e.g. stroke-only or fill-only node)
//   'unsupported'→ mixed / gradient / image / multi-paint → let PNG render it
function singleSolid(paints: unknown): RGBA | null | 'unsupported' {
  if (typeof paints === 'symbol') return 'unsupported'; // figma.mixed
  if (!Array.isArray(paints)) return null;
  const vis = (paints as Paint[]).filter((p) => p && p.visible !== false && (p.opacity ?? 1) > 0.0001);
  if (vis.length === 0) return null;
  if (vis.length > 1) return 'unsupported';
  const p = vis[0];
  if (p.type !== 'SOLID') return 'unsupported';
  const c = (p as SolidPaint).color;
  return [c.r, c.g, c.b, p.opacity ?? 1];
}

interface Geo { readonly windingRule: string; readonly data: string }

// Build the full vector drawing for a node, or null to keep the PNG fallback.
export function buildVectorDrawing(node: SceneNode): VectorDrawing | null {
  try {
    const n = node as unknown as {
      fillGeometry?: ReadonlyArray<Geo>;
      strokeGeometry?: ReadonlyArray<Geo>;
      fills?: unknown;
      strokes?: unknown;
      effects?: readonly { visible?: boolean }[];
      width?: number;
      height?: number;
    };

    // A node with a visible effect (drop/inner shadow, layer blur) is left to the
    // PNG fallback, which bakes the effect in. The vector mesh renders fill+stroke
    // only, so emitting it here would silently drop the shadow.
    if (Array.isArray(n.effects) && n.effects.some((e) => e && e.visible !== false)) return null;

    const fillGeo = n.fillGeometry;
    const strokeGeo = n.strokeGeometry;
    if (!Array.isArray(fillGeo) && !Array.isArray(strokeGeo)) return null;

    const fillCol = singleSolid(n.fills);
    const strokeCol = singleSolid(n.strokes);
    // If any present paint is richer than a single solid, the PNG represents the
    // whole node faithfully — don't emit a half-right mesh.
    if (fillCol === 'unsupported' || strokeCol === 'unsupported') return null;

    const meshes: VectorMesh[] = [];
    let total = 0;

    const addGeo = (geo: ReadonlyArray<Geo> | undefined, color: RGBA | null): boolean => {
      if (!color || !Array.isArray(geo)) return true;
      for (const path of geo) {
        const rings = parsePath(path.data);
        if (!rings) return false;
        const tri = triangulate(rings, path.windingRule);
        if (!tri) continue;
        const mesh = withAA(tri.verts, tri.tris, color);
        total += mesh.verts.length / 2;
        if (total > MAX_TOTAL_VERTS) return false;
        meshes.push(mesh);
      }
      return true;
    };

    // Fills first, strokes on top — matches Figma paint order.
    if (!addGeo(fillGeo, fillCol)) return null;
    if (!addGeo(strokeGeo, strokeCol)) return null;

    const w = n.width ?? 0;
    const h = n.height ?? 0;
    if (meshes.length === 0 || w <= 0 || h <= 0) return null;
    return { bounds: [w, h], meshes };
  } catch {
    return null;
  }
}

// ---------------------------------------------------------------------------
// SVG path (Figma subset) → closed rings of [x0,y0,x1,y1,…] node-local px.
// ---------------------------------------------------------------------------
function parsePath(data: string): number[][] | null {
  const out: number[][] = [];
  if (!data) return out;
  const tok = data.match(/[a-zA-Z]|-?\d*\.?\d+(?:[eE][+-]?\d+)?/g);
  if (!tok) return out;

  let i = 0;
  let cur: number[] = [];
  let sx = 0;
  let sy = 0;
  let cx = 0;
  let cy = 0;
  const num = (): number => parseFloat(tok[i++]);
  let bad = false; // tripped if any parsed coord is non-finite → PNG fallback
  const flush = (): void => {
    if (cur.length >= 6) {
      for (let k = 0; k < cur.length; k++) {
        if (!Number.isFinite(cur[k])) { bad = true; break; }
      }
      out.push(cur);
    }
    cur = [];
  };

  while (i < tok.length) {
    const cmd = tok[i++];
    switch (cmd) {
      case 'M':
        cx = num();
        cy = num();
        flush();
        cur = [cx, cy];
        sx = cx;
        sy = cy;
        break;
      case 'L':
        cx = num();
        cy = num();
        cur.push(cx, cy);
        break;
      case 'C': {
        const x0 = num();
        const y0 = num();
        const x1 = num();
        const y1 = num();
        const x = num();
        const y = num();
        flattenCubic(cx, cy, x0, y0, x1, y1, x, y, cur, 0);
        cx = x;
        cy = y;
        break;
      }
      case 'Q': {
        const qx = num();
        const qy = num();
        const x = num();
        const y = num();
        // quadratic → cubic control points, then reuse the cubic flattener
        flattenCubic(cx, cy, cx + (2 / 3) * (qx - cx), cy + (2 / 3) * (qy - cy),
          x + (2 / 3) * (qx - x), y + (2 / 3) * (qy - y), x, y, cur, 0);
        cx = x;
        cy = y;
        break;
      }
      case 'Z':
      case 'z':
        cx = sx;
        cy = sy;
        flush();
        break;
      case 'm':
      case 'l':
      case 'c':
      case 'q':
        return null;
      default:
        return null;
    }
  }
  flush();
  // Any non-finite coordinate (NaN/Infinity from a malformed token) would
  // poison earcut downstream — bail to the PNG fallback instead.
  if (bad) return null;
  return out;
}

// Adaptive de Casteljau subdivision. Appends intermediate points + the end
// point to `out` (the start point is assumed already present).
function flattenCubic(
  x0: number, y0: number, x1: number, y1: number,
  x2: number, y2: number, x3: number, y3: number,
  out: number[], depth: number,
): void {
  if (depth > 18 || out.length > MAX_RING_SLOTS) { out.push(x3, y3); return; }
  const dx = x3 - x0;
  const dy = y3 - y0;
  // DEGENERATE segment (all/most points coincident): with a near-zero chord,
  // BOTH sides of the flatness comparison below collapse toward 0 and float
  // cancellation noise decides it — every level can "fail" flatness, so the
  // recursion expands to its full 2^18 leaves and emits ~260k coincident points
  // per segment (which then hang earcut for seconds). A curve whose control
  // polygon spans less than DEGENERATE_EPS cannot deviate visibly from a point;
  // emit the endpoint and stop. Normal curves never get here (their polygon
  // span dwarfs the epsilon long before flatness passes), so output for them
  // is pixel-identical.
  const span = Math.abs(x1 - x0) + Math.abs(y1 - y0)
    + Math.abs(x2 - x1) + Math.abs(y2 - y1)
    + Math.abs(x3 - x2) + Math.abs(y3 - y2);
  if (span < DEGENERATE_EPS) { out.push(x3, y3); return; }
  const d1 = Math.abs((x1 - x3) * dy - (y1 - y3) * dx);
  const d2 = Math.abs((x2 - x3) * dy - (y2 - y3) * dx);
  if ((d1 + d2) * (d1 + d2) <= FLATTEN_TOL * FLATTEN_TOL * (dx * dx + dy * dy)) {
    out.push(x3, y3);
    return;
  }
  const x01 = (x0 + x1) / 2;
  const y01 = (y0 + y1) / 2;
  const x12 = (x1 + x2) / 2;
  const y12 = (y1 + y2) / 2;
  const x23 = (x2 + x3) / 2;
  const y23 = (y2 + y3) / 2;
  const xa = (x01 + x12) / 2;
  const ya = (y01 + y12) / 2;
  const xb = (x12 + x23) / 2;
  const yb = (y12 + y23) / 2;
  const xm = (xa + xb) / 2;
  const ym = (ya + yb) / 2;
  flattenCubic(x0, y0, x01, y01, xa, ya, xm, ym, out, depth + 1);
  flattenCubic(xm, ym, xb, yb, x23, y23, x3, y3, out, depth + 1);
}

// ---------------------------------------------------------------------------
// Triangulation: classify EVENODD rings into solids/holes by containment parity,
// then earcut each solid with its holes. Multi-ring NONZERO paths fall back to PNG
// until the procedural path has full winding-number grouping.
// ---------------------------------------------------------------------------
function triangulate(contours: number[][], windingRule: string): { verts: number[]; tris: number[] } | null {
  const rings = contours.map(cleanRing).filter((r) => r.length >= 6);
  if (rings.length === 0) return null;
  const rule = (windingRule || '').toUpperCase();
  if (rings.length > 1 && rule !== 'EVENODD') return null;

  // Representative point per ring for containment tests. Using vertex[0] is
  // degenerate — it lies ON the ring, so an exactly-shared vertex with another
  // ring makes pointInRing's parity test unreliable. The centroid of the ring's
  // first triangle is an interior-ish point that generically misses every
  // vertex/edge of the other rings. Rings here always have >= 3 points.
  const repX = rings.map((r) => (r[0] + r[2] + r[4]) / 3);
  const repY = rings.map((r) => (r[1] + r[3] + r[5]) / 3);

  const depth: number[] = rings.map((_, i) => {
    let d = 0;
    for (let j = 0; j < rings.length; j++) {
      if (j !== i && pointInRing(repX[i], repY[i], rings[j])) d++;
    }
    return d;
  });
  // innermost containing ring = direct parent
  const parent: number[] = rings.map((_, i) => {
    let best = -1;
    let bestDepth = -1;
    for (let j = 0; j < rings.length; j++) {
      if (j === i) continue;
      if (depth[j] > bestDepth && pointInRing(repX[i], repY[i], rings[j])) {
        bestDepth = depth[j];
        best = j;
      }
    }
    return best;
  });

  const verts: number[] = [];
  const tris: number[] = [];
  for (let s = 0; s < rings.length; s++) {
    if (depth[s] % 2 !== 0) continue; // odd depth = hole, emitted by its parent
    const flat = rings[s].slice();
    const holeIdx: number[] = [];
    for (let h = 0; h < rings.length; h++) {
      if (depth[h] % 2 === 1 && parent[h] === s) {
        holeIdx.push(flat.length / 2);
        for (let k = 0; k < rings[h].length; k++) flat.push(rings[h][k]);
      }
    }
    const t = earcut(flat, holeIdx.length ? holeIdx : undefined, 2);
    if (!t.length) continue;
    const base = verts.length / 2;
    for (let k = 0; k < flat.length; k++) verts.push(flat[k]);
    for (let k = 0; k < t.length; k++) tris.push(t[k] + base);
  }
  return tris.length ? { verts, tris } : null;
}

// Drop consecutive duplicates and a closing point equal to the start.
function cleanRing(r: number[]): number[] {
  const out: number[] = [];
  for (let i = 0; i + 1 < r.length; i += 2) {
    const x = r[i];
    const y = r[i + 1];
    const n = out.length;
    if (n >= 2 && Math.abs(out[n - 2] - x) < 1e-6 && Math.abs(out[n - 1] - y) < 1e-6) continue;
    out.push(x, y);
  }
  if (out.length >= 4) {
    const n = out.length;
    if (Math.abs(out[0] - out[n - 2]) < 1e-6 && Math.abs(out[1] - out[n - 1]) < 1e-6) {
      out.length = n - 2;
    }
  }
  return out;
}

function pointInRing(px: number, py: number, ring: number[]): boolean {
  let inside = false;
  const n = ring.length / 2;
  for (let i = 0, j = n - 1; i < n; j = i++) {
    const xi = ring[i * 2];
    const yi = ring[i * 2 + 1];
    const xj = ring[j * 2];
    const yj = ring[j * 2 + 1];
    if ((yi > py) !== (yj > py) && px < ((xj - xi) * (py - yi)) / (yj - yi) + xi) {
      inside = !inside;
    }
  }
  return inside;
}

// ---------------------------------------------------------------------------
// Anti-alias fringe: extrude every boundary edge outward by AA_PX into a band
// of alpha-0 vertices, so the edge fades instead of stair-stepping. Boundary
// edges are directed edges with no opposite twin; outward = away from the
// triangle's third vertex (orientation-agnostic).
// ---------------------------------------------------------------------------
function withAA(coreVerts: number[], coreTris: number[], color: RGBA): VectorMesh {
  const verts = coreVerts.slice();
  const tris = coreTris.slice();
  const alpha: number[] = new Array(coreVerts.length / 2).fill(1);

  // Pack a directed edge (a,b) into one number. The radix must exceed the max
  // vertex index, or two distinct edges collide. Core verts are bounded by the
  // MAX_TOTAL_VERTS cap enforced before withAA runs, so MAX_TOTAL_VERTS+1 is a
  // safe radix (max key ~5.8e8, well under MAX_SAFE_INTEGER). The old 0x100000
  // (2^20) radix silently collided once a mesh exceeded ~1M verts.
  const RADIX = MAX_TOTAL_VERTS + 1;
  const key = (a: number, b: number): number => a * RADIX + b;
  const hasTwin = new Set<number>();
  for (let t = 0; t < coreTris.length; t += 3) {
    hasTwin.add(key(coreTris[t], coreTris[t + 1]));
    hasTwin.add(key(coreTris[t + 1], coreTris[t + 2]));
    hasTwin.add(key(coreTris[t + 2], coreTris[t]));
  }

  const edge = (u: number, v: number, c: number): void => {
    if (hasTwin.has(key(v, u))) return; // interior edge (shared by two triangles)
    const ux = coreVerts[u * 2];
    const uy = coreVerts[u * 2 + 1];
    const vx = coreVerts[v * 2];
    const vy = coreVerts[v * 2 + 1];
    let nx = -(vy - uy);
    let ny = vx - ux;
    const len = Math.hypot(nx, ny);
    if (len < 1e-6) return;
    nx = (nx / len) * AA_PX;
    ny = (ny / len) * AA_PX;
    // flip to point away from the interior (the third vertex c)
    const mx = (ux + vx) / 2;
    const my = (uy + vy) / 2;
    if ((coreVerts[c * 2] - mx) * nx + (coreVerts[c * 2 + 1] - my) * ny > 0) {
      nx = -nx;
      ny = -ny;
    }
    const iu = verts.length / 2;
    verts.push(ux + nx, uy + ny);
    alpha.push(0);
    const iv = verts.length / 2;
    verts.push(vx + nx, vy + ny);
    alpha.push(0);
    tris.push(u, v, iv, u, iv, iu);
  };

  for (let t = 0; t < coreTris.length; t += 3) {
    const a = coreTris[t];
    const b = coreTris[t + 1];
    const c = coreTris[t + 2];
    edge(a, b, c);
    edge(b, c, a);
    edge(c, a, b);
  }

  return { color, verts, tris, alpha };
}
