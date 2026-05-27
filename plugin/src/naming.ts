// =============================================================================
// FigForge — naming + sanitization + canonical-reference parsing
// =============================================================================

import type { CanonicalKind, CanonicalRef } from './types';

/** snake_case-safe token: lowercase, non-alphanumerics → "_", collapsed, trimmed. */
export function sanitize(raw: string): string {
  const s = (raw || '')
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '_')
    .replace(/_+/g, '_')
    .replace(/^_+|_+$/g, '');
  return s.length ? s : 'node';
}

/** `<root>_<element>@<scale>x.png` — deterministic, dedup-friendly. */
export function generateFileName(root: string, element: string, scale: number): string {
  const r = sanitize(root);
  const e = sanitize(element);
  const base = r && e ? `${r}_${e}` : r || e;
  const scaleTag = Number.isInteger(scale) ? `${scale}` : `${scale}`.replace('.', '_');
  return `${base}@${scaleTag}x.png`;
}

// ---------------------------------------------------------------------------
// Canonical UI element naming convention.
//
//   Btn_<instanceName>_<canonicalRef>
//
// The leading token is a case-insensitive KIND tag, the trailing
// underscore-delimited token is the canonical reference name, and everything
// between is the instance name. Examples:
//   Btn_Save_PrimaryButton        → kind=button, instance=Save,        ref=PrimaryButton
//   Btn_Cancel_Secondary_Button   → kind=button, instance=Cancel,      ref=Secondary_Button
//                                    (ref is the *last* token: "Button"; see below)
//
// To keep multi-word refs usable we treat the ref as the final token only.
// Designers who need underscores in a ref should avoid them; the convention is
// deliberately simple while we support a single canonical kind (button).
// ---------------------------------------------------------------------------
const KIND_TAGS: Record<string, CanonicalKind> = {
  btn: 'button',
  button: 'button',
};

export function parseCanonical(name: string): CanonicalRef | null {
  if (!name) return null;
  const parts = name.split('_').map((p) => p.trim()).filter(Boolean);
  if (parts.length < 3) return null;

  const kind = KIND_TAGS[parts[0].toLowerCase()];
  if (!kind) return null;

  const ref = parts[parts.length - 1];
  const instanceName = parts.slice(1, parts.length - 1).join('_');
  if (!ref || !instanceName) return null;

  return { kind, ref, instanceName };
}

/** True if a name follows any recognised canonical convention. */
export function isCanonicalName(name: string): boolean {
  return parseCanonical(name) !== null;
}
