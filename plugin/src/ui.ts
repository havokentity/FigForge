// =============================================================================
// FigForge — UI thread (iframe)
// =============================================================================

import { MANIFEST_VERSION, type BinaryAsset, type ElementConfig, type ExportOptions, type ExportScale, type TreeNode } from './types';
import { bytesToBase64 } from './base64';

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
  unityToken?: string;
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
const unityTokenEl = document.getElementById('unityToken') as HTMLInputElement | null;
if (unityTokenEl) {
  const el = unityTokenEl;
  const saved = readPrefs().unityToken;
  if (saved) el.value = saved;
  const looksLikeToken = (s: string) => /^[0-9a-f]{32}$/i.test(s);
  const applyToken = (raw: string) => {
    const t = raw.trim();
    el.value = t;
    savePrefs({ unityToken: t });
    if (looksLikeToken(t)) setStatus('Live-import token set.');
  };
  el.addEventListener('input', () => savePrefs({ unityToken: el.value.trim() }));
  // Reliable path: a real ⌘/Ctrl-V paste. Figma's plugin sandbox delivers clipboard
  // data on an actual paste keystroke (unlike navigator.clipboard.readText, which it
  // blocks). When the pasted text is a token we own the update so it lands clean.
  el.addEventListener('paste', (e) => {
    const text = e.clipboardData?.getData('text');
    if (text && looksLikeToken(text.trim())) {
      e.preventDefault();
      applyToken(text);
      el.select();
    }
  });
  // Bonus: hosts that DO grant clipboard-read (e.g. a browser preview) auto-fill on
  // click. Figma blocks this, so it silently no-ops there — paste with ⌘V instead.
  el.addEventListener('focus', async () => {
    el.select();
    try {
      const clip = (await navigator.clipboard?.readText())?.trim();
      if (clip && looksLikeToken(clip) && clip !== el.value.trim()) applyToken(clip);
    } catch {
      // clipboard read blocked by the sandbox — use ⌘V.
    }
  });
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
] as const) {
  $(`#${id}`).addEventListener('click', () => {
    setStatus(`Creating ${kind} component…`);
    post({ type: 'create-canonical', kind, componentsPage: compPageOn() });
  });
}

// Floating options popover (＋List, ＋Slider): the chip toggles it, anchored under
// the chip; closes on outside click or Escape; 'Add …' creates that combination
// (each combo is its own master). Hide = play the reverse spring (.closing →
// list-pop-out), THEN set hidden when it ends — keyed by animation name so a
// reopen mid-close never strands the popover.
const popoverHiders: (() => void)[] = [];
function wirePopover(btn: HTMLElement, pop: HTMLElement | null): { hide: () => void } {
  function hide() {
    if (!pop || pop.hasAttribute('hidden') || pop.classList.contains('closing')) return;
    pop.classList.add('closing');
  }
  popoverHiders.push(hide);
  pop?.addEventListener('animationend', (e) => {
    if ((e as AnimationEvent).animationName !== 'list-pop-out' || !pop) return;
    pop.classList.remove('closing');
    pop.setAttribute('hidden', '');
  });
  btn.addEventListener('click', (e) => {
    e.stopPropagation();
    if (!pop) return;
    if (!pop.hasAttribute('hidden') && !pop.classList.contains('closing')) { hide(); return; }
    for (const h of popoverHiders) if (h !== hide) h(); // one popover at a time
    pop.classList.remove('closing'); // reopening mid-close: cancel the out-animation
    pop.removeAttribute('hidden');
    // Anchor below the chip (offsets are relative to .create-group, the positioned
    // ancestor), clamped so the popover never overflows the group's right edge.
    const group = pop.offsetParent as HTMLElement | null;
    const maxLeft = group ? Math.max(0, group.clientWidth - pop.offsetWidth - 4) : 0;
    pop.style.left = Math.min(btn.offsetLeft, maxLeft) + 'px';
    pop.style.top = btn.offsetTop + btn.offsetHeight + 6 + 'px';
  });
  pop?.addEventListener('click', (e) => e.stopPropagation()); // clicks inside don't close it
  document.addEventListener('click', hide);
  document.addEventListener('keydown', (e) => { if (e.key === 'Escape') hide(); });
  return { hide };
}

// List variants: part toggles (header/icons/subtitles) + scrollbar width.
const listPop = wirePopover($('#createListBtn') as HTMLElement, document.getElementById('listOpts'));
$('#listOptsCreate').addEventListener('click', () => {
  const on = (id: string) => (document.getElementById(id) as HTMLInputElement | null)?.checked !== false;
  const sbRaw = parseInt((document.getElementById('listOptSbWidth') as HTMLInputElement | null)?.value ?? '10', 10);
  const scrollbarWidth = isNaN(sbRaw) ? 10 : Math.min(40, Math.max(2, sbRaw));
  listPop.hide();
  setStatus('Creating list component…');
  post({
    type: 'create-canonical', kind: 'list', componentsPage: compPageOn(),
    listOpts: { header: on('listOptHeader'), icon: on('listOptIcon'), subtitle: on('listOptSubtitle'), scrollbarWidth },
  });
});

// Slider variants: custom value range (start/end) and/or slotted snapping (slot
// count). The Start/End/Slots inputs enable with their checkbox so the defaults
// (0..1, continuous) stay obvious.
const sliderPop = wirePopover($('#createSliderBtn') as HTMLElement, document.getElementById('sliderOpts'));
const sliderChecked = (id: string) => (document.getElementById(id) as HTMLInputElement | null)?.checked === true;
function syncSliderOptInputs() {
  const range = sliderChecked('sliderOptRange'), slotted = sliderChecked('sliderOptSlotted');
  for (const [id, en] of [['sliderOptMin', range], ['sliderOptMax', range], ['sliderOptSlots', slotted]] as const) {
    const el = document.getElementById(id) as HTMLInputElement | null;
    if (el) el.disabled = !en;
  }
}
document.getElementById('sliderOptRange')?.addEventListener('change', syncSliderOptInputs);
document.getElementById('sliderOptSlotted')?.addEventListener('change', syncSliderOptInputs);
$('#sliderOptsCreate').addEventListener('click', () => {
  const num = (id: string, def: number) => {
    const v = parseFloat((document.getElementById(id) as HTMLInputElement | null)?.value ?? '');
    return isNaN(v) ? def : v;
  };
  sliderPop.hide();
  setStatus('Creating slider component…');
  post({
    type: 'create-canonical', kind: 'slider', componentsPage: compPageOn(),
    sliderOpts: {
      range: sliderChecked('sliderOptRange'), min: num('sliderOptMin', 0), max: num('sliderOptMax', 100),
      slotted: sliderChecked('sliderOptSlotted'), slots: Math.round(num('sliderOptSlots', 5)),
      value: sliderChecked('sliderOptValue'),
    },
  });
});

// Progress variants: style (bar / ring / gauge / segments), indeterminate
// (animated), and/or a live percentage read-out. The Blocks input enables with
// the Segments style so the other styles' defaults stay obvious.
const progressPop = wirePopover($('#createProgressBtn') as HTMLElement, document.getElementById('progressOpts'));
const progressStyle = () =>
  (document.querySelector('input[name="progressStyle"]:checked') as HTMLInputElement | null)?.value ?? 'bar';
function syncProgressOptInputs() {
  const seg = document.getElementById('progressOptSegments') as HTMLInputElement | null;
  if (seg) seg.disabled = progressStyle() !== 'segments';
}
for (const r of Array.from(document.querySelectorAll('input[name="progressStyle"]'))) {
  r.addEventListener('change', syncProgressOptInputs);
}
$('#progressOptsCreate').addEventListener('click', () => {
  const on = (id: string) => (document.getElementById(id) as HTMLInputElement | null)?.checked === true;
  const segRaw = parseInt((document.getElementById('progressOptSegments') as HTMLInputElement | null)?.value ?? '10', 10);
  progressPop.hide();
  setStatus('Creating progress component…');
  post({
    type: 'create-canonical', kind: 'progress', componentsPage: compPageOn(),
    progressOpts: {
      style: progressStyle(),
      segments: isNaN(segRaw) ? 10 : Math.min(50, Math.max(2, segRaw)),
      indeterminate: on('progressOptIndet'), value: on('progressOptValue'),
    },
  });
});

// Table variants: rows × columns, header toggle, scrollbar width.
const tablePop = wirePopover($('#createTableBtn') as HTMLElement, document.getElementById('tableOpts'));
$('#tableOptsCreate').addEventListener('click', () => {
  const int = (id: string, def: number, lo: number, hi: number) => {
    const v = parseInt((document.getElementById(id) as HTMLInputElement | null)?.value ?? '', 10);
    return isNaN(v) ? def : Math.min(hi, Math.max(lo, v));
  };
  tablePop.hide();
  setStatus('Creating table component…');
  post({
    type: 'create-canonical', kind: 'table', componentsPage: compPageOn(),
    tableOpts: {
      rows: int('tableOptRows', 4, 1, 100), cols: int('tableOptCols', 3, 1, 12),
      header: (document.getElementById('tableOptHeader') as HTMLInputElement | null)?.checked !== false,
      scrollbarWidth: int('tableOptSbWidth', 10, 2, 40),
    },
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

// Where a finished export goes — a .zip download or a POST straight to Unity — is
// captured at click time and travels WITH the request (`target`, echoed back by
// main in export[-page]-complete). It used to live in module globals, which a
// second click could clobber while an export was still running: main now rejects
// the concurrent run, but the click would still have silently re-routed the
// first export's result (e.g. a Send-to-Unity page export downloading as a zip).
$('#exportBtn').addEventListener('click', () => {
  setStatus('Exporting…');
  showProgress(true);
  post({ type: 'export', target: 'zip', scale: parseScale(), options: currentOptions(), elementConfigs: collectConfigs() });
});

$('#exportPageBtn').addEventListener('click', () => {
  setStatus('Exporting whole page…');
  showProgress(true);
  // elementConfigs travel with page exports too — the per-element exclude/merge/
  // force-PNG choices saved on the elements apply to every frame on the page,
  // exactly as they would frame-by-frame (main re-applies them before the loop).
  post({ type: 'export-page', target: 'zip', scale: parseScale(), options: currentOptions(), elementConfigs: collectConfigs() });
});

$('#exportUnityBtn').addEventListener('click', () => {
  setStatus('Exporting page → Unity…');
  showProgress(true);
  post({ type: 'export-page', target: 'unity', scale: parseScale(), options: currentOptions(), elementConfigs: collectConfigs() });
});

$('#exportFrameUnityBtn').addEventListener('click', () => {
  setStatus('Exporting frame → Unity…');
  showProgress(true);
  post({ type: 'export', target: 'unity', scale: parseScale(), options: currentOptions(), elementConfigs: collectConfigs() });
});

function unityImportUrl(): string {
  const raw = ($('#unityPort') as HTMLInputElement)?.value;
  const port = Math.min(65535, Math.max(1024, parseInt(raw, 10) || 1995));
  return `http://127.0.0.1:${port}/import`;
}

function unityToken(): string {
  return ($('#unityToken') as HTMLInputElement)?.value.trim() || '';
}

async function sendToUnity(project: { name: string; initial: string }, screens: PageScreen[]) {
  const url = unityImportUrl();
  // JSON wire boundary (manifest version 2.0): each PNG goes as a base64 `b64`
  // string — ~1.33× the binary size, vs ~3.7× for the old `data` number[]
  // rendered as decimal text, and no 10× number[] heap copy beforehand. Unity's
  // FigForgeLiveImport.LiveAsset reads `b64` first and still accepts the legacy
  // `data` array from a 1.0-era plugin.
  const wireScreens = screens.map((s) => ({
    name: s.name,
    manifest: s.manifest,
    section: s.section,
    role: s.role,
    assets: s.assets.map((a) => ({ name: a.name, b64: bytesToBase64(a.data) })),
  }));
  try {
    const res = await fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-FigForge-Token': unityToken() },
      body: JSON.stringify({ project, screens: wireScreens }),
    });
    if (res.ok) setStatus(`Sent ${screens.length} screen(s) → Unity. Building in the FigForge importer.`);
    else if (res.status === 401)
      setStatus('Unity rejected the token — copy it from the FigForge importer (Live import → Plugin token) into the Token field.', true);
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

// JSZip arrives from cdnjs at runtime (a <script> tag in ui.html) — offline or
// behind a corporate proxy the tag fails and the global never exists. Checked
// BEFORE any zip work so the failure is an honest, actionable message instead of
// an unhandled throw after the UI had already claimed "Bundle downloaded".
function jszipAvailable(): boolean {
  if (typeof JSZip !== 'undefined') return true;
  setStatus('JSZip failed to load — check network/proxy; bundle not saved.', true);
  return false;
}

// Script tags load synchronously in order, so by the time this module runs the
// JSZip global either exists or never will — surface a blocked cdnjs load the
// moment the UI opens, not at download time. Unity send doesn't need JSZip.
if (typeof JSZip === 'undefined') {
  setStatus('JSZip failed to load from cdnjs (offline / proxy?) — Download Frame/Page will not save bundles; Send to Unity still works.', true);
}

async function downloadBundle(manifestJson: string, assets: BinaryAsset[]) {
  if (!jszipAvailable()) return;
  try {
    const zip = new JSZip();
    zip.file('manifest.json', manifestJson);
    for (const a of assets) zip.file(a.name, a.data); // JSZip takes Uint8Array natively
    const blob = await zip.generateAsync({ type: 'blob' });
    const url = URL.createObjectURL(blob);
    const screen = JSON.parse(manifestJson).screen?.name || 'figforge';
    const a = document.createElement('a');
    a.href = url;
    a.download = `${screen}_figforge.zip`;
    a.click();
    URL.revokeObjectURL(url);
    // Success is only claimed HERE — after the zip blob is handed to the browser.
    setStatus(`Exported ${assets.length} asset(s). Bundle downloaded.`);
  } catch (e) {
    setStatus(`Bundle packaging failed — ${String((e as Error)?.message || e)}; bundle not saved.`, true);
  }
}

interface PageScreen {
  name: string;
  manifest: string;
  assets: BinaryAsset[];
  section?: string;
  role?: string;
}
async function downloadProjectBundle(project: { name: string; initial: string }, screens: PageScreen[]) {
  if (!jszipAvailable()) return;
  try {
    const zip = new JSZip();
    const used = new Set<string>();
    const index = {
      schema: 'figforge/project',
      version: MANIFEST_VERSION,
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
      for (const a of s.assets) zip.file(`${folder}/${a.name}`, a.data); // Uint8Array, native to JSZip
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
    // Success is only claimed HERE — after the zip blob is handed to the browser.
    setStatus(`Exported ${screens.length} screen(s). Project bundle downloaded.`);
  } catch (e) {
    setStatus(`Project bundle packaging failed — ${String((e as Error)?.message || e)}; bundle not saved.`, true);
  }
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

    // `msg.target` is the destination captured when the export was REQUESTED,
    // echoed back by main — see the export button handlers. The download/send
    // helpers print their own success/failure, so a "Bundle downloaded" can no
    // longer precede (or survive) a JSZip failure.
    case 'export-complete':
      showProgress(false);
      setProgress(0);
      if (msg.target === 'unity') {
        const screenName = screenFromManifest(msg.manifest);
        sendToUnity(
          { name: screenName, initial: screenName },
          [{ name: screenName, manifest: msg.manifest, assets: msg.assets, role: 'screen' }]
        );
      } else {
        downloadBundle(msg.manifest, msg.assets);
      }
      break;

    case 'export-page-complete':
      showProgress(false);
      setProgress(0);
      if (msg.target === 'unity') {
        sendToUnity(msg.project, msg.screens);
      } else {
        downloadProjectBundle(msg.project, msg.screens);
      }
      break;

    case 'export-error':
      showProgress(false);
      setStatus(msg.message, true);
      break;

    case 'status':
      // `error` is optional — main flags it on e.g. a rejected concurrent export
      // (which can't use 'export-error': that would tear down the progress UI of
      // the export that's still running).
      setStatus(msg.message, !!msg.error);
      break;

    case 'mcp-response':
      if (socket && socket.readyState === WebSocket.OPEN) {
        socket.send(JSON.stringify(msg.payload));
        bridgeLog(`→ ${msg.payload.type} ${msg.payload.error ? 'ERR' : 'ok'}`);
      }
      break;
  }
};
