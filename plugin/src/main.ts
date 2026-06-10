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
import { buildTree, detectCanonical } from './traverser';
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
      const detached = detachedCanonicalInstances(figma.currentPage, found.map((s) => s.node));
      if (detached.length > 0) {
        const examples = detached.slice(0, 4).map((d) => `${d.kind}:${d.name}`).join(', ');
        const more = detached.length > 4 ? ` (+${detached.length - 4} more)` : '';
        const message = `FigForge controls outside exported frames will be skipped: ${examples}${more}`;
        figma.notify(message);
        figma.ui.postMessage({ type: 'status', message });
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
        useComponentsPage = (msg as { componentsPage?: boolean }).componentsPage !== false;
        await createCanonicalButton();
        figma.ui.postMessage({
          type: 'status',
          message: useComponentsPage
            ? `Button instance placed. Master is on the FigForge Components page — skin it there. Click ＋Button again for more.`
            : `Button placed (master parked on this page, off to the left). Skin the master; click ＋Button again for more.`,
        });
      } catch (e) {
        figma.ui.postMessage({ type: 'export-error', message: 'Create button failed: ' + String((e as Error)?.message || e) });
      }
      break;
    }

    case 'create-canonical': {
      try {
        useComponentsPage = (msg as { componentsPage?: boolean }).componentsPage !== false;
        const comp = await createCanonical(String((msg as { kind?: string }).kind || ''),
          (msg as { listOpts?: Partial<ListOptions> }).listOpts);
        const where = useComponentsPage ? 'on the FigForge Components page' : 'parked on this page (off to the left)';
        figma.ui.postMessage({ type: 'status', message: `${comp.name} instance placed. Master is ${where} — skin it; click again to add more (group radios under one frame).` });
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
// UI checkbox "Components page" (default ON): masters go to the dedicated FigForge
// Components page. When OFF, masters are parked loose on the current design page.
// Set from each create-* message before the creator runs (single-threaded plugin).
let useComponentsPage = true;

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
  if (page.name === COMPONENTS_PAGE) return out;
  for (const n of page.children) {
    if ((n as SceneNode).visible === false) continue;
    if (n.type === 'SECTION') {
      const sec = sanitize(n.name);
      for (const c of (n as SectionNode).children) {
        if (c.type === 'FRAME' && (c as SceneNode).visible !== false) {
          out.push({ node: c as SceneNode, section: sec });
        }
      }
    } else if (n.type === 'FRAME') {
      out.push({ node: n as SceneNode, section: '' });
    }
  }
  return out;
}

function screenFrames(page: PageNode): FrameNode[] {
  return collectScreens(page).map((s) => s.node).filter((n): n is FrameNode => n.type === 'FRAME');
}

function isInside(node: SceneNode, ancestor: SceneNode): boolean {
  let cur: BaseNode | null = node;
  while (cur && cur.type !== 'PAGE') {
    if (cur.id === ancestor.id) return true;
    cur = cur.parent;
  }
  return false;
}

function detachedCanonicalInstances(page: PageNode, screens: SceneNode[]): { name: string; kind: CanonicalKind }[] {
  const out: { name: string; kind: CanonicalKind }[] = [];
  if (page.name === COMPONENTS_PAGE) return out;
  const nodes = page.findAll((n) => n.type === 'INSTANCE' && (n as SceneNode).visible !== false) as InstanceNode[];
  for (const node of nodes) {
    const canonical = detectCanonical(node);
    if (!canonical) continue;
    if (screens.some((screen) => isInside(node, screen))) continue;
    out.push({ name: node.name, kind: canonical.kind });
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
  // Reuse the "Button" master wherever it lives — don't spam Button2/3; just drop
  // another INSTANCE so you can place many.
  const existing = findMaster('Button');
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

  parkMaster(comp);
  placeInstance(comp);
  return comp;
}

// Drop a usable INSTANCE of a canonical component onto a DESIGN page, laid out in a
// grid near the viewport centre (offset per existing instance so repeated clicks
// stack neatly), then select + frame it. This is what lets you create more than one.
// Instances are NEVER placed on the FigForge Components page (where the masters live):
// if that's the active page we switch to a real design page first — otherwise clicking
// +Dropdown while viewing the components would drop the instance among the components.
function placeInstance(comp: ComponentNode): InstanceNode {
  let target = figma.currentPage;
  if (target.name === COMPONENTS_PAGE) {
    target = (figma.root.children.find((p) => p.name !== COMPONENTS_PAGE) as PageNode | undefined)
      ?? (() => { const p = figma.createPage(); p.name = 'FigForge Sandbox'; return p; })();
    figma.currentPage = target;
  }
  // Prefer dropping the instance INSIDE the screen frame the user is working in:
  // a loose instance sitting on the page (outside any frame) is NOT a descendant of
  // an exported screen, so the export skips it and Unity never sees the control.
  const frame = enclosingScreenFrame(target.selection) ?? singleScreenFrame(target);
  const parent: BaseNode & ChildrenMixin = frame ?? target;
  const inst = comp.createInstance();
  parent.appendChild(inst);
  const prior = parent.findAll(
    (n) => n.type === 'INSTANCE' && (n as InstanceNode).mainComponent === comp
  ).length - 1; // minus the one we just added
  if (frame) {
    // frame-local coords: stack a small grid inset from the frame's top-left.
    inst.x = Math.round(24 + (prior % 4) * (inst.width + 20));
    inst.y = Math.round(24 + Math.floor(prior / 4) * (inst.height + 20));
  } else {
    const c = figma.viewport.center;
    inst.x = Math.round(c.x + (prior % 4) * (inst.width + 20));
    inst.y = Math.round(c.y + Math.floor(prior / 4) * (inst.height + 20));
    figma.notify('Placed on the page — drag it into a frame so it exports as part of a screen.');
  }
  figma.currentPage.selection = [inst];
  figma.viewport.scrollAndZoomIntoView([inst]);
  return inst;
}

function singleScreenFrame(page: PageNode): FrameNode | undefined {
  const frames = screenFrames(page).filter((frame) => frame.visible !== false);
  return frames.length === 1 ? frames[0] : undefined;
}

// The screen FRAME enclosing any selected node. Screens can be top-level page
// frames or frames inside Figma Sections; collectScreens exports both.
function enclosingScreenFrame(nodes: readonly SceneNode[]): FrameNode | undefined {
  for (const n of nodes) {
    let cur: BaseNode | null = n;
    while (cur && cur.type !== 'PAGE') {
      if (cur.type === 'FRAME' && cur.parent && (cur.parent.type === 'PAGE' || cur.parent.type === 'SECTION')) {
        return cur as FrameNode;
      }
      cur = cur.parent;
    }
  }
  return undefined;
}

// Canonical master component names FigForge knows how to create.
const FIGFORGE_MASTERS = ['Button', 'Toggle', 'Radio', 'InputField', 'Dropdown', 'List', 'ListItem'];

// Find an existing canonical master by name ANYWHERE in the document (masters no
// longer need to live on a dedicated page — they can sit loose on any design page,
// and instances of one master collate to a single Unity prefab regardless of page).
function findMaster(ref: string): ComponentNode | undefined {
  for (const page of figma.root.children) {
    const c = page.children.find((n) => n.type === 'COMPONENT' && n.name === ref);
    if (c) return c as ComponentNode;
  }
  return undefined;
}

// Park a freshly-created master, stacked below existing FigForge masters. With the
// "Components page" option ON (default) it goes to the dedicated FigForge Components
// page; OFF, it's parked LOOSE off-canvas on the current design page (outside screen
// frames, so it's never exported as part of a screen).
function parkMaster(comp: ComponentNode): void {
  let page: PageNode;
  if (useComponentsPage) {
    page = (figma.root.children.find((p) => p.name === COMPONENTS_PAGE) as PageNode | undefined)
      ?? (() => { const p = figma.createPage(); p.name = COMPONENTS_PAGE; return p; })();
  } else {
    page = figma.currentPage;
  }
  page.appendChild(comp);
  let y = 0;
  for (const n of page.children) {
    if (n !== comp && n.type === 'COMPONENT' && FIGFORGE_MASTERS.includes(n.name)) {
      y = Math.max(y, (n as ComponentNode).y + (n as ComponentNode).height + 40);
    }
  }
  comp.x = useComponentsPage ? 40 : -comp.width - 280;
  comp.y = useComponentsPage ? y + 40 : y;
}

function solidRect(name: string, w: number, h: number, r: number, color: RGB, alpha = 1): RectangleNode {
  const rect = figma.createRectangle();
  rect.name = name; rect.resize(w, h); rect.cornerRadius = r;
  rect.fills = [{ type: 'SOLID', color, opacity: alpha }];
  return rect;
}

// Dispatch a "+Toggle / +Radio / +Dropdown / +List" create request to its builder.
async function createCanonical(kind: string, listOpts?: Partial<ListOptions>): Promise<ComponentNode> {
  switch (kind) {
    case 'toggle': return createToggleLike('toggle', 'Toggle', false);
    case 'radio': return createToggleLike('radio', 'Radio', true);
    case 'input': return createInputField();
    case 'dropdown': return createDropdown();
    case 'list': return createList({ ...LIST_DEFAULTS, ...(listOpts ?? {}) });
    default: throw new Error(`unknown canonical kind '${kind}'`);
  }
}

// Toggle / Radio: a Background box (UGUI Toggle.targetGraphic) + a Checkmark shown
// when on (Toggle.graphic) + HitArea + Label. Radio is circular and grouped in Unity
// by its parent frame. Off by default.
async function createToggleLike(kind: CanonicalKind, ref: string, circular: boolean): Promise<ComponentNode> {
  let comp = findMaster(ref);
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
    parkMaster(comp);
  }
  placeInstance(comp); // each click drops another instance on your page (so you can make many / group radios)
  return comp;
}

// InputField: Background + Placeholder + optional Text value. The generated Unity
// prefab becomes a TMP_InputField; skin the Background and text layers in Figma.
async function createInputField(): Promise<ComponentNode> {
  const reuse = findMaster('InputField');
  if (reuse) {
    await normalizeInputFieldMaster(reuse);
    placeInstance(reuse);
    return reuse;
  }

  const font = await loadUiFont();
  const W = 240, H = 44, R = 8;

  const comp = figma.createComponent();
  comp.name = 'InputField'; comp.resize(W, H); comp.fills = []; comp.clipsContent = true;

  const bg = solidRect('Background', W, H, R, { r: 1, g: 1, b: 1 });
  bg.strokes = [{ type: 'SOLID', color: { r: 0.8, g: 0.8, b: 0.85 } }]; bg.strokeWeight = 1;
  bg.x = 0; bg.y = 0; bg.constraints = { horizontal: 'STRETCH', vertical: 'STRETCH' };
  comp.appendChild(bg);

  const placeholder = figma.createText();
  placeholder.fontName = font; placeholder.name = 'Placeholder'; placeholder.characters = 'Enter text';
  placeholder.fontSize = 14; placeholder.fills = [{ type: 'SOLID', color: { r: 0.55, g: 0.56, b: 0.62 } }];
  placeholder.textAlignVertical = 'CENTER';
  placeholder.textAutoResize = 'NONE';
  placeholder.resize(W - 24, H);
  comp.appendChild(placeholder);
  placeholder.x = 12; placeholder.y = 0;
  placeholder.constraints = { horizontal: 'STRETCH', vertical: 'CENTER' };

  const value = figma.createText();
  value.fontName = font; value.name = 'Text'; value.characters = ' ';
  value.fontSize = 14; value.fills = [{ type: 'SOLID', color: { r: 0.1, g: 0.1, b: 0.12 } }];
  value.textAlignVertical = 'CENTER';
  value.textAutoResize = 'NONE';
  value.resize(W - 24, H);
  comp.appendChild(value);
  value.x = 12; value.y = 0;
  value.constraints = { horizontal: 'STRETCH', vertical: 'CENTER' };

  const hit = solidRect('HitArea', W, H, 0, { r: 0, g: 0, b: 0 }, 0);
  hit.x = 0; hit.y = 0; hit.constraints = { horizontal: 'STRETCH', vertical: 'STRETCH' };
  comp.appendChild(hit);

  comp.setSharedPluginData('figforge', 'canonical', JSON.stringify({ kind: 'input', ref: 'InputField' }));
  parkMaster(comp);
  placeInstance(comp);
  return comp;
}

async function normalizeInputFieldMaster(comp: ComponentNode): Promise<void> {
  const text = comp.findOne((n) => n.type === 'TEXT' && (n.name === 'Text' || n.name === 'Value')) as TextNode | null;
  if (!text) return;
  if (text.fontName !== figma.mixed) await figma.loadFontAsync(text.fontName as FontName);
  if (text.characters.length === 0) text.characters = ' ';
  text.visible = true;
  text.textAutoResize = 'NONE';
  text.resize(Math.max(1, comp.width - 24), comp.height);
  text.x = 12;
  text.y = 0;

  const placeholder = comp.findOne((n) => n.type === 'TEXT' && (n.name === 'Placeholder' || n.name === 'Label')) as TextNode | null;
  if (placeholder) {
    if (placeholder.fontName !== figma.mixed) await figma.loadFontAsync(placeholder.fontName as FontName);
    placeholder.textAutoResize = 'NONE';
    placeholder.resize(Math.max(1, comp.width - 24), comp.height);
    placeholder.x = 12;
    placeholder.y = 0;
  }
}

// The reusable dropdown option row — its own component (Regular/Rollover/Pressed/
// HitArea/Label) so the popup item template + its hover/press states are skinned once.
async function ensureDropdownOption(font: FontName, W: number, ROW: number): Promise<ComponentNode> {
  const found = findMaster('DropdownOption');
  if (found) return found;
  const item = figma.createComponent();
  item.name = 'DropdownOption'; item.resize(W, ROW); item.fills = []; item.clipsContent = true;
  const reg = solidRect('Regular', W, ROW, 0, { r: 1, g: 1, b: 1 });
  reg.constraints = { horizontal: 'STRETCH', vertical: 'STRETCH' }; item.appendChild(reg);
  const roll = solidRect('Rollover', W, ROW, 0, { r: 0.93, g: 0.95, b: 1 });
  roll.visible = false; roll.constraints = { horizontal: 'STRETCH', vertical: 'STRETCH' }; item.appendChild(roll);
  const press = solidRect('Pressed', W, ROW, 0, { r: 0.85, g: 0.89, b: 1 });
  press.visible = false; press.constraints = { horizontal: 'STRETCH', vertical: 'STRETCH' }; item.appendChild(press);
  // Selected = the current value, distinct from hover (a stronger accent fill).
  const sel = solidRect('Selected', W, ROW, 0, { r: 0.80, g: 0.84, b: 1 });
  sel.visible = false; sel.constraints = { horizontal: 'STRETCH', vertical: 'STRETCH' }; item.appendChild(sel);
  const hit = solidRect('HitArea', W, ROW, 0, { r: 0, g: 0, b: 0 }, 0);
  hit.constraints = { horizontal: 'STRETCH', vertical: 'STRETCH' }; item.appendChild(hit);
  const t = figma.createText();
  t.fontName = font; t.name = 'Label'; t.characters = 'Option'; t.fontSize = 14;
  t.fills = [{ type: 'SOLID', color: { r: 0.1, g: 0.1, b: 0.12 } }]; t.textAlignVertical = 'CENTER';
  item.appendChild(t); t.x = 12; t.y = (ROW - t.height) / 2; t.constraints = { horizontal: 'STRETCH', vertical: 'CENTER' };
  parkMaster(item);
  return item;
}

// Dropdown: a Background + caption Label + Arrow (closed look, 40px), referencing a
// DropdownOption component for the popup item styling/states. The "Options" frame below
// holds a few DropdownOption INSTANCES as a visible open-list preview (the importer reads
// the option master's states; the closed control stays the Background's 40px in Unity).
// Hide the open-list "Options" preview on a PLACED dropdown instance (a per-instance
// override) so it only shows the closed box on the page. The MASTER keeps the preview
// visible (that's where the option look/length is specified), and capture reads it there.
function hideInstanceOptions(inst: InstanceNode): void {
  const opts = inst.findOne((n) => n.name === 'Options');
  if (opts) (opts as unknown as { visible: boolean }).visible = false;
}

async function createDropdown(): Promise<ComponentNode> {
  const reuse = findMaster('Dropdown');
  if (reuse) { hideInstanceOptions(placeInstance(reuse)); return reuse; }

  const font = await loadUiFont();
  const W = 220, H = 40, R = 8, ROW = 36;
  const optComp = await ensureDropdownOption(font, W, ROW);

  const comp = figma.createComponent();
  comp.name = 'Dropdown'; comp.resize(W, H); comp.fills = []; comp.clipsContent = false; // preview overflows below

  const bg = solidRect('Background', W, H, R, { r: 1, g: 1, b: 1 });
  bg.strokes = [{ type: 'SOLID', color: { r: 0.8, g: 0.8, b: 0.85 } }]; bg.strokeWeight = 1;
  bg.x = 0; bg.y = 0; bg.constraints = { horizontal: 'STRETCH', vertical: 'STRETCH' };
  comp.appendChild(bg);

  // Closed-control hover/press fills for the box itself (hidden colour carriers; Unity
  // swaps the Background fill on the dropdown's pointer state, like the arrow chevron).
  const bgRoll = solidRect('BgRollover', W, H, R, { r: 0.96, g: 0.97, b: 1 });
  bgRoll.visible = false; bgRoll.x = 0; bgRoll.y = 0; bgRoll.constraints = { horizontal: 'STRETCH', vertical: 'STRETCH' };
  comp.appendChild(bgRoll);
  const bgPress = solidRect('BgPressed', W, H, R, { r: 0.90, g: 0.92, b: 1 });
  bgPress.visible = false; bgPress.x = 0; bgPress.y = 0; bgPress.constraints = { horizontal: 'STRETCH', vertical: 'STRETCH' };
  comp.appendChild(bgPress);

  const label = figma.createText();
  label.fontName = font; label.name = 'Label'; label.characters = 'Option 1';
  label.fontSize = 14; label.fills = [{ type: 'SOLID', color: { r: 0.1, g: 0.1, b: 0.12 } }];
  label.textAlignVertical = 'CENTER';
  comp.appendChild(label); label.x = 12; label.y = (H - label.height) / 2;
  label.constraints = { horizontal: 'STRETCH', vertical: 'CENTER' };

  // Arrow — a stateful chevron (Regular / Rollover / Pressed glyph + HitArea) so the open
  // affordance reacts to the dropdown's hover/press in Unity. Only Regular shows by default.
  const ARROW = 28;
  const arrow = figma.createFrame();
  arrow.name = 'Arrow'; arrow.resize(ARROW, H); arrow.fills = []; arrow.x = W - ARROW - 6; arrow.y = 0;
  arrow.constraints = { horizontal: 'MAX', vertical: 'STRETCH' };
  comp.appendChild(arrow);
  const chevron = (name: string, color: RGB, visible: boolean) => {
    // Crisp downward-triangle VECTOR: createNodeFromSvg sizes correctly from the
    // viewBox (raw createVector does not — it stays ~100×100 and renders as a block),
    // then flatten to a single VectorNode whose solid fill the Unity capture reads
    // exactly like the old text glyph did.
    const svg = figma.createNodeFromSvg(
      '<svg xmlns="http://www.w3.org/2000/svg" width="12" height="7" viewBox="0 0 12 7"><path d="M0 0 L12 0 L6 7 Z" fill="#000000"/></svg>',
    );
    const v = figma.flatten([svg], arrow);
    v.name = name;
    v.fills = [{ type: 'SOLID', color }];
    v.strokes = [];
    v.visible = visible;
    v.x = Math.round((ARROW - v.width) / 2);
    v.y = Math.round((H - v.height) / 2);
    v.constraints = { horizontal: 'CENTER', vertical: 'CENTER' };
  };
  chevron('Regular', { r: 0.4, g: 0.4, b: 0.45 }, true);
  chevron('Rollover', { r: 0.18, g: 0.18, b: 0.22 }, false);
  chevron('Pressed', { r: 0.49, g: 0.36, b: 1 }, false);
  const arrowHit = solidRect('HitArea', ARROW, H, 0, { r: 0, g: 0, b: 0 }, 0);
  arrowHit.x = 0; arrowHit.y = 0; arrowHit.constraints = { horizontal: 'STRETCH', vertical: 'STRETCH' };
  arrow.appendChild(arrowHit);

  // Open-list preview: 3 DropdownOption instances stacked below (the importer reads the
  // first one's master for the item template; the option texts come from the labels).
  const opts = figma.createFrame();
  opts.name = 'Options'; opts.resize(W, ROW * 3); opts.fills = [];
  opts.x = 0; opts.y = H + 4; opts.constraints = { horizontal: 'STRETCH', vertical: 'MIN' };
  comp.appendChild(opts);
  for (let i = 0; i < 3; i++) {
    const ins = optComp.createInstance();
    ins.name = 'Option'; ins.resize(W, ROW); ins.x = 0; ins.y = i * ROW;
    ins.constraints = { horizontal: 'STRETCH', vertical: 'MIN' };
    opts.appendChild(ins);
    const lbl = ins.findOne((n) => n.type === 'TEXT' && n.name === 'Label') as TextNode | null;
    if (lbl) lbl.characters = `Option ${i + 1}`;
  }

  comp.setSharedPluginData('figforge', 'canonical', JSON.stringify({ kind: 'dropdown', ref: 'Dropdown', value: 'Option 1' }));
  parkMaster(comp);
  hideInstanceOptions(placeInstance(comp));
  return comp;
}

// Load a bold/medium weight of the same family for titles; fall back to the base font.
async function loadBoldFont(base: FontName): Promise<FontName> {
  for (const style of ['Semi Bold', 'SemiBold', 'Bold', 'Medium']) {
    const f: FontName = { family: base.family, style };
    try { await figma.loadFontAsync(f); return f; } catch { /* try next */ }
  }
  return base;
}

// The reusable row used by List — its own component so every row in the composited
// preview stays in sync when you skin it once. Rich form: full-bleed state layers
// (Regular/Rollover/Pressed/Selected), a leading Icon, two-line Title + Subtitle, a
// trailing Accessory chevron, a bottom Divider, and a transparent HitArea on top.
// Which optional pieces a List variant includes. One '+List' button, many shapes:
// no header section, icon-less rows, subtitle-less rows, scrollbar thickness — any
// combination. Each combination is its own master/canonical ref so variants coexist
// and reuse cleanly (a non-default scrollbar width becomes part of the name).
export type ListOptions = { header: boolean; icon: boolean; subtitle: boolean; scrollbarWidth: number };
const DEFAULT_SCROLLBAR_WIDTH = 10;
const LIST_DEFAULTS: ListOptions = { header: true, icon: true, subtitle: true, scrollbarWidth: DEFAULT_SCROLLBAR_WIDTH };

function clampScrollbarWidth(w: unknown): number {
  const n = typeof w === 'number' && isFinite(w) ? Math.round(w) : DEFAULT_SCROLLBAR_WIDTH;
  return Math.min(40, Math.max(2, n));
}

function listItemVariantName(o: ListOptions): string {
  const p: string[] = [];
  if (!o.icon) p.push('NoIcon');
  if (!o.subtitle) p.push('NoSubtitle');
  return 'ListItem' + (p.length ? ' ' + p.join(' ') : '');
}

function listVariantName(o: ListOptions): string {
  const p: string[] = [];
  if (!o.header) p.push('NoHeader');
  if (!o.icon) p.push('NoIcon');
  if (!o.subtitle) p.push('NoSubtitle');
  const sb = clampScrollbarWidth(o.scrollbarWidth);
  if (sb !== DEFAULT_SCROLLBAR_WIDTH) p.push('SB' + sb);
  return 'List' + (p.length ? ' ' + p.join(' ') : '');
}

async function ensureListItem(font: FontName, titleFont: FontName, W: number, ROW: number,
                              opts: ListOptions): Promise<ComponentNode> {
  const itemName = listItemVariantName(opts);
  const found = findMaster(itemName);
  if (found) return found;
  const item = figma.createComponent();
  item.name = itemName; item.resize(W, ROW); item.fills = []; item.clipsContent = true;

  // Pointer/selection state backgrounds (full-bleed). Only Regular shows by default;
  // Unity swaps these on hover/press and keeps Selected on the chosen row.
  const reg = solidRect('Regular', W, ROW, 0, { r: 1, g: 1, b: 1 });
  reg.constraints = { horizontal: 'STRETCH', vertical: 'STRETCH' }; item.appendChild(reg);
  const roll = solidRect('Rollover', W, ROW, 0, { r: 0.96, g: 0.96, b: 1 });
  roll.visible = false; roll.constraints = { horizontal: 'STRETCH', vertical: 'STRETCH' }; item.appendChild(roll);
  const press = solidRect('Pressed', W, ROW, 0, { r: 0.92, g: 0.92, b: 1 });
  press.visible = false; press.constraints = { horizontal: 'STRETCH', vertical: 'STRETCH' }; item.appendChild(press);
  const sel = solidRect('Selected', W, ROW, 0, { r: 0.90, g: 0.91, b: 1 });
  sel.visible = false; sel.constraints = { horizontal: 'STRETCH', vertical: 'STRETCH' }; item.appendChild(sel);

  const PAD = 14, ICON = 40;
  // Row content in a horizontal AUTO-LAYOUT frame: Icon | TextCol(grows) | Accessory.
  // Because it's auto-layout, deleting the Icon reflows the Title/Subtitle to the left
  // automatically — the icon-less version "just works" with no manual repositioning.
  const content = figma.createFrame();
  content.name = 'Content'; content.fills = []; content.clipsContent = false;
  content.x = 0; content.y = 0; content.resize(W, ROW);
  content.layoutMode = 'HORIZONTAL';
  content.primaryAxisSizingMode = 'FIXED';
  content.counterAxisSizingMode = 'FIXED';
  content.primaryAxisAlignItems = 'MIN';
  content.counterAxisAlignItems = 'CENTER';
  content.itemSpacing = 12;
  content.paddingLeft = PAD; content.paddingRight = PAD;
  content.constraints = { horizontal: 'STRETCH', vertical: 'STRETCH' };
  item.appendChild(content);

  // Leading icon slot — fixed size; delete it and the text reflows left.
  if (opts.icon) {
    const icon = solidRect('Icon', ICON, ICON, 10, { r: 0.49, g: 0.36, b: 1 });
    content.appendChild(icon); icon.layoutGrow = 0;
  }

  // Title + Subtitle in a vertical column that grows to fill the remaining width.
  const textCol = figma.createFrame();
  textCol.name = 'TextCol'; textCol.fills = [];
  textCol.layoutMode = 'VERTICAL';
  textCol.primaryAxisSizingMode = 'FIXED';
  textCol.counterAxisSizingMode = 'FIXED';
  textCol.primaryAxisAlignItems = 'CENTER';
  textCol.counterAxisAlignItems = 'MIN';
  textCol.itemSpacing = 2;
  content.appendChild(textCol); textCol.layoutGrow = 1; textCol.layoutAlign = 'STRETCH';

  const title = figma.createText();
  title.fontName = titleFont; title.name = 'Title'; title.characters = 'Title'; title.fontSize = 15;
  title.fills = [{ type: 'SOLID', color: { r: 0.1, g: 0.1, b: 0.12 } }];
  textCol.appendChild(title); title.textAutoResize = 'HEIGHT'; title.layoutAlign = 'STRETCH';
  if (opts.subtitle) {
    const sub = figma.createText();
    sub.fontName = font; sub.name = 'Subtitle'; sub.characters = 'Subtitle'; sub.fontSize = 12;
    sub.fills = [{ type: 'SOLID', color: { r: 0.5, g: 0.5, b: 0.55 } }];
    textCol.appendChild(sub); sub.textAutoResize = 'HEIGHT'; sub.layoutAlign = 'STRETCH';
  }

  // Trailing accessory — a chevron-right vector (skin/replace freely); fixed size.
  const svg = figma.createNodeFromSvg(
    '<svg xmlns="http://www.w3.org/2000/svg" width="7" height="12" viewBox="0 0 7 12"><path d="M0 0 L7 6 L0 12 Z" fill="#000000"/></svg>');
  const acc = figma.flatten([svg], content);
  acc.name = 'Accessory'; acc.fills = [{ type: 'SOLID', color: { r: 0.6, g: 0.6, b: 0.65 } }]; acc.strokes = [];
  acc.layoutGrow = 0;

  // Bottom divider separator between rows.
  const div = solidRect('Divider', W - PAD * 2, 1, 0, { r: 0.9, g: 0.9, b: 0.93 });
  div.x = PAD; div.y = ROW - 1; div.constraints = { horizontal: 'STRETCH', vertical: 'MAX' }; item.appendChild(div);

  // Transparent full-bleed hit target — last so it sits on top.
  const hit = solidRect('HitArea', W, ROW, 0, { r: 0, g: 0, b: 0 }, 0);
  hit.constraints = { horizontal: 'STRETCH', vertical: 'STRETCH' }; item.appendChild(hit);

  parkMaster(item); // loose on the current page, off-canvas
  return item;
}

// List: a rounded, clipped Background + a Header (section title) + a COMPOSITED stack of
// ListItem instances (so you see a real multi-row list in Figma and copy it onto your
// page). Skin the one ListItem master (Regular/Rollover/Pressed/Selected states, Icon,
// Title, Subtitle, Accessory, Divider) and every row updates. In Unity the row is
// repeated `count` times (count = (list height − header) ÷ row height) with state swaps.
async function createList(opts: ListOptions = LIST_DEFAULTS): Promise<ComponentNode> {
  const name = listVariantName(opts);
  const sbWidth = clampScrollbarWidth(opts.scrollbarWidth);
  const reuse = findMaster(name);
  if (reuse) { ensureListScrollbar(reuse, sbWidth); ensureListRoundedClip(reuse); ensureListMask(reuse); placeInstance(reuse); return reuse; }

  const font = await loadUiFont();
  const titleFont = await loadBoldFont(font);
  // Subtitle-less rows are shorter (single text line); header-less lists drop the section strip.
  const W = 320, ROW = opts.subtitle ? 64 : 48, HEADER = opts.header ? 44 : 0, ROWS = 4, H = HEADER + ROW * ROWS, R = 14;
  const itemComp = await ensureListItem(font, titleFont, W, ROW, opts);

  const comp = figma.createComponent();
  comp.name = name; comp.resize(W, H); comp.fills = []; comp.clipsContent = true;

  const bg = solidRect('Background', W, H, R, { r: 1, g: 1, b: 1 });
  bg.strokes = [{ type: 'SOLID', color: { r: 0.82, g: 0.83, b: 0.88 } }]; bg.strokeWeight = 1;
  bg.x = 0; bg.y = 0; bg.constraints = { horizontal: 'STRETCH', vertical: 'STRETCH' };
  comp.appendChild(bg);

  // Header — a section title row pinned to the top (above the scrollable rows in Unity).
  if (opts.header) {
    const header = figma.createFrame();
    header.name = 'Header'; header.resize(W, HEADER); header.fills = []; header.x = 0; header.y = 0;
    header.constraints = { horizontal: 'STRETCH', vertical: 'MIN' };
    comp.appendChild(header);
    const ht = figma.createText();
    ht.fontName = titleFont; ht.name = 'Title'; ht.characters = 'Section'; ht.fontSize = 13;
    ht.fills = [{ type: 'SOLID', color: { r: 0.45, g: 0.45, b: 0.5 } }]; ht.textAlignVertical = 'CENTER';
    header.appendChild(ht); ht.x = 14; ht.y = Math.round((HEADER - ht.height) / 2);
    ht.constraints = { horizontal: 'STRETCH', vertical: 'CENTER' };
    const hdiv = solidRect('Divider', W, 1, 0, { r: 0.88, g: 0.88, b: 0.92 });
    hdiv.x = 0; hdiv.y = HEADER - 1; hdiv.constraints = { horizontal: 'STRETCH', vertical: 'MAX' };
    header.appendChild(hdiv);
  }

  // Composited preview: ROWS instances of the single ListItem master (the first is the
  // 'Item' the importer reads; all stay in sync when you skin the master). Row text is
  // a per-instance override; the first row previews the Selected state.
  const subtitles = ['Details', 'Subtitle', 'More info', 'Description'];
  for (let i = 0; i < ROWS; i++) {
    const ins = itemComp.createInstance();
    ins.name = 'Item'; ins.resize(W, ROW); ins.x = 0; ins.y = HEADER + i * ROW;
    ins.constraints = { horizontal: 'STRETCH', vertical: 'MIN' };
    comp.appendChild(ins);
    const title = ins.findOne((n) => n.type === 'TEXT' && n.name === 'Title') as TextNode | null;
    if (title) title.characters = `Item ${i + 1}`;
    const sub = ins.findOne((n) => n.type === 'TEXT' && n.name === 'Subtitle') as TextNode | null;
    if (sub) sub.characters = subtitles[i % subtitles.length];
    if (i === 0) {
      const selected = ins.findOne((n) => n.name === 'Selected');
      if (selected) (selected as unknown as { visible: boolean }).visible = true;
    }
  }

  ensureListScrollbar(comp, sbWidth);
  ensureListRoundedClip(comp);
  ensureListMask(comp);
  comp.setSharedPluginData('figforge', 'canonical', JSON.stringify({ kind: 'list', ref: name }));
  parkMaster(comp);
  placeInstance(comp);
  return comp;
}

// Add the (hidden) 'Mask' layer that defines the scroll/clip region — the designer's
// handle on clipping. Defaults to the Background interior minus the Header (the same
// box Unity would derive): inset past the bg stroke + 1px. Move/resize it to control
// exactly where rows render; Unity anchors the viewport to it and the scrollbar hugs
// its right edge. Retrofitted onto existing masters like the Scrollbar layer.
function ensureListMask(comp: ComponentNode): void {
  if (!('children' in comp)) return;
  const kids = (comp as ChildrenMixin).children as SceneNode[];
  if (kids.some((c) => c.name === 'Mask')) return;
  const W = comp.width, H = comp.height;
  const header = kids.find((c) => c.name === 'Header');
  const HEADER = header ? (header as unknown as { height: number }).height : 0;
  const bg = kids.find((c) => c.name === 'Background');
  const strokeW = bg && typeof (bg as unknown as { strokeWeight?: unknown }).strokeWeight === 'number'
    ? (bg as unknown as { strokeWeight: number }).strokeWeight : 0;
  const bgRadius = bg && typeof (bg as unknown as { cornerRadius?: unknown }).cornerRadius === 'number'
    ? (bg as unknown as { cornerRadius: number }).cornerRadius : 0;
  const inset = strokeW + 1;

  // The Mask's OWN corner radius defines the clip rounding in Unity (rounded stencil
  // mask) — default it to the Background's radius so rows follow the container curve.
  const mask = solidRect('Mask', Math.max(1, W - inset * 2), Math.max(1, H - HEADER - inset * 2),
    Math.max(0, bgRadius - inset), { r: 0.2, g: 0.55, b: 1 }, 0.12); // guide tint; hidden anyway
  mask.x = inset; mask.y = HEADER + inset;
  mask.visible = false;
  mask.constraints = { horizontal: 'STRETCH', vertical: 'STRETCH' };
  comp.appendChild(mask);
}

// Round the List COMPONENT FRAME to the Background's radius so clipsContent clips
// the square rows to the rounded shape — without this a header-less list's first
// row paints square corners over the Background's rounded top. (Unity mirrors this
// by rounding the first/last row backgrounds.)
function ensureListRoundedClip(comp: ComponentNode): void {
  if (!('children' in comp)) return;
  const bg = (comp as ChildrenMixin).children.find((c) => c.name === 'Background');
  const r = bg && 'cornerRadius' in bg && typeof (bg as unknown as { cornerRadius?: unknown }).cornerRadius === 'number'
    ? (bg as unknown as { cornerRadius: number }).cornerRadius : 0;
  if (r > 0) { (comp as unknown as { cornerRadius: number }).cornerRadius = r; comp.clipsContent = true; }
}

// Add a skinnable 'Scrollbar' layer (Track + Thumb + hidden ThumbRollover/ThumbPressed
// state colours) to a List master that lacks one — and retrofit the state layers into
// an existing Scrollbar frame. The exporter captures width/shapes/state colours so
// Unity styles the real uGUI Scrollbar; in Figma the layer is a static preview.
function ensureListScrollbar(comp: ComponentNode, width: number = DEFAULT_SCROLLBAR_WIDTH): void {
  if (!('children' in comp)) return;
  let sb = (comp as ChildrenMixin).children.find((c) => c.name === 'Scrollbar') as FrameNode | undefined;
  if (!sb) {
    const W = comp.width, H = comp.height;
    const header = (comp as ChildrenMixin).children.find((c) => c.name === 'Header');
    const HEADER = header ? (header as unknown as { height: number }).height : 0;
    const SB = clampScrollbarWidth(width), PAD = 3;

    sb = figma.createFrame();
    sb.name = 'Scrollbar'; sb.fills = [];
    sb.resize(SB, Math.max(24, H - HEADER - PAD * 2));
    sb.x = W - SB - PAD; sb.y = HEADER + PAD;
    sb.constraints = { horizontal: 'MAX', vertical: 'STRETCH' };
    comp.appendChild(sb);

    const track = solidRect('Track', SB, sb.height, SB / 2, { r: 0, g: 0, b: 0 }, 0.06);
    track.x = 0; track.y = 0; track.constraints = { horizontal: 'STRETCH', vertical: 'STRETCH' };
    sb.appendChild(track);

    const thumb = solidRect('Thumb', SB, Math.max(24, Math.round(sb.height * 0.4)), SB / 2, { r: 0.55, g: 0.56, b: 0.6 }, 0.55);
    thumb.x = 0; thumb.y = 0; thumb.constraints = { horizontal: 'STRETCH', vertical: 'MIN' };
    sb.appendChild(thumb);
  }

  // Hidden hover/press colour layers for the thumb (same convention as row states).
  const kids = (sb as ChildrenMixin).children as SceneNode[];
  const thumbNode = kids.find((c) => c.name === 'Thumb');
  const tw = thumbNode ? (thumbNode as unknown as { width: number }).width : 6;
  const th = thumbNode ? (thumbNode as unknown as { height: number }).height : 24;
  const tcr = thumbNode && typeof (thumbNode as unknown as { cornerRadius?: unknown }).cornerRadius === 'number'
    ? (thumbNode as unknown as { cornerRadius: number }).cornerRadius : tw / 2;
  if (!kids.some((c) => c.name === 'ThumbRollover')) {
    const roll = solidRect('ThumbRollover', tw, th, tcr, { r: 0.42, g: 0.43, b: 0.48 }, 0.75);
    roll.visible = false; roll.x = 0; roll.y = 0;
    roll.constraints = { horizontal: 'STRETCH', vertical: 'MIN' };
    sb.appendChild(roll);
  }
  if (!kids.some((c) => c.name === 'ThumbPressed')) {
    const press = solidRect('ThumbPressed', tw, th, tcr, { r: 0.3, g: 0.31, b: 0.36 }, 0.85);
    press.visible = false; press.x = 0; press.y = 0;
    press.constraints = { horizontal: 'STRETCH', vertical: 'MIN' };
    sb.appendChild(press);
  }
}
