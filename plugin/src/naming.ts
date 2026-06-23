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
//                                    (ref is everything AFTER the instance token)
//
// The first token is the kind tag, the SECOND token is the instance name, and the
// REMAINDER (joined back with underscores) is the ref — so multi-word refs like
// "Secondary_Button" survive. Three-part names are unchanged (the remainder is a
// single token). The instance is a single token by design; refs may be multi-word.
// ---------------------------------------------------------------------------
const KIND_TAGS: Record<string, CanonicalKind> = {
  btn: 'button',
  button: 'button',
  tgl: 'toggle',
  toggle: 'toggle',
  chk: 'toggle',
  check: 'toggle',
  checkbox: 'toggle',
  sw: 'switch',
  switch: 'switch',
  inp: 'input',
  input: 'input',
  field: 'input',
  step: 'stepper',
  stepper: 'stepper',
  num: 'stepper',
  number: 'stepper',
  drp: 'dropdown',
  dropdown: 'dropdown',
  select: 'dropdown',
  sld: 'slider',
  slider: 'slider',
  prg: 'progress',
  progress: 'progress',
  progressbar: 'progress',
  modal: 'modal',
  dialog: 'modal',
  dlg: 'modal',
  toast: 'toast',
  notification: 'toast',
  notify: 'toast',
};

export function parseCanonical(name: string): CanonicalRef | null {
  if (!name) return null;
  const parts = name.split('_').map((p) => p.trim()).filter(Boolean);
  if (parts.length < 3) return null;

  const kind = KIND_TAGS[parts[0].toLowerCase()];
  if (!kind) return null;

  const instanceName = parts[1];
  const ref = parts.slice(2).join('_');
  if (!ref || !instanceName) return null;

  return { kind, ref, instanceName };
}

/** True if a name follows any recognised canonical convention. */
export function isCanonicalName(name: string): boolean {
  return parseCanonical(name) !== null;
}

// ---------------------------------------------------------------------------
// Pass-through (unwrap) naming convention.
//
// A container marked pass-through is SKIPPED by the generator: it never becomes
// a GameObject, and its children are promoted to the nearest surviving ancestor.
// Detected on the RAW (un-sanitized) layer name so it works headlessly and in
// page/MCP exports without any UI state:
//   • a leading '~' marker   →  "~Wrapper", "~ spacer"
//   • a leading word         →  "unwrap row", "passthrough / group", "pass-through"
// The leading-token rule mirrors the canonical KIND-tag convention above; the
// '~' prefix is the ergonomic quick-mark. Matching is anchored to the start so a
// node merely *containing* the word (e.g. "Password") never trips it.
// ---------------------------------------------------------------------------
export function isPassthroughName(name: string): boolean {
  const raw = (name || '').trim();
  if (!raw) return false;
  if (raw.startsWith('~')) return true;
  const compact = raw.toLowerCase().replace(/[^a-z]/g, '');
  return compact.startsWith('passthrough') || compact.startsWith('unwrap');
}
