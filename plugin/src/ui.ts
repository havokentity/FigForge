// =============================================================================
// FigForge — UI thread (iframe)
// =============================================================================

import type { ElementConfig, ExportScale, TreeNode } from './types';

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

// ---------------------------------------------------------------------------
// Header chrome
// ---------------------------------------------------------------------------
let lastPreset = 'M';
document.querySelectorAll('#sizeSeg button').forEach((b) =>
  b.addEventListener('click', () => {
    document.querySelectorAll('#sizeSeg button').forEach((x) => x.classList.remove('active'));
    b.classList.add('active');
    lastPreset = (b as HTMLElement).dataset.size!;
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
$('#createBtnBtn').addEventListener('click', () => {
  setStatus('Creating button component…');
  post({ type: 'create-button' });
});
for (const [id, kind] of [
  ['createToggleBtn', 'toggle'], ['createRadioBtn', 'radio'],
  ['createDropdownBtn', 'dropdown'], ['createListBtn', 'list'],
] as const) {
  $(`#${id}`).addEventListener('click', () => {
    setStatus(`Creating ${kind} component…`);
    post({ type: 'create-canonical', kind });
  });
}

// option chips
function wireChip(id: string) {
  const el = $(`#${id}`);
  const input = el.querySelector('input') as HTMLInputElement;
  el.addEventListener('click', (e) => {
    if (e.target !== input) input.checked = !input.checked;
    el.classList.toggle('on', input.checked);
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
  if (!currentTree) {
    host.innerHTML = '<div class="empty">Select a frame in Figma.</div>';
    return;
  }
  const query = ($('#search') as HTMLInputElement).value.trim().toLowerCase();
  const expanded = new Set<string>(); // simple: everything expanded for now

  const walk = (node: TreeNode) => {
    const match = !query || node.displayName.toLowerCase().includes(query);
    const childEls = node.children.map(walk).filter(Boolean) as HTMLElement[];
    if (!match && childEls.length === 0) return null;

    const row = document.createElement('div');
    row.className = 'row';
    if (node.id === selectedId) row.classList.add('sel');
    if (excluded.has(node.id) || !node.visible) row.classList.add('hidden');
    row.style.paddingLeft = `${8 + node.depth * 12}px`;

    row.innerHTML = `
      <span class="twirl">${node.children.length ? '▸' : ''}</span>
      <span class="type-tag">${TYPE_SHORT[node.type] || node.type.slice(0, 3)}</span>
      <span class="tname">${escapeHtml(node.displayName)}</span>
      ${node.canonicalRef ? `<span class="canon-tag" title="canonical: ${escapeHtml(node.canonicalRef)}">${escapeHtml(node.canonicalRef)}</span>` : ''}
    `;

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
    return frag as unknown as HTMLElement;
  };

  const tree = walk(currentTree);
  if (tree) host.appendChild(tree);
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

$('#search').addEventListener('input', renderTree);

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

function currentOptions() {
  return {
    autoMerge: ($('#optAutoMerge input') as HTMLInputElement).checked,
    rasterizeStrokes: ($('#optRasterStroke input') as HTMLInputElement).checked,
    emitGradients: ($('#optGradients input') as HTMLInputElement).checked,
    emitImageFills: true,
  };
}

$('#exportBtn').addEventListener('click', () => {
  setStatus('Exporting…');
  showProgress(true);
  post({ type: 'export', scale: parseScale(), options: currentOptions(), elementConfigs: collectConfigs() });
});

// Where the next page export goes: download a .zip, or POST straight to Unity.
let pageExportTarget: 'zip' | 'unity' = 'zip';

$('#exportPageBtn').addEventListener('click', () => {
  pageExportTarget = 'zip';
  setStatus('Exporting whole page…');
  showProgress(true);
  post({ type: 'export-page', scale: parseScale(), options: currentOptions() });
});

$('#exportUnityBtn').addEventListener('click', () => {
  pageExportTarget = 'unity';
  setStatus('Exporting whole page → Unity…');
  showProgress(true);
  post({ type: 'export-page', scale: parseScale(), options: currentOptions() });
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
function setStatus(text: string, err = false) {
  const el = $('#status');
  el.textContent = text;
  el.classList.toggle('err', err);
}
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

function bridgeLog(line: string) { console.log('[FigForge MCP]', line); }

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
  if (wantConnected) disconnectMcp();
  else { wantConnected = true; connectMcp(); }
});
setMcp('off');

// ---------------------------------------------------------------------------
// main → ui
// ---------------------------------------------------------------------------
window.onmessage = (event: MessageEvent) => {
  const msg = (event.data && event.data.pluginMessage) || event.data;
  if (!msg || !msg.type) return;

  switch (msg.type) {
    case 'selection-info':
      currentTree = msg.tree;
      selectedId = null;
      setStatus(`${msg.name} · ${msg.elementCount} layers`);
      ($('#exportBtn') as HTMLButtonElement).disabled = false;
      renderTree();
      break;

    case 'no-selection':
      currentTree = null;
      ($('#exportBtn') as HTMLButtonElement).disabled = true;
      setStatus('Select a frame, component, or group.');
      renderTree();
      break;

    case 'element-preview': {
      const blob = new Blob([new Uint8Array(msg.imageData)], { type: 'image/png' });
      const url = URL.createObjectURL(blob);
      $('#previewHead').textContent = `${msg.name} · ${msg.figmaType} · ${Math.round(msg.size.w)}×${Math.round(msg.size.h)}`;
      $('#previewWrap').innerHTML = '';
      const img = document.createElement('img');
      img.src = url;
      $('#previewWrap').appendChild(img);
      break;
    }

    case 'progress':
      setProgress(msg.total ? Math.round((msg.current / msg.total) * 100) : 0);
      setStatus(`Exporting ${msg.label} (${msg.current}/${msg.total})`);
      break;

    case 'export-complete':
      showProgress(false);
      setProgress(0);
      downloadBundle(msg.manifest, msg.assets);
      setStatus(`Exported ${msg.assets.length} asset(s). Bundle downloaded.`);
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
