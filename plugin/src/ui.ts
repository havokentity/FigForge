// =============================================================================
// FigForge — UI thread (iframe)
// =============================================================================

import type { ElementConfig, ExportOptions, ExportScale, TreeNode } from './types';

declare const JSZip: any;
declare const __FIGFORGE_VERSION__: string; // injected by esbuild from package.json

const $ = <T extends HTMLElement = HTMLElement>(sel: string) => document.querySelector(sel) as T;
function post(msg: Record<string, unknown>) {
  parent.postMessage({ pluginMessage: msg }, '*');
}

// Show the real version in the header pill.
$('#version').textContent = 'v' + __FIGFORGE_VERSION__;

// ---- per-element config state ----
const excluded = new Set<string>();
const merged = new Set<string>();
const forcedPng = new Set<string>();
let currentTree: TreeNode | null = null;
let selectedId: string | null = null;
let expandedNodes = new Set<string>();

type TreeFilter = 'all' | 'exported' | 'hidden' | 'merged' | 'canonical';
type PreviewZoomMode = 'fit' | 'actual' | 'manual';
interface UiPrefs {
  scale?: string;
  chips?: Record<string, boolean>;
  componentsPage?: boolean;
  unityPort?: string;
  fontFaceDilate?: string;
  windowPreset?: string;
  mcpDesired?: boolean;
  treeFilter?: TreeFilter;
}

const PREFS_KEY = 'figforge.ui.prefs';
const DEFAULT_FONT_FACE_DILATE = 0.15;
const TREE_FILTERS = new Set<TreeFilter>(['all', 'exported', 'hidden', 'merged', 'canonical']);
const hasTreeFilters = Boolean(document.getElementById('treeFilters'));
let activeTreeFilter: TreeFilter = hasTreeFilters ? readTreeFilter(readPrefs().treeFilter) : 'all';
let previewImg: HTMLImageElement | null = null;
let previewObjectUrl: string | null = null;
let previewZoom = 1;
let previewZoomMode: PreviewZoomMode = 'fit';

function readPrefs(): UiPrefs {
  try {
    const raw = localStorage.getItem(PREFS_KEY);
    if (!raw) return {};
    const parsed = JSON.parse(raw);
    return parsed && typeof parsed === 'object' ? parsed : {};
  } catch {
    return {};
  }
}

function savePrefs(patch: UiPrefs) {
  try {
    localStorage.setItem(PREFS_KEY, JSON.stringify({ ...readPrefs(), ...patch }));
  } catch {
    // Figma can deny storage in some runtimes; preferences are best-effort.
  }
}

function readTreeFilter(value: unknown): TreeFilter {
  return typeof value === 'string' && TREE_FILTERS.has(value as TreeFilter) ? (value as TreeFilter) : 'all';
}

// ---------------------------------------------------------------------------
// Header chrome
// ---------------------------------------------------------------------------
const savedPreset = readPrefs().windowPreset;
let lastPreset = savedPreset === 'S' || savedPreset === 'L' ? savedPreset : 'M';
function setSizePresetUi(preset: string) {
  document.querySelectorAll('#sizeSeg button').forEach((x) => {
    x.classList.toggle('active', (x as HTMLElement).dataset.size === preset);
  });
}
setSizePresetUi(lastPreset);
if (lastPreset !== 'M') post({ type: 'resize-ui', preset: lastPreset });
document.querySelectorAll('#sizeSeg button').forEach((b) =>
  b.addEventListener('click', () => {
    lastPreset = (b as HTMLElement).dataset.size!;
    setSizePresetUi(lastPreset);
    savePrefs({ windowPreset: lastPreset });
    post({ type: 'resize-ui', preset: lastPreset });
  })
);

let minimized = false;
const minBtn = $('#minBtn');
minBtn.addEventListener('click', () => {
  minimized = !minimized;
  document.body.classList.toggle('minimized', minimized);
  minBtn.textContent = minimized ? '⤢' : '—';
  minBtn.title = minimized ? 'Restore' : 'Minimize';
  post({ type: 'resize-ui', preset: minimized ? 'mini' : lastPreset });
});

$('#reloadBtn').addEventListener('click', () => post({ type: 'reload' }));
$('#clearLog').addEventListener('click', clearLog);
const savedScale = readPrefs().scale;
if (savedScale) {
  const scaleEl = $('#scale') as HTMLSelectElement;
  if ([...scaleEl.options].some((o) => o.value === savedScale)) scaleEl.value = savedScale;
}
$('#scale').addEventListener('change', () => savePrefs({ scale: ($('#scale') as HTMLSelectElement).value }));
const compPageOn = () => {
  const el = document.getElementById('componentsPageChk') as HTMLInputElement | null;
  return el ? el.checked : true; // default ON
};
const componentsPageChk = document.getElementById('componentsPageChk') as HTMLInputElement | null;
if (componentsPageChk) {
  const saved = readPrefs().componentsPage;
  if (typeof saved === 'boolean') componentsPageChk.checked = saved;
  componentsPageChk.closest('.chip')?.classList.toggle('on', componentsPageChk.checked);
  componentsPageChk.addEventListener('change', () => {
    componentsPageChk.closest('.chip')?.classList.toggle('on', componentsPageChk.checked);
    savePrefs({ componentsPage: componentsPageChk.checked });
  });
}
const unityPortEl = document.getElementById('unityPort') as HTMLInputElement | null;
if (unityPortEl) {
  const saved = readPrefs().unityPort;
  if (saved) unityPortEl.value = saved;
  unityPortEl.addEventListener('input', () => savePrefs({ unityPort: unityPortEl.value }));
}
const fontDilateEl = document.getElementById('fontFaceDilate') as HTMLInputElement | null;
if (fontDilateEl) {
  const saved = readPrefs().fontFaceDilate;
  if (saved) fontDilateEl.value = saved;
  fontDilateEl.addEventListener('input', () => savePrefs({ fontFaceDilate: fontDilateEl.value }));
}
$('#createBtnBtn').addEventListener('click', () => {
  setStatus('Creating button component…');
  post({ type: 'create-button', componentsPage: compPageOn() });
});
for (const [id, kind] of [
  ['createToggleBtn', 'toggle'], ['createRadioBtn', 'radio'],
  ['createInputBtn', 'input'],
  ['createDropdownBtn', 'dropdown'],
  ['createSliderBtn', 'slider'],
] as const) {
  $(`#${id}`).addEventListener('click', () => {
    setStatus(`Creating ${kind} component…`);
    post({ type: 'create-canonical', kind, componentsPage: compPageOn() });
  });
}

// List variants: the ＋List chip opens a floating popover of part toggles
// (header/icons/subtitles); 'Add List' creates that combination (each combo is
// its own master). Anchored under the chip; closes on outside click or Escape.
const listOptsEl = document.getElementById('listOpts') as HTMLElement | null;
const listBtnEl = $('#createListBtn') as HTMLElement;
// Hide = play the reverse spring (.closing → list-pop-out), THEN set hidden when it
// ends. Keyed by animation name so a reopen mid-close never strands the popover.
function hideListOpts() {
  if (!listOptsEl || listOptsEl.hasAttribute('hidden') || listOptsEl.classList.contains('closing')) return;
  listOptsEl.classList.add('closing');
}
listOptsEl?.addEventListener('animationend', (e) => {
  if ((e as AnimationEvent).animationName !== 'list-pop-out' || !listOptsEl) return;
  listOptsEl.classList.remove('closing');
  listOptsEl.setAttribute('hidden', '');
});
listBtnEl.addEventListener('click', (e) => {
  e.stopPropagation();
  if (!listOptsEl) return;
  if (!listOptsEl.hasAttribute('hidden') && !listOptsEl.classList.contains('closing')) { hideListOpts(); return; }
  listOptsEl.classList.remove('closing'); // reopening mid-close: cancel the out-animation
  listOptsEl.removeAttribute('hidden');
  // Anchor below the chip (offsets are relative to .create-group, the positioned
  // ancestor), clamped so the popover never overflows the group's right edge.
  const group = listOptsEl.offsetParent as HTMLElement | null;
  const maxLeft = group ? Math.max(0, group.clientWidth - listOptsEl.offsetWidth - 4) : 0;
  listOptsEl.style.left = Math.min(listBtnEl.offsetLeft, maxLeft) + 'px';
  listOptsEl.style.top = listBtnEl.offsetTop + listBtnEl.offsetHeight + 6 + 'px';
});
listOptsEl?.addEventListener('click', (e) => e.stopPropagation()); // clicks inside don't close it
document.addEventListener('click', hideListOpts);
document.addEventListener('keydown', (e) => { if (e.key === 'Escape') hideListOpts(); });
$('#listOptsCreate').addEventListener('click', () => {
  const on = (id: string) => (document.getElementById(id) as HTMLInputElement | null)?.checked !== false;
  const sbRaw = parseInt((document.getElementById('listOptSbWidth') as HTMLInputElement | null)?.value ?? '10', 10);
  const scrollbarWidth = isNaN(sbRaw) ? 10 : Math.min(40, Math.max(2, sbRaw));
  hideListOpts();
  setStatus('Creating list component…');
  post({
    type: 'create-canonical', kind: 'list', componentsPage: compPageOn(),
    listOpts: { header: on('listOptHeader'), icon: on('listOptIcon'), subtitle: on('listOptSubtitle'), scrollbarWidth },
  });
});

// option chips
function wireChip(id: string) {
  const el = $(`#${id}`);
  const input = el.querySelector('input') as HTMLInputElement;
  const saved = readPrefs().chips?.[id];
  if (typeof saved === 'boolean') input.checked = saved;
  el.classList.toggle('on', input.checked);
  el.addEventListener('click', (e) => {
    if (e.target !== input) {
      e.preventDefault();
      input.checked = !input.checked;
    }
    el.classList.toggle('on', input.checked);
    savePrefs({ chips: { ...(readPrefs().chips || {}), [id]: input.checked } });
  });
}
['optAutoMerge', 'optGradients', 'optRasterStroke'].forEach(wireChip);

// ---------------------------------------------------------------------------
// Tree rendering
// ---------------------------------------------------------------------------
const TYPE_SHORT: Record<string, string> = {
  FRAME: 'FRM', GROUP: 'GRP', TEXT: 'TXT', VECTOR: 'VEC', INSTANCE: 'INS',
  COMPONENT: 'CMP', RECTANGLE: 'RECT', ELLIPSE: 'ELL', LINE: 'LN',
};

function renderTree() {
  const host = $('#tree');
  host.innerHTML = '';
  updateTreeStats();
  updateTreeFilterUi();
  if (!currentTree) {
    host.innerHTML = '<div class="empty">Select a frame in Figma.</div>';
    return;
  }
  const query = ($('#search') as HTMLInputElement).value.trim().toLowerCase();
  const revealingMatches = Boolean(query) || activeTreeFilter !== 'all';

  const walk = (node: TreeNode): HTMLElement | null => {
    const children = node.children || [];
    const expanded = expandedNodes.has(node.id);
    const nameMatch = !query || node.displayName.toLowerCase().includes(query);
    const filterMatch = matchesTreeFilter(node);
    const selfMatch = nameMatch && filterMatch;
    const childEls = (revealingMatches || expanded) ? children.map(walk).filter(Boolean) as HTMLElement[] : [];
    if (!selfMatch && childEls.length === 0) return null;

    const row = document.createElement('div');
    row.className = 'row';
    if (node.id === selectedId) row.classList.add('sel');
    if (excluded.has(node.id) || !node.visible) row.classList.add('hidden');
    row.style.paddingLeft = `${8 + node.depth * 12}px`;

    row.innerHTML = `
      <span class="twirl" title="${children.length ? 'Expand/collapse' : ''}">${children.length ? (expanded || revealingMatches ? '▾' : '▸') : ''}</span>
      <span class="type-tag">${TYPE_SHORT[node.type] || node.type.slice(0, 3)}</span>
      <span class="tname">${escapeHtml(node.displayName)}</span>
      ${node.canonicalRef ? `<span class="canon-tag" title="canonical: ${escapeHtml(node.canonicalRef)}">${escapeHtml(node.canonicalRef)}</span>` : ''}
    `;
    const twirl = row.querySelector('.twirl') as HTMLElement | null;
    if (twirl && children.length) {
      twirl.style.cursor = 'pointer';
      twirl.addEventListener('click', (e) => {
        e.stopPropagation();
        toggle(expandedNodes, node.id);
        renderTree();
      });
    }

    const eye = miniBtn('👁', excluded.has(node.id) ? '' : 'on', 'Toggle export', () => {
      toggle(excluded, node.id); post({ type: 'toggle-visibility', nodeId: node.id }); renderTree();
    });
    row.appendChild(eye);

    if (node.canMerge) {
      row.appendChild(miniBtn('⊞', merged.has(node.id) ? 'on' : '', 'Merge into one PNG', () => {
        toggle(merged, node.id); post({ type: 'toggle-merge', nodeId: node.id }); renderTree();
      }));
    }
    if (node.canExportPng && node.type === 'TEXT') {
      row.appendChild(miniBtn('P', forcedPng.has(node.id) ? 'on png' : 'png', 'Rasterize as PNG', () => {
        toggle(forcedPng, node.id); post({ type: 'toggle-png', nodeId: node.id }); renderTree();
      }));
    }

    row.addEventListener('click', () => {
      selectedId = node.id;
      post({ type: 'highlight-element', nodeId: node.id });
      post({ type: 'preview-element', nodeId: node.id });
      renderTree();
    });

    const frag = document.createElement('div');
    frag.appendChild(row);
    childEls.forEach((c) => frag.appendChild(c));
    return frag;
  };

  const tree = walk(currentTree);
  if (tree) host.appendChild(tree);
  else host.innerHTML = '<div class="empty">No matching layers.</div>';
}

function primeExpandedNodes(root: TreeNode) {
  expandedNodes = new Set([root.id]);
}

function miniBtn(label: string, cls: string, title: string, onClick: () => void): HTMLButtonElement {
  const b = document.createElement('button');
  b.className = `mini ${cls}`;
  b.textContent = label;
  b.title = title;
  b.addEventListener('click', (e) => { e.stopPropagation(); onClick(); });
  return b;
}
function toggle(set: Set<string>, id: string) { set.has(id) ? set.delete(id) : set.add(id); }
function escapeHtml(s: string) { return s.replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]!)); }

function matchesTreeFilter(node: TreeNode): boolean {
  switch (activeTreeFilter) {
    case 'exported': return !excluded.has(node.id) && node.visible;
    case 'hidden': return excluded.has(node.id) || !node.visible;
    case 'merged': return merged.has(node.id);
    case 'canonical': return Boolean(node.canonicalRef);
    default: return true;
  }
}

function updateTreeStats() {
  const el = document.getElementById('treeStats');
  if (!el) return;
  if (!currentTree) {
    el.textContent = '';
    return;
  }
  const stats = { total: 0, exported: 0, excluded: 0, merged: 0, raster: 0 };
  const walk = (node: TreeNode) => {
    stats.total += 1;
    if (!excluded.has(node.id) && node.visible) stats.exported += 1;
    if (excluded.has(node.id) || !node.visible) stats.excluded += 1;
    if (merged.has(node.id)) stats.merged += 1;
    if (forcedPng.has(node.id)) stats.raster += 1;
    (node.children || []).forEach(walk);
  };
  walk(currentTree);
  el.textContent = `total ${stats.total} · exported ${stats.exported} · excluded ${stats.excluded} · merged ${stats.merged} · raster ${stats.raster}`;
}

function updateTreeFilterUi() {
  const host = document.getElementById('treeFilters');
  if (!host) return;
  host.querySelectorAll<HTMLButtonElement>('button[data-filter]').forEach((btn) => {
    btn.classList.toggle('active', btn.dataset.filter === activeTreeFilter);
  });
}

function wireTreeFilters() {
  const host = document.getElementById('treeFilters');
  if (!host) return;
  host.addEventListener('click', (e) => {
    const target = e.target;
    const btn = target instanceof HTMLElement ? target.closest<HTMLButtonElement>('button[data-filter]') : null;
    if (!btn) return;
    activeTreeFilter = readTreeFilter(btn.dataset.filter);
    savePrefs({ treeFilter: activeTreeFilter });
    renderTree();
  });
  updateTreeFilterUi();
}

$('#search').addEventListener('input', renderTree);
wireTreeFilters();

// ---------------------------------------------------------------------------
// Preview zoom
// ---------------------------------------------------------------------------
function updatePreviewZoom() {
  if (!previewImg) return;
  if (previewZoomMode === 'fit') {
    previewImg.style.width = '';
    previewImg.style.height = '';
    previewImg.style.maxWidth = '100%';
    previewImg.style.maxHeight = '100%';
    setPreviewZoomLabel('Fit');
    return;
  }
  previewImg.style.maxWidth = 'none';
  previewImg.style.maxHeight = 'none';
  previewImg.style.width = `${Math.max(1, Math.round(previewImg.naturalWidth * previewZoom))}px`;
  previewImg.style.height = 'auto';
  setPreviewZoomLabel(`${Math.round(previewZoom * 100)}%`);
}

function setPreviewZoomLabel(text: string) {
  const label = document.getElementById('previewZoomLabel');
  if (label) label.textContent = text;
}

function setPreviewZoom(mode: PreviewZoomMode, zoom = previewZoom) {
  previewZoomMode = mode;
  previewZoom = Math.min(4, Math.max(0.1, zoom));
  updatePreviewZoom();
}

function wirePreviewZoom() {
  document.getElementById('previewFit')?.addEventListener('click', () => setPreviewZoom('fit', 1));
  document.getElementById('previewActual')?.addEventListener('click', () => setPreviewZoom('actual', 1));
  document.getElementById('previewZoomOut')?.addEventListener('click', () => setPreviewZoom('manual', previewZoomMode === 'fit' ? 0.9 : previewZoom - 0.1));
  document.getElementById('previewZoomIn')?.addEventListener('click', () => setPreviewZoom('manual', previewZoomMode === 'fit' ? 1.1 : previewZoom + 0.1));
  setPreviewZoomLabel('Fit');
}
wirePreviewZoom();

// ---------------------------------------------------------------------------
// Export
// ---------------------------------------------------------------------------
function parseScale(): ExportScale {
  const v = ($('#scale') as HTMLSelectElement).value;
  const [t, n] = v.split(':');
  const value = parseFloat(n);
  if (t === 'w') return { type: 'width', value };
  if (t === 'h') return { type: 'height', value };
  return { type: 'scale', value };
}

function collectConfigs(): ElementConfig[] {
  const ids = new Set<string>([...excluded, ...merged, ...forcedPng]);
  return [...ids].map((id) => ({
    id,
    excluded: excluded.has(id),
    merged: merged.has(id),
    rasterize: forcedPng.has(id),
  }));
}

function readFontFaceDilate(): number {
  const raw = (document.getElementById('fontFaceDilate') as HTMLInputElement | null)?.value;
  const value = parseFloat(raw || '');
  if (!Number.isFinite(value)) return DEFAULT_FONT_FACE_DILATE;
  return Math.min(1, Math.max(0, value));
}

function currentOptions(): ExportOptions {
  return {
    autoMerge: ($('#optAutoMerge input') as HTMLInputElement).checked,
    rasterizeStrokes: ($('#optRasterStroke input') as HTMLInputElement).checked,
    emitGradients: ($('#optGradients input') as HTMLInputElement).checked,
    emitImageFills: true,
    fontFaceDilate: readFontFaceDilate(),
  };
}

$('#exportBtn').addEventListener('click', () => {
  frameExportTarget = 'zip';
  setStatus('Exporting…');
  showProgress(true);
  post({ type: 'export', scale: parseScale(), options: currentOptions(), elementConfigs: collectConfigs() });
});

// Where the next page export goes: download a .zip, or POST straight to Unity.
let pageExportTarget: 'zip' | 'unity' = 'zip';
let frameExportTarget: 'zip' | 'unity' = 'zip';

$('#exportPageBtn').addEventListener('click', () => {
  pageExportTarget = 'zip';
  setStatus('Exporting whole page…');
  showProgress(true);
  post({ type: 'export-page', scale: parseScale(), options: currentOptions() });
});

$('#exportUnityBtn').addEventListener('click', () => {
  pageExportTarget = 'unity';
  setStatus('Exporting page → Unity…');
  showProgress(true);
  post({ type: 'export-page', scale: parseScale(), options: currentOptions() });
});

$('#exportFrameUnityBtn').addEventListener('click', () => {
  frameExportTarget = 'unity';
  setStatus('Exporting frame → Unity…');
  showProgress(true);
  post({ type: 'export', scale: parseScale(), options: currentOptions(), elementConfigs: collectConfigs() });
});

function unityImportUrl(): string {
  const raw = ($('#unityPort') as HTMLInputElement)?.value;
  const port = Math.min(65535, Math.max(1024, parseInt(raw, 10) || 1995));
  return `http://127.0.0.1:${port}/import`;
}

async function sendToUnity(project: { name: string; initial: string }, screens: PageScreen[]) {
  const url = unityImportUrl();
  try {
    const res = await fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ project, screens }),
    });
    if (res.ok) setStatus(`Sent ${screens.length} screen(s) → Unity. Building in the FigForge importer.`);
    else setStatus(`Unity refused the import (HTTP ${res.status}).`, true);
  } catch (e) {
    setStatus(`Couldn't reach Unity at ${url} — is the FigForge importer open with live import enabled?`, true);
  }
}

function screenFromManifest(manifestJson: string): string {
  try {
    const parsed = JSON.parse(manifestJson);
    return parsed?.screen?.name || 'screen';
  } catch {
    return 'screen';
  }
}

async function downloadBundle(manifestJson: string, assets: { name: string; data: number[] }[]) {
  const zip = new JSZip();
  zip.file('manifest.json', manifestJson);
  for (const a of assets) zip.file(a.name, new Uint8Array(a.data));
  const blob = await zip.generateAsync({ type: 'blob' });
  const url = URL.createObjectURL(blob);
  const screen = JSON.parse(manifestJson).screen?.name || 'figforge';
  const a = document.createElement('a');
  a.href = url;
  a.download = `${screen}_figforge.zip`;
  a.click();
  URL.revokeObjectURL(url);
}

interface PageScreen {
  name: string;
  manifest: string;
  assets: { name: string; data: number[] }[];
  section?: string;
  role?: string;
}
async function downloadProjectBundle(project: { name: string; initial: string }, screens: PageScreen[]) {
  const zip = new JSZip();
  const used = new Set<string>();
  const index = {
    schema: 'figforge/project',
    version: '1.0',
    generator: 'FigForge',
    name: project.name,
    exportedAt: new Date().toISOString(),
    initial: project.initial,
    screens: [] as { name: string; manifest: string; section: string; role: string }[],
  };
  for (const s of screens) {
    let folder = s.name || 'screen';
    let n = 1;
    while (used.has(folder)) folder = `${s.name}_${n++}`;
    used.add(folder);
    zip.file(`${folder}/manifest.json`, s.manifest);
    for (const a of s.assets) zip.file(`${folder}/${a.name}`, new Uint8Array(a.data));
    index.screens.push({ name: s.name, manifest: `${folder}/manifest.json`, section: s.section || '', role: s.role || 'screen' });
  }
  zip.file('project.json', JSON.stringify(index, null, 2));
  const blob = await zip.generateAsync({ type: 'blob' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = `${(project.name || 'figforge').replace(/[^a-z0-9]+/gi, '_')}_page.zip`;
  a.click();
  URL.revokeObjectURL(url);
}

// ---------------------------------------------------------------------------
// Status / progress helpers
// ---------------------------------------------------------------------------
function clockTime(): string {
  const d = new Date();
  const p = (n: number) => n.toString().padStart(2, '0');
  return `${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
}

let lastLogReplaceable = false;
// Append a line to the scrollable status / bundle / Unity log. `replace` rewrites
// the previous line in place (used for rapid progress ticks so they don't flood).
// Auto-scrolls only when the user is already at the bottom, so reading history isn't
// yanked away. Full text wraps — nothing is truncated.
function appendLog(text: string, opts: { err?: boolean; dim?: boolean; ok?: boolean; replace?: boolean } = {}) {
  const el = $('#status');
  const atBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 24;
  let line = opts.replace && lastLogReplaceable && el.lastElementChild ? (el.lastElementChild as HTMLElement) : null;
  if (line) line.textContent = '';
  else { line = document.createElement('div'); el.appendChild(line); }
  line.className = 'log-line' + (opts.err ? ' err' : opts.ok ? ' ok' : opts.dim ? ' dim' : '');
  const ts = document.createElement('span'); ts.className = 'ts'; ts.textContent = clockTime();
  const msg = document.createElement('span'); msg.className = 'msg'; msg.textContent = text;
  line.appendChild(ts); line.appendChild(msg);
  lastLogReplaceable = !!opts.replace;
  while (el.childElementCount > 300) el.removeChild(el.firstElementChild as Node);
  if (atBottom) el.scrollTop = el.scrollHeight;
}

function setStatus(text: string, err = false, replace = false) { appendLog(text, { err, replace }); }
function clearLog() { $('#status').textContent = ''; lastLogReplaceable = false; }
function showProgress(show: boolean) { $('#progress').classList.toggle('show', show); }
function setProgress(pct: number) { ($('#progress > div') as HTMLElement).style.width = `${pct}%`; }

// ---------------------------------------------------------------------------
// MCP bridge — a WebSocket client to the local FigForge bridge server. One
// start/stop toggle in the header; the dot shows the live connection state.
// While "on" it auto-reconnects, so it goes green as soon as the server is up.
// ---------------------------------------------------------------------------
const BRIDGE_URL = 'ws://127.0.0.1:1994/ws';
let socket: WebSocket | null = null;
let wantConnected = false;
let reconnectTimer: number | null = null;

function bridgeLog(line: string) { console.log('[FigForge MCP]', line); appendLog(line, { dim: true }); }

type McpState = 'off' | 'connecting' | 'connected';
function setMcp(state: McpState) {
  const dot = $('#mcpDot');
  const ctl = $('#mcpCtl');
  dot.classList.toggle('on', state === 'connected');
  dot.classList.toggle('connecting', state === 'connecting');
  ctl.classList.toggle('connected', state === 'connected');
  ctl.classList.toggle('connecting', state === 'connecting');
  $('#mcpLabel').textContent =
    state === 'connected' ? 'Disconnect' : state === 'connecting' ? 'Connecting…' : 'Connect MCP';
}

function scheduleReconnect() {
  if (reconnectTimer !== null) return;
  reconnectTimer = window.setTimeout(() => {
    reconnectTimer = null;
    if (wantConnected) connectMcp();
  }, 2000);
}

function connectMcp() {
  if (socket) return;
  setMcp('connecting');
  try {
    socket = new WebSocket(BRIDGE_URL);
    socket.onopen = () => { bridgeLog('connected'); setMcp('connected'); };
    socket.onclose = () => {
      socket = null;
      if (wantConnected) { setMcp('connecting'); scheduleReconnect(); }
      else setMcp('off');
    };
    socket.onerror = () => bridgeLog('socket error');
    socket.onmessage = (ev) => {
      try { post({ type: 'mcp-request', payload: JSON.parse(ev.data) }); }
      catch { bridgeLog('bad message from server'); }
    };
  } catch (e) {
    bridgeLog('connect failed: ' + String(e));
    if (wantConnected) scheduleReconnect();
  }
}

function disconnectMcp() {
  wantConnected = false;
  if (reconnectTimer !== null) { clearTimeout(reconnectTimer); reconnectTimer = null; }
  if (socket) { socket.close(); socket = null; }
  setMcp('off');
}

$('#mcpCtl').addEventListener('click', () => {
  if (wantConnected) {
    savePrefs({ mcpDesired: false });
    disconnectMcp();
  } else {
    wantConnected = true;
    savePrefs({ mcpDesired: true });
    connectMcp();
  }
});
setMcp('off');
if (readPrefs().mcpDesired) {
  wantConnected = true;
  connectMcp();
}

// ---------------------------------------------------------------------------
// main → ui
// ---------------------------------------------------------------------------
window.onmessage = (event: MessageEvent) => {
  const msg = (event.data && event.data.pluginMessage) || event.data;
  if (!msg || !msg.type) return;

  switch (msg.type) {
    case 'selection-info':
      const previousRootId = currentTree?.id;
      currentTree = msg.tree;
      selectedId = null;
      if (currentTree && currentTree.id !== previousRootId) primeExpandedNodes(currentTree);
      setStatus(`${msg.name} · ${msg.elementCount} layers`);
      ($('#exportBtn') as HTMLButtonElement).disabled = false;
      ($('#exportFrameUnityBtn') as HTMLButtonElement).disabled = false;
      renderTree();
      break;

    case 'no-selection':
      currentTree = null;
      ($('#exportBtn') as HTMLButtonElement).disabled = true;
      ($('#exportFrameUnityBtn') as HTMLButtonElement).disabled = true;
      expandedNodes = new Set();
      setStatus('Select a frame, component, or group.');
      renderTree();
      break;

    case 'element-preview': {
      if (previewObjectUrl) URL.revokeObjectURL(previewObjectUrl);
      const blob = new Blob([new Uint8Array(msg.imageData)], { type: 'image/png' });
      const url = URL.createObjectURL(blob);
      previewObjectUrl = url;
      $('#previewHead').textContent = `${msg.name} · ${msg.figmaType} · ${Math.round(msg.size.w)}×${Math.round(msg.size.h)}`;
      $('#previewWrap').innerHTML = '';
      const img = document.createElement('img');
      img.src = url;
      previewImg = img;
      img.onload = () => updatePreviewZoom();
      $('#previewWrap').appendChild(img);
      updatePreviewZoom();
      break;
    }

    case 'progress':
      setProgress(msg.total ? Math.round((msg.current / msg.total) * 100) : 0);
      setStatus(`Exporting ${msg.label} (${msg.current}/${msg.total})`, false, true); // replace in place
      break;

    case 'export-complete':
      showProgress(false);
      setProgress(0);
      if (frameExportTarget === 'unity') {
        const screenName = screenFromManifest(msg.manifest);
        frameExportTarget = 'zip';
        sendToUnity(
          { name: screenName, initial: screenName },
          [{ name: screenName, manifest: msg.manifest, assets: msg.assets, role: 'screen' }]
        );
      } else {
        downloadBundle(msg.manifest, msg.assets);
        setStatus(`Exported ${msg.assets.length} asset(s). Bundle downloaded.`);
      }
      break;

    case 'export-page-complete':
      showProgress(false);
      setProgress(0);
      if (pageExportTarget === 'unity') {
        sendToUnity(msg.project, msg.screens);
      } else {
        downloadProjectBundle(msg.project, msg.screens);
        setStatus(`Exported ${msg.screens.length} screen(s). Project bundle downloaded.`);
      }
      break;

    case 'export-error':
      showProgress(false);
      frameExportTarget = 'zip';
      setStatus(msg.message, true);
      break;

    case 'status':
      setStatus(msg.message);
      break;

    case 'mcp-response':
      if (socket && socket.readyState === WebSocket.OPEN) {
        socket.send(JSON.stringify(msg.payload));
        bridgeLog(`→ ${msg.payload.type} ${msg.payload.error ? 'ERR' : 'ok'}`);
      }
      break;
  }
};
