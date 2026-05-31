// =============================================================================
// FigForge — plugin main thread (Figma sandbox)
//
// Owns the document: builds the layer tree for the UI, runs exports, and serves
// MCP requests forwarded by the UI from the bridge server.
// =============================================================================

import {
  DEFAULT_EXPORT_OPTIONS,
  DEFAULT_EXPORT_SCALE,
  type CanonicalKind,
  type ElementConfig,
  type ExportOptions,
  type ExportScale,
} from './types';
import { buildTree } from './traverser';
import { exportDesign } from './exporter';
import { sanitize } from './naming';

const WINDOW_PRESETS: Record<string, { w: number; h: number }> = {
  S: { w: 460, h: 600 },
  M: { w: 600, h: 760 },
  L: { w: 820, h: 920 },
  mini: { w: 260, h: 44 },
};

figma.showUI(__html__, { width: WINDOW_PRESETS.M.w, height: WINDOW_PRESETS.M.h, themeColors: true });

// Per-session UI state we need to remember between messages.
const excluded = new Set<string>();
const merged = new Set<string>();
const forcedPng = new Set<string>();

function selectedRoot(): SceneNode | null {
  const sel = figma.currentPage.selection;
  if (sel.length === 0) return null;
  const node = sel[0];
  const ok = ['FRAME', 'COMPONENT', 'COMPONENT_SET', 'INSTANCE', 'GROUP'].includes(node.type);
  return ok ? node : null;
}

function pushSelection() {
  const root = selectedRoot();
  if (!root) {
    figma.ui.postMessage({ type: 'no-selection' });
    return;
  }
  const tree = buildTree(root, excluded);
  let count = 0;
  const walk = (n: typeof tree) => {
    count++;
    n.children.forEach(walk);
  };
  walk(tree);
  figma.ui.postMessage({ type: 'selection-info', name: root.name, elementCount: count, tree });
}

figma.on('selectionchange', pushSelection);
pushSelection();

// ---------------------------------------------------------------------------
// UI → main
// ---------------------------------------------------------------------------
figma.ui.onmessage = async (msg: { type: string; [k: string]: unknown }) => {
  switch (msg.type) {
    case 'reload':
      pushSelection();
      break;

    case 'resize-ui': {
      const preset = WINDOW_PRESETS[(msg.preset as string) || 'M'] || WINDOW_PRESETS.M;
      figma.ui.resize(preset.w, preset.h);
      break;
    }

    case 'toggle-visibility': {
      const id = msg.nodeId as string;
      if (excluded.has(id)) excluded.delete(id);
      else excluded.add(id);
      pushSelection();
      break;
    }

    case 'toggle-merge': {
      const id = msg.nodeId as string;
      if (merged.has(id)) merged.delete(id);
      else merged.add(id);
      pushSelection();
      break;
    }

    case 'toggle-png': {
      const id = msg.nodeId as string;
      if (forcedPng.has(id)) forcedPng.delete(id);
      else forcedPng.add(id);
      pushSelection();
      break;
    }

    case 'highlight-element': {
      const node = figma.getNodeById(msg.nodeId as string) as SceneNode | null;
      if (node) {
        figma.currentPage.selection = [node];
        figma.viewport.scrollAndZoomIntoView([node]);
      }
      break;
    }

    case 'preview-element':
      await sendPreview(msg.nodeId as string);
      break;

    case 'export': {
      const root = selectedRoot();
      if (!root) {
        figma.ui.postMessage({ type: 'export-error', message: 'Select a frame to export.' });
        break;
      }
      try {
        const scale = (msg.scale as ExportScale) || DEFAULT_EXPORT_SCALE;
        const options = (msg.options as ExportOptions) || DEFAULT_EXPORT_OPTIONS;
        applyConfigs(msg.elementConfigs as ElementConfig[] | undefined);
        const result = await exportDesign(
          root,
          scale,
          options,
          excluded,
          merged,
          forcedPng,
          (current, total, label) =>
            figma.ui.postMessage({ type: 'progress', current, total, label })
        );
        figma.ui.postMessage({
          type: 'export-complete',
          manifest: JSON.stringify(result.manifest, null, 2),
          assets: result.assets,
        });
      } catch (e) {
        figma.ui.postMessage({ type: 'export-error', message: String((e as Error)?.message || e) });
      }
      break;
    }

    case 'export-page': {
      const found = collectScreens(figma.currentPage);
      if (found.length === 0) {
        figma.ui.postMessage({ type: 'export-error', message: 'No top-level frames (or frames in sections) on this page.' });
        break;
      }
      try {
        const scale = (msg.scale as ExportScale) || DEFAULT_EXPORT_SCALE;
        const options = (msg.options as ExportOptions) || DEFAULT_EXPORT_OPTIONS;
        const screens: {
          name: string; manifest: string; assets: { name: string; data: number[] }[];
          section: string; role: string;
        }[] = [];
        for (let i = 0; i < found.length; i++) {
          figma.ui.postMessage({ type: 'progress', current: i, total: found.length, label: found[i].node.name });
          const result = await exportDesign(found[i].node, scale, options);
          screens.push({
            name: sanitize(found[i].node.name),
            manifest: JSON.stringify(result.manifest, null, 2),
            assets: result.assets,
            section: found[i].section,
            role: frameRole(found[i].node),
          });
        }
        // Initial = first non-shell screen.
        const firstScreen = screens.find((s) => s.role !== 'shell') || screens[0];
        figma.ui.postMessage({
          type: 'export-page-complete',
          project: { name: figma.currentPage.name, initial: firstScreen ? firstScreen.name : '' },
          screens,
        });
      } catch (e) {
        figma.ui.postMessage({ type: 'export-error', message: String((e as Error)?.message || e) });
      }
      break;
    }

    case 'create-button': {
      try {
        await createCanonicalButton();
        figma.ui.postMessage({
          type: 'status',
          message: `Button instance placed. Skin the master on the FigForge Components page; click ＋Button again to add more.`,
        });
      } catch (e) {
        figma.ui.postMessage({ type: 'export-error', message: 'Create button failed: ' + String((e as Error)?.message || e) });
      }
      break;
    }

    case 'create-canonical': {
      try {
        const comp = await createCanonical(String((msg as { kind?: string }).kind || ''));
        figma.ui.postMessage({ type: 'status', message: `${comp.name} instance placed. Skin the master on the FigForge Components page; click again to add more (group radios under one frame).` });
      } catch (e) {
        figma.ui.postMessage({ type: 'export-error', message: 'Create failed: ' + String((e as Error)?.message || e) });
      }
      break;
    }

    case 'mcp-request':
      await handleMcp(msg.payload as McpRequest);
      break;

    case 'cancel':
      figma.closePlugin();
      break;
  }
};

function applyConfigs(configs: ElementConfig[] | undefined) {
  if (!configs) return;
  for (const c of configs) {
    if (c.excluded) excluded.add(c.id);
    else excluded.delete(c.id);
    if (c.merged) merged.add(c.id);
    else merged.delete(c.id);
    if (c.rasterize) forcedPng.add(c.id);
    else forcedPng.delete(c.id);
  }
}

async function sendPreview(nodeId: string) {
  const node = figma.getNodeById(nodeId) as SceneNode | null;
  if (!node || !('exportAsync' in node)) return;
  try {
    const bytes = await (node as unknown as {
      exportAsync: (s: ExportSettings) => Promise<Uint8Array>;
    }).exportAsync({ format: 'PNG', constraint: { type: 'SCALE', value: 1 } });
    figma.ui.postMessage({
      type: 'element-preview',
      nodeId,
      name: node.name,
      figmaType: node.type,
      size: { w: (node as unknown as { width: number }).width, h: (node as unknown as { height: number }).height },
      imageData: Array.from(bytes),
    });
  } catch {
    /* unpreviewable (e.g. empty container) — ignore */
  }
}

// ---------------------------------------------------------------------------
// MCP request handling (forwarded from the bridge via the UI WebSocket)
// ---------------------------------------------------------------------------
interface McpRequest {
  type: string;
  requestId: string;
  nodeIds?: string[];
  params?: Record<string, unknown>;
}

function summarize(node: BaseNode, depth: number): unknown {
  const base: Record<string, unknown> = { id: node.id, name: node.name, type: node.type };
  if (depth > 0 && 'children' in node) {
    base.children = (node as ChildrenMixin).children.map((c) => summarize(c, depth - 1));
  }
  return base;
}

async function handleMcp(req: McpRequest) {
  const response: { type: string; requestId: string; data?: unknown; error?: string } = {
    type: req.type,
    requestId: req.requestId,
  };
  try {
    switch (req.type) {
      case 'get_metadata':
        response.data = {
          fileName: figma.root.name,
          currentPage: figma.currentPage.name,
          pages: figma.root.children.map((p) => ({ id: p.id, name: p.name })),
        };
        break;
      case 'get_document':
        response.data = summarize(figma.currentPage, 2);
        break;
      case 'get_selection':
        response.data = figma.currentPage.selection.map((n) => summarize(n, 1));
        break;
      case 'get_node': {
        const id = (req.params?.nodeId as string) || req.nodeIds?.[0];
        const node = id ? figma.getNodeById(id) : null;
        response.data = node ? summarize(node, 3) : null;
        if (!node) response.error = `Node not found: ${id}`;
        break;
      }
      case 'get_design_context': {
        const depth = (req.params?.depth as number) ?? 2;
        response.data = summarize(figma.currentPage, depth);
        break;
      }
      case 'get_screenshot': {
        const ids = req.nodeIds || figma.currentPage.selection.map((n) => n.id);
        const scale = (req.params?.scale as number) ?? 2;
        const shots: { nodeId: string; data: number[] }[] = [];
        for (const id of ids) {
          const node = figma.getNodeById(id) as SceneNode | null;
          if (node && 'exportAsync' in node) {
            const bytes = await (node as unknown as {
              exportAsync: (s: ExportSettings) => Promise<Uint8Array>;
            }).exportAsync({ format: 'PNG', constraint: { type: 'SCALE', value: scale } });
            shots.push({ nodeId: id, data: Array.from(bytes) });
          }
        }
        response.data = { screenshots: shots };
        break;
      }
      case 'export_unity': {
        // Full Unity export reusing the UI's exporter, driven over MCP so an
        // agent can batch every frame itself. (FigmaTest feature.)
        const ids = req.nodeIds || figma.currentPage.selection.map((n) => n.id);
        const exports: unknown[] = [];
        for (const id of ids) {
          const node = figma.getNodeById(id) as SceneNode | null;
          if (node && 'exportAsync' in node) {
            const result = await exportDesign(node, DEFAULT_EXPORT_SCALE, DEFAULT_EXPORT_OPTIONS);
            exports.push({
              nodeId: id,
              name: sanitize(node.name),
              manifest: result.manifest,
              assets: result.assets,
            });
          }
        }
        response.data = { exports };
        break;
      }
      default:
        response.error = `Unknown MCP command: ${req.type}`;
    }
  } catch (e) {
    response.error = String((e as Error)?.message || e);
  }
  figma.ui.postMessage({ type: 'mcp-response', payload: response });
}

// ---------------------------------------------------------------------------
// Create Button tool — scaffolds a tagged canonical-button Component on a
// dedicated "FigForge Components" page. Skin it, then drop instances anywhere;
// the exporter detects them via the plugin-data tag.
// ---------------------------------------------------------------------------
const COMPONENTS_PAGE = 'FigForge Components';

async function loadUiFont(): Promise<FontName> {
  const candidates: FontName[] = [
    { family: 'Inter', style: 'Regular' },
    { family: 'Roboto', style: 'Regular' },
    { family: 'Arial', style: 'Regular' },
  ];
  for (const f of candidates) {
    try {
      await figma.loadFontAsync(f);
      return f;
    } catch {
      /* try next */
    }
  }
  const all = await figma.listAvailableFontsAsync();
  const f = all[0].fontName;
  await figma.loadFontAsync(f);
  return f;
}

function jumpTo(page: PageNode, node: SceneNode) {
  figma.currentPage = page;
  figma.currentPage.selection = [node];
  figma.viewport.scrollAndZoomIntoView([node]);
}

function stateRect(name: string, color: RGB, visible: boolean, w: number, h: number, r: number): RectangleNode {
  const rect = figma.createRectangle();
  rect.name = name;
  rect.resize(w, h);
  rect.x = 0;
  rect.y = 0;
  rect.cornerRadius = r;
  rect.fills = [{ type: 'SOLID', color }];
  rect.constraints = { horizontal: 'STRETCH', vertical: 'STRETCH' };
  rect.visible = visible;
  return rect;
}

// Collect screen frames on a page — top-level frames AND frames inside Sections,
// each tagged with its enclosing section name (sanitized; '' if none).
function collectScreens(page: PageNode): { node: SceneNode; section: string }[] {
  const out: { node: SceneNode; section: string }[] = [];
  for (const n of page.children) {
    if ((n as SceneNode).visible === false) continue;
    if (n.type === 'SECTION') {
      const sec = sanitize(n.name);
      for (const c of (n as SectionNode).children) {
        if (['FRAME', 'COMPONENT'].includes(c.type) && (c as SceneNode).visible !== false) {
          out.push({ node: c as SceneNode, section: sec });
        }
      }
    } else if (['FRAME', 'COMPONENT'].includes(n.type)) {
      out.push({ node: n as SceneNode, section: '' });
    }
  }
  return out;
}

// A frame is the app "shell" if named Shell / Shell_* or tagged role=shell.
function frameRole(node: SceneNode): string {
  const n = node.name.toLowerCase();
  if (n === 'shell' || n.startsWith('shell_') || n.startsWith('shell ')) return 'shell';
  if (node.getSharedPluginData('figforge', 'role') === 'shell') return 'shell';
  return 'screen';
}

async function createCanonicalButton(): Promise<ComponentNode> {
  let page = figma.root.children.find((p) => p.name === COMPONENTS_PAGE) as PageNode | undefined;
  if (!page) {
    page = figma.createPage();
    page.name = COMPONENTS_PAGE;
  }

  // Reuse the default "Button" master if it already exists — don't spam Button2/3;
  // instead drop another INSTANCE on the current page so you can place many.
  const existing = page.children.find(
    (n) => n.type === 'COMPONENT' && n.name === 'Button'
  ) as ComponentNode | undefined;
  if (existing) {
    placeInstance(existing);
    return existing;
  }

  const font = await loadUiFont();
  const W = 160, H = 48, R = 8;

  const comp = figma.createComponent();
  comp.name = 'Button';
  comp.resize(W, H);
  comp.fills = []; // the visible background is the state layers below

  // Visual states (only Regular shown by default) — skin each one.
  comp.appendChild(stateRect('Regular', { r: 0.49, g: 0.36, b: 1 }, true, W, H, R));
  comp.appendChild(stateRect('Rollover', { r: 0.58, g: 0.47, b: 1 }, false, W, H, R));
  comp.appendChild(stateRect('Pressed', { r: 0.40, g: 0.27, b: 0.92 }, false, W, H, R));

  // Hit area — full-bleed transparent rect marking the clickable region.
  const hit = figma.createRectangle();
  hit.name = 'HitArea';
  hit.resize(W, H);
  hit.x = 0;
  hit.y = 0;
  hit.fills = [{ type: 'SOLID', color: { r: 0, g: 0, b: 0 }, opacity: 0 }];
  hit.constraints = { horizontal: 'STRETCH', vertical: 'STRETCH' };
  comp.appendChild(hit);

  // Label (top, centered).
  const label = figma.createText();
  label.fontName = font;
  label.name = 'Label';
  label.characters = 'Button';
  label.fontSize = 16;
  label.fills = [{ type: 'SOLID', color: { r: 1, g: 1, b: 1 } }];
  label.textAlignHorizontal = 'CENTER';
  label.textAlignVertical = 'CENTER';
  comp.appendChild(label);
  label.x = (W - label.width) / 2;
  label.y = (H - label.height) / 2;
  label.constraints = { horizontal: 'CENTER', vertical: 'CENTER' };

  comp.setSharedPluginData('figforge', 'canonical', JSON.stringify({ kind: 'button', ref: 'Button' }));

  page.appendChild(comp);
  comp.x = 0;
  comp.y = 0;
  placeInstance(comp);
  return comp;
}

// The shared FigForge Components page (created on first use).
function componentsPage(): PageNode {
  let page = figma.root.children.find((p) => p.name === COMPONENTS_PAGE) as PageNode | undefined;
  if (!page) { page = figma.createPage(); page.name = COMPONENTS_PAGE; }
  return page;
}

// Drop a usable INSTANCE of a canonical component onto the user's current page,
// laid out in a grid near the viewport centre (offset per existing instance so
// repeated clicks stack neatly). Selecting + framing it. This is what lets you
// create more than one — each click places another instance to position/group.
function placeInstance(comp: ComponentNode): InstanceNode {
  const inst = comp.createInstance();
  figma.currentPage.appendChild(inst);
  const prior = figma.currentPage.findAll(
    (n) => n.type === 'INSTANCE' && (n as InstanceNode).mainComponent === comp
  ).length - 1; // minus the one we just added
  const c = figma.viewport.center;
  inst.x = Math.round(c.x + (prior % 4) * (inst.width + 20));
  inst.y = Math.round(c.y + Math.floor(prior / 4) * (inst.height + 20));
  figma.currentPage.selection = [inst];
  figma.viewport.scrollAndZoomIntoView([inst]);
  return inst;
}

function solidRect(name: string, w: number, h: number, r: number, color: RGB, alpha = 1): RectangleNode {
  const rect = figma.createRectangle();
  rect.name = name; rect.resize(w, h); rect.cornerRadius = r;
  rect.fills = [{ type: 'SOLID', color, opacity: alpha }];
  return rect;
}

// Dispatch a "+Toggle / +Radio / +Dropdown / +List" create request to its builder.
async function createCanonical(kind: string): Promise<ComponentNode> {
  switch (kind) {
    case 'toggle': return createToggleLike('toggle', 'Toggle', false);
    case 'radio': return createToggleLike('radio', 'Radio', true);
    case 'dropdown': return createDropdown();
    case 'list': return createList();
    default: throw new Error(`unknown canonical kind '${kind}'`);
  }
}

// Toggle / Radio: a Background box (UGUI Toggle.targetGraphic) + a Checkmark shown
// when on (Toggle.graphic) + HitArea + Label. Radio is circular and grouped in Unity
// by its parent frame. Off by default.
async function createToggleLike(kind: CanonicalKind, ref: string, circular: boolean): Promise<ComponentNode> {
  const page = componentsPage();
  let comp = page.children.find((n) => n.type === 'COMPONENT' && n.name === ref) as ComponentNode | undefined;
  if (!comp) {
    const font = await loadUiFont();
    const BOX = 24, W = 150, H = 24;
    const boxR = circular ? BOX / 2 : 6, ckR = circular ? 7 : 3, CK = 14;

    comp = figma.createComponent();
    comp.name = ref; comp.resize(W, H); comp.fills = [];

    const bg = solidRect('Background', BOX, BOX, boxR, { r: 0.85, g: 0.86, b: 0.9 });
    bg.x = 0; bg.y = 0; bg.constraints = { horizontal: 'MIN', vertical: 'CENTER' };
    comp.appendChild(bg);

    const ck = solidRect('Checkmark', CK, CK, ckR, { r: 0.49, g: 0.36, b: 1 });
    ck.x = (BOX - CK) / 2; ck.y = (BOX - CK) / 2; ck.visible = false;
    ck.constraints = { horizontal: 'MIN', vertical: 'CENTER' };
    comp.appendChild(ck);

    const hit = solidRect('HitArea', W, H, 0, { r: 0, g: 0, b: 0 }, 0);
    hit.x = 0; hit.y = 0; hit.constraints = { horizontal: 'STRETCH', vertical: 'STRETCH' };
    comp.appendChild(hit);

    const label = figma.createText();
    label.fontName = font; label.name = 'Label'; label.characters = ref;
    label.fontSize = 14; label.fills = [{ type: 'SOLID', color: { r: 0.1, g: 0.1, b: 0.12 } }];
    label.textAlignVertical = 'CENTER';
    comp.appendChild(label);
    label.x = BOX + 8; label.y = (H - label.height) / 2;
    label.constraints = { horizontal: 'STRETCH', vertical: 'CENTER' };

    comp.setSharedPluginData('figforge', 'canonical', JSON.stringify({ kind, ref, value: 'off' }));
    page.appendChild(comp); comp.x = 0; comp.y = 0;
  }
  placeInstance(comp); // each click drops another instance on your page (so you can make many / group radios)
  return comp;
}

// Dropdown: a Background + caption Label + Arrow, plus a hidden Options frame whose
// child text layers are the selectable options (captured into a TMP_Dropdown).
async function createDropdown(): Promise<ComponentNode> {
  const page = componentsPage();
  const reuse = page.children.find((n) => n.type === 'COMPONENT' && n.name === 'Dropdown') as ComponentNode | undefined;
  if (reuse) { placeInstance(reuse); return reuse; }

  const font = await loadUiFont();
  const W = 220, H = 40, R = 8;
  const comp = figma.createComponent();
  comp.name = 'Dropdown'; comp.resize(W, H); comp.fills = [];

  const bg = solidRect('Background', W, H, R, { r: 1, g: 1, b: 1 });
  bg.strokes = [{ type: 'SOLID', color: { r: 0.8, g: 0.8, b: 0.85 } }]; bg.strokeWeight = 1;
  bg.x = 0; bg.y = 0; bg.constraints = { horizontal: 'STRETCH', vertical: 'STRETCH' };
  comp.appendChild(bg);

  const label = figma.createText();
  label.fontName = font; label.name = 'Label'; label.characters = 'Option 1';
  label.fontSize = 14; label.fills = [{ type: 'SOLID', color: { r: 0.1, g: 0.1, b: 0.12 } }];
  label.textAlignVertical = 'CENTER';
  comp.appendChild(label); label.x = 12; label.y = (H - label.height) / 2;
  label.constraints = { horizontal: 'STRETCH', vertical: 'CENTER' };

  const arrow = figma.createText();
  arrow.fontName = font; arrow.name = 'Arrow'; arrow.characters = '▾';
  arrow.fontSize = 14; arrow.fills = [{ type: 'SOLID', color: { r: 0.4, g: 0.4, b: 0.45 } }];
  comp.appendChild(arrow); arrow.x = W - 24; arrow.y = (H - arrow.height) / 2;
  arrow.constraints = { horizontal: 'MAX', vertical: 'CENTER' };

  // Hidden options source — each child text is one option.
  const opts = figma.createFrame();
  opts.name = 'Options'; opts.resize(W, 120); opts.fills = []; opts.visible = false;
  opts.x = 0; opts.y = H + 4;
  comp.appendChild(opts);
  for (let i = 0; i < 3; i++) {
    const t = figma.createText();
    t.fontName = font; t.characters = `Option ${i + 1}`; t.fontSize = 14;
    t.fills = [{ type: 'SOLID', color: { r: 0.1, g: 0.1, b: 0.12 } }];
    opts.appendChild(t); t.x = 12; t.y = 8 + i * 36;
  }

  comp.setSharedPluginData('figforge', 'canonical', JSON.stringify({ kind: 'dropdown', ref: 'Dropdown', value: 'Option 1' }));
  page.appendChild(comp); comp.x = 0; comp.y = 0;
  placeInstance(comp);
  return comp;
}

// The reusable row used by List — its own component (Regular/Rollover/HitArea/Label)
// so every row in the composited list preview stays in sync when you skin it once.
async function ensureListItem(page: PageNode, font: FontName, W: number, ROW: number): Promise<ComponentNode> {
  const found = page.children.find((n) => n.type === 'COMPONENT' && n.name === 'ListItem') as ComponentNode | undefined;
  if (found) return found;
  const item = figma.createComponent();
  item.name = 'ListItem'; item.resize(W, ROW); item.fills = []; item.clipsContent = true;
  const reg = solidRect('Regular', W, ROW, 0, { r: 1, g: 1, b: 1 });
  reg.visible = true; reg.constraints = { horizontal: 'STRETCH', vertical: 'STRETCH' }; item.appendChild(reg);
  const roll = solidRect('Rollover', W, ROW, 0, { r: 0.93, g: 0.92, b: 1 });
  roll.visible = false; roll.constraints = { horizontal: 'STRETCH', vertical: 'STRETCH' }; item.appendChild(roll);
  const hit = solidRect('HitArea', W, ROW, 0, { r: 0, g: 0, b: 0 }, 0);
  hit.constraints = { horizontal: 'STRETCH', vertical: 'STRETCH' }; item.appendChild(hit);
  const t = figma.createText();
  t.fontName = font; t.name = 'Label'; t.characters = 'Item'; t.fontSize = 14;
  t.fills = [{ type: 'SOLID', color: { r: 0.1, g: 0.1, b: 0.12 } }]; t.textAlignVertical = 'CENTER';
  item.appendChild(t); t.x = 16; t.y = (ROW - t.height) / 2; t.constraints = { horizontal: 'STRETCH', vertical: 'CENTER' };
  page.appendChild(item); item.x = 0; item.y = -ROW - 40; // park the master above the List
  return item;
}

// List: a rounded, clipped Background + a COMPOSITED stack of ListItem instances (so
// you see a real multi-row list in Figma and copy it onto your page). Skin the one
// ListItem master (Regular/Rollover states) and every row updates. In Unity the row
// is repeated `count` times (count = list height ÷ row height) with the rollover swap.
async function createList(): Promise<ComponentNode> {
  const page = componentsPage();
  const reuse = page.children.find((n) => n.type === 'COMPONENT' && n.name === 'List') as ComponentNode | undefined;
  if (reuse) { placeInstance(reuse); return reuse; }

  const font = await loadUiFont();
  const W = 260, ROW = 48, ROWS = 5, H = ROW * ROWS, R = 14;
  const itemComp = await ensureListItem(page, font, W, ROW);

  const comp = figma.createComponent();
  comp.name = 'List'; comp.resize(W, H); comp.fills = []; comp.clipsContent = true;

  const bg = solidRect('Background', W, H, R, { r: 1, g: 1, b: 1 });
  bg.strokes = [{ type: 'SOLID', color: { r: 0.82, g: 0.83, b: 0.88 } }]; bg.strokeWeight = 1;
  bg.x = 0; bg.y = 0; bg.constraints = { horizontal: 'STRETCH', vertical: 'STRETCH' };
  comp.appendChild(bg);

  // Composited preview: ROWS instances of the single ListItem master (the first is the
  // 'Item' the importer reads; all stay in sync when you skin the master).
  for (let i = 0; i < ROWS; i++) {
    const ins = itemComp.createInstance();
    ins.name = 'Item'; ins.resize(W, ROW); ins.x = 0; ins.y = i * ROW;
    ins.constraints = { horizontal: 'STRETCH', vertical: 'MIN' };
    comp.appendChild(ins);
  }

  comp.setSharedPluginData('figforge', 'canonical', JSON.stringify({ kind: 'list', ref: 'List' }));
  page.appendChild(comp); comp.x = 0; comp.y = 0;
  placeInstance(comp);
  return comp;
}
