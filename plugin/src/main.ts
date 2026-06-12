// =============================================================================
// FigForge — plugin main thread (Figma sandbox)
//
// Owns the document: builds the layer tree for the UI, runs exports, and serves
// MCP requests forwarded by the UI from the bridge server.
// =============================================================================

import {
  DEFAULT_EXPORT_OPTIONS,
  DEFAULT_EXPORT_SCALE,
  type BinaryAsset,
  type CanonicalKind,
  type ElementConfig,
  type ExportOptions,
  type ExportScale,
} from './types';
import { bytesToBase64 } from './base64';
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

// One export at a time. exportDesign isolates layers by MUTATING node.visible
// around each exportAsync call; a second export started mid-run interleaves with
// the first pass and each bakes the other's hidden layers into its PNGs. Every
// entry point that runs exportDesign ('export', 'export-page', MCP export_unity)
// checks this flag and rejects a concurrent request with a visible message —
// never queued silently (the selection/configs may be stale by completion time,
// so the user should re-trigger deliberately). Cleared in `finally` on every path.
let exportInFlight = false;
const EXPORT_BUSY_MESSAGE = 'An export is already running — wait for it to finish, then try again.';

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
      // Rejected as 'status' (error-flagged), NOT 'export-error': ui.ts reacts to
      // export-error by tearing down the progress bar — UI state that belongs to
      // the export that's still legitimately running.
      if (exportInFlight) {
        figma.ui.postMessage({ type: 'status', message: EXPORT_BUSY_MESSAGE, error: true });
        break;
      }
      const root = selectedRoot();
      if (!root) {
        figma.ui.postMessage({ type: 'export-error', message: 'Select a frame to export.' });
        break;
      }
      exportInFlight = true;
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
          // Echo the click-time destination (zip download vs Unity POST) so the UI
          // routes THIS export's result — a module global in ui.ts would be
          // re-routed by a second (rejected) click while this one was running.
          target: (msg.target as string) || 'zip',
          manifest: JSON.stringify(result.manifest, null, 2),
          assets: result.assets,
        });
      } catch (e) {
        figma.ui.postMessage({ type: 'export-error', message: String((e as Error)?.message || e) });
      } finally {
        exportInFlight = false;
      }
      break;
    }

    case 'export-page': {
      if (exportInFlight) { // see 'export' — same gate, same reasoning
        figma.ui.postMessage({ type: 'status', message: EXPORT_BUSY_MESSAGE, error: true });
        break;
      }
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
      exportInFlight = true;
      try {
        const scale = (msg.scale as ExportScale) || DEFAULT_EXPORT_SCALE;
        const options = (msg.options as ExportOptions) || DEFAULT_EXPORT_OPTIONS;
        // Same per-element exclude/merge/force-PNG handling as the single-frame
        // 'export' path: sync the saved configs, then pass the sets to every
        // frame's exportDesign — the ids are page-wide, each frame picks up the
        // ones under it. Page export used to silently ignore all of this.
        applyConfigs(msg.elementConfigs as ElementConfig[] | undefined);
        const screens: {
          name: string; manifest: string; assets: BinaryAsset[];
          section: string; role: string;
        }[] = [];
        for (let i = 0; i < found.length; i++) {
          figma.ui.postMessage({ type: 'progress', current: i, total: found.length, label: found[i].node.name });
          const result = await exportDesign(found[i].node, scale, options, excluded, merged, forcedPng);
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
          target: (msg.target as string) || 'zip', // click-time destination, echoed — see 'export'
          project: { name: figma.currentPage.name, initial: firstScreen ? firstScreen.name : '' },
          screens,
        });
      } catch (e) {
        figma.ui.postMessage({ type: 'export-error', message: String((e as Error)?.message || e) });
      } finally {
        exportInFlight = false;
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
          (msg as { listOpts?: Partial<ListOptions> }).listOpts,
          (msg as { sliderOpts?: Partial<SliderOptions> }).sliderOpts,
          (msg as { tableOpts?: Partial<TableOptions> }).tableOpts,
          (msg as { progressOpts?: Partial<ProgressOptions> }).progressOpts);
        const where = useComponentsPage ? 'on the FigForge Components page' : 'parked on this page (off to the left)';
        figma.ui.postMessage({ type: 'status', message: `${comp.name} instance placed. Master is ${where} — skin it; click again to add more (group related radios together).` });
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
      // Uint8Array structured-clones through postMessage — no number[] blow-up.
      imageData: bytes,
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
        // MCP responses leave the plugin as JSON (ui.ts → WebSocket → bridge),
        // so the PNG bytes go base64 here: ~1.33× the binary size on the wire
        // vs ~3.7× as decimal number[] text. The bridge (server/src/tools.ts)
        // accepts both forms during the transition.
        const shots: { nodeId: string; data: string }[] = [];
        for (const id of ids) {
          const node = figma.getNodeById(id) as SceneNode | null;
          if (node && 'exportAsync' in node) {
            const bytes = await (node as unknown as {
              exportAsync: (s: ExportSettings) => Promise<Uint8Array>;
            }).exportAsync({ format: 'PNG', constraint: { type: 'SCALE', value: scale } });
            shots.push({ nodeId: id, data: bytesToBase64(bytes) });
          }
        }
        response.data = { screenshots: shots };
        break;
      }
      case 'export_unity': {
        // Full Unity export reusing the UI's exporter, driven over MCP so an
        // agent can batch every frame itself. (FigmaTest feature.)
        // Shares the one-export-at-a-time gate with the UI buttons: this path
        // mutates node.visible exactly the same way. Throwing here surfaces the
        // rejection to the bridge as a normal MCP error response.
        if (exportInFlight) throw new Error(EXPORT_BUSY_MESSAGE);
        exportInFlight = true;
        try {
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
                // JSON wire boundary (see get_screenshot above): base64, not
                // number[] — server/src/tools.ts executeExportUnity decodes
                // either form.
                assets: result.assets.map((a) => ({ name: a.name, data: bytesToBase64(a.data) })),
              });
            }
          }
          response.data = { exports };
        } finally {
          exportInFlight = false;
        }
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
const FIGFORGE_MASTERS = ['Button', 'Toggle', 'Radio', 'Switch', 'InputField', 'Stepper', 'Dropdown', 'Slider', 'Progress', 'List', 'ListItem', 'Table', 'TableRow'];

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
    // Variant masters ('List NoHeader', 'Slider 0to100 S5') stack too — match the
    // base name with a space-delimited suffix, not just the exact defaults.
    const isMaster = n !== comp && n.type === 'COMPONENT'
      && FIGFORGE_MASTERS.some((m) => n.name === m || n.name.startsWith(m + ' '));
    if (isMaster) {
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

// Dispatch a "+Toggle / +Radio / +Dropdown / +Slider / +Progress / +List / +Table" create request to its builder.
async function createCanonical(kind: string, listOpts?: Partial<ListOptions>,
                               sliderOpts?: Partial<SliderOptions>,
                               tableOpts?: Partial<TableOptions>,
                               progressOpts?: Partial<ProgressOptions>): Promise<ComponentNode> {
  switch (kind) {
    case 'toggle': return createToggleLike('toggle', 'Toggle', false);
    case 'radio': return createToggleLike('radio', 'Radio', true);
    case 'switch': return createSwitch();
    case 'input': return createInputField();
    case 'stepper': return createStepper();
    case 'dropdown': return createDropdown();
    case 'slider': return createSlider(sliderOpts);
    case 'progress': return createProgress(progressOpts);
    case 'list': return createList({ ...LIST_DEFAULTS, ...(listOpts ?? {}) });
    case 'table': return createTable({ ...TABLE_DEFAULTS, ...(tableOpts ?? {}) });
    default: throw new Error(`unknown canonical kind '${kind}'`);
  }
}

// Toggle / Radio: a Background box (UGUI Toggle.targetGraphic) + a Checkmark shown
// when on (Toggle.graphic) + HitArea + Label. Radio is circular and grouped in Unity
// by its parent frame/group. Off by default.
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

// Switch: an iOS-style Toggle. Track is the off rail, Fill is the on tint, and
// Thumb slides between the two ends in Unity while the Toggle value plumbing
// remains the same as checkbox/radio.
async function createSwitch(): Promise<ComponentNode> {
  const reuse = findMaster('Switch');
  if (reuse) { placeInstance(reuse); return reuse; }

  const W = 56, H = 32, PAD = 3, THUMB = 26;
  const comp = figma.createComponent();
  comp.name = 'Switch'; comp.resize(W, H); comp.fills = [];

  const track = solidRect('Track', W, H, H / 2, { r: 0.82, g: 0.84, b: 0.88 });
  track.x = 0; track.y = 0; track.constraints = { horizontal: 'STRETCH', vertical: 'STRETCH' };
  comp.appendChild(track);

  const fill = solidRect('Fill', W, H, H / 2, { r: 0.2, g: 0.78, b: 0.35 });
  fill.x = 0; fill.y = 0; fill.visible = false; fill.constraints = { horizontal: 'STRETCH', vertical: 'STRETCH' };
  comp.appendChild(fill);

  const thumb = solidRect('Thumb', THUMB, THUMB, THUMB / 2, { r: 1, g: 1, b: 1 });
  thumb.effects = [{ type: 'DROP_SHADOW', color: { r: 0, g: 0, b: 0, a: 0.22 }, offset: { x: 0, y: 1 }, radius: 3, spread: 0, visible: true, blendMode: 'NORMAL' }];
  thumb.x = PAD; thumb.y = PAD; thumb.constraints = { horizontal: 'MIN', vertical: 'CENTER' };
  comp.appendChild(thumb);

  const roll = solidRect('ThumbRollover', THUMB, THUMB, THUMB / 2, { r: 0.95, g: 0.94, b: 1 });
  roll.visible = false; roll.x = thumb.x; roll.y = thumb.y; roll.constraints = { horizontal: 'MIN', vertical: 'CENTER' };
  comp.appendChild(roll);

  const press = solidRect('ThumbPressed', THUMB, THUMB, THUMB / 2, { r: 0.88, g: 0.86, b: 1 });
  press.visible = false; press.x = thumb.x; press.y = thumb.y; press.constraints = { horizontal: 'MIN', vertical: 'CENTER' };
  comp.appendChild(press);

  const hit = solidRect('HitArea', W, H, 0, { r: 0, g: 0, b: 0 }, 0);
  hit.x = 0; hit.y = 0; hit.constraints = { horizontal: 'STRETCH', vertical: 'STRETCH' };
  comp.appendChild(hit);

  comp.setSharedPluginData('figforge', 'canonical', JSON.stringify({ kind: 'switch', ref: 'Switch', value: 'off' }));
  parkMaster(comp);
  placeInstance(comp);
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

// Stepper: numeric input with canonical - / + buttons. The InputField child reuses
// the input value binding; min/max/step are stored in the tag as slider-style
// numeric fields (slots = step).
async function createStepper(): Promise<ComponentNode> {
  const reuse = findMaster('Stepper');
  if (reuse) { placeInstance(reuse); return reuse; }

  const font = await loadUiFont();
  const BTN = 40, FIELD = 72, H = 40, W = BTN * 2 + FIELD, R = 8;
  const comp = figma.createComponent();
  comp.name = 'Stepper'; comp.resize(W, H); comp.fills = []; comp.clipsContent = true;

  const makeButton = (name: string, x: number, label: string): void => {
    const bg = solidRect(name, BTN, H, R, { r: 0.96, g: 0.97, b: 1 });
    bg.strokes = [{ type: 'SOLID', color: { r: 0.72, g: 0.74, b: 0.82 } }];
    bg.strokeWeight = 1; bg.x = x; bg.y = 0; bg.constraints = { horizontal: name === 'Minus' ? 'MIN' : 'MAX', vertical: 'STRETCH' };
    comp.appendChild(bg);
    const roll = solidRect(name + 'Rollover', BTN, H, R, { r: 0.93, g: 0.95, b: 1 });
    roll.visible = false; roll.x = x; roll.y = 0; roll.constraints = bg.constraints;
    comp.appendChild(roll);
    const press = solidRect(name + 'Pressed', BTN, H, R, { r: 0.85, g: 0.89, b: 1 });
    press.visible = false; press.x = x; press.y = 0; press.constraints = bg.constraints;
    comp.appendChild(press);
    const t = figma.createText();
    t.fontName = font; t.name = name + 'Label'; t.characters = label;
    t.fontSize = 18; t.textAlignHorizontal = 'CENTER'; t.textAlignVertical = 'CENTER';
    t.fills = [{ type: 'SOLID', color: { r: 0.2, g: 0.22, b: 0.28 } }];
    t.textAutoResize = 'NONE'; t.resize(BTN, H);
    t.x = x; t.y = 0; t.constraints = { horizontal: name === 'Minus' ? 'MIN' : 'MAX', vertical: 'STRETCH' };
    comp.appendChild(t);
  };

  makeButton('Minus', 0, '-');

  const input = solidRect('InputField', FIELD, H, 0, { r: 1, g: 1, b: 1 });
  input.strokes = [{ type: 'SOLID', color: { r: 0.72, g: 0.74, b: 0.82 } }];
  input.strokeWeight = 1; input.x = BTN; input.y = 0; input.constraints = { horizontal: 'STRETCH', vertical: 'STRETCH' };
  comp.appendChild(input);

  const text = figma.createText();
  text.fontName = font; text.name = 'Text'; text.characters = '0';
  text.fontSize = 14; text.textAlignHorizontal = 'CENTER'; text.textAlignVertical = 'CENTER';
  text.fills = [{ type: 'SOLID', color: { r: 0.1, g: 0.1, b: 0.12 } }];
  text.textAutoResize = 'NONE'; text.resize(FIELD, H);
  text.x = BTN; text.y = 0; text.constraints = { horizontal: 'STRETCH', vertical: 'STRETCH' };
  comp.appendChild(text);

  makeButton('Plus', BTN + FIELD, '+');

  comp.setSharedPluginData('figforge', 'canonical', JSON.stringify({
    kind: 'stepper', ref: 'Stepper',
    value: '0',
    minValue: 0, maxValue: 100, slots: 1,
  }));
  parkMaster(comp);
  placeInstance(comp);
  return comp;
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

// Which numeric behaviour a Slider variant has. One '+Slider' chip, many shapes:
// a custom [min..max] value range, discrete slots (tick marks + snapping), a live
// value read-out, or any combination. Each combination is its own master/
// canonical ref (the options become part of the name, e.g. 'Slider 0to100 S5 V')
// so variants coexist and reuse cleanly — the List variants convention.
export type SliderOptions = {
  range: boolean; min: number; max: number;
  slotted: boolean; slots: number;
  value: boolean; // show a 'Value' read-out text (top-right, live in Unity)
};
const SLIDER_DEFAULTS: SliderOptions = { range: false, min: 0, max: 1, slotted: false, slots: 5, value: false };

function normalizeSliderOptions(o: Partial<SliderOptions> | undefined): SliderOptions {
  const s = { ...SLIDER_DEFAULTS, ...(o ?? {}) };
  if (!isFinite(s.min)) s.min = 0;
  if (!isFinite(s.max)) s.max = 1;
  if (!s.range) { s.min = 0; s.max = 1; }
  if (s.max <= s.min) s.max = s.min + 1; // degenerate range — keep it draggable
  s.slots = s.slotted ? Math.min(100, Math.max(2, Math.round(s.slots || 0))) : 0;
  return s;
}

// Compact number for variant names/tag values ('0.5', '100', '-10').
function fmtSliderNum(n: number): string {
  return String(+n.toFixed(3));
}

function sliderVariantName(o: SliderOptions): string {
  const p: string[] = [];
  if (o.range && !(o.min === 0 && o.max === 1)) p.push(`${fmtSliderNum(o.min)}to${fmtSliderNum(o.max)}`);
  if (o.slots >= 2) p.push('S' + o.slots);
  if (o.value) p.push('V');
  return 'Slider' + (p.length ? ' ' + p.join(' ') : '');
}

// Slider: a Track (the rail) + Fill (the filled portion — its width ÷ the Track's
// width IS the initial value as a fraction of the range; resize it to set the
// default) + optional Ticks (slotted variants: one notch per slot, snapping in
// Unity) + Thumb (+ hidden ThumbRollover/ThumbPressed state-colour layers, same
// convention as the list scrollbar) + HitArea (the slider row's click/drag
// surface) + Label above. In Unity it becomes a real uGUI Slider; skin any layer
// here and instances update.
async function createSlider(optsIn?: Partial<SliderOptions>): Promise<ComponentNode> {
  const opts = normalizeSliderOptions(optsIn);
  const name = sliderVariantName(opts);
  const reuse = findMaster(name);
  if (reuse) { placeInstance(reuse); return reuse; }

  const font = await loadUiFont();
  const W = 240, LABEL_H = 20, ROW = 28, H = LABEL_H + ROW, TRACK = 6, THUMB = 18;
  // Initial position: mid-range, snapped to the nearest slot when slotted (so the
  // preview Fill/Thumb sit exactly on a notch and export a slot-aligned value).
  const ratio = opts.slots >= 2 ? Math.round(0.5 * (opts.slots - 1)) / (opts.slots - 1) : 0.5;
  const trackY = LABEL_H + (ROW - TRACK) / 2;

  const comp = figma.createComponent();
  comp.name = name; comp.resize(W, H); comp.fills = [];

  const track = solidRect('Track', W, TRACK, TRACK / 2, { r: 0.85, g: 0.86, b: 0.9 });
  track.x = 0; track.y = trackY; track.constraints = { horizontal: 'STRETCH', vertical: 'CENTER' };
  comp.appendChild(track);

  const fill = solidRect('Fill', W * ratio, TRACK, TRACK / 2, { r: 0.49, g: 0.36, b: 1 });
  fill.x = 0; fill.y = trackY; fill.constraints = { horizontal: 'MIN', vertical: 'CENTER' };
  comp.appendChild(fill);

  // Slot tick marks — one notch per slot across the track, over the fill (skin or
  // delete freely; Unity renders the layer as-is and snaps values to the slots).
  if (opts.slots >= 2) {
    const TICK_W = 2, TICK_H = TRACK + 6;
    const ticks = figma.createFrame();
    ticks.name = 'Ticks'; ticks.fills = [];
    ticks.resize(W, TICK_H);
    ticks.x = 0; ticks.y = trackY - (TICK_H - TRACK) / 2;
    ticks.constraints = { horizontal: 'STRETCH', vertical: 'CENTER' };
    comp.appendChild(ticks);
    for (let i = 0; i < opts.slots; i++) {
      const t = solidRect('Tick', TICK_W, TICK_H, 1, { r: 0.98, g: 0.98, b: 1 }, 0.9);
      t.x = Math.min(W - TICK_W, Math.max(0, (W * i) / (opts.slots - 1) - TICK_W / 2));
      t.y = 0;
      t.constraints = { horizontal: 'SCALE', vertical: 'STRETCH' };
      ticks.appendChild(t);
    }
  }

  const thumb = solidRect('Thumb', THUMB, THUMB, THUMB / 2, { r: 1, g: 1, b: 1 });
  thumb.strokes = [{ type: 'SOLID', color: { r: 0.49, g: 0.36, b: 1 } }];
  thumb.strokeWeight = 2;
  thumb.x = W * ratio - THUMB / 2; thumb.y = LABEL_H + (ROW - THUMB) / 2;
  thumb.constraints = { horizontal: 'MIN', vertical: 'CENTER' };
  comp.appendChild(thumb);

  // Hidden hover/press colour layers for the thumb (same convention as the list
  // scrollbar's ThumbRollover/ThumbPressed) — Unity recolours per pointer state.
  const roll = solidRect('ThumbRollover', THUMB, THUMB, THUMB / 2, { r: 0.95, g: 0.94, b: 1 });
  roll.visible = false; roll.x = thumb.x; roll.y = thumb.y;
  roll.constraints = { horizontal: 'MIN', vertical: 'CENTER' };
  comp.appendChild(roll);
  const press = solidRect('ThumbPressed', THUMB, THUMB, THUMB / 2, { r: 0.88, g: 0.86, b: 1 });
  press.visible = false; press.x = thumb.x; press.y = thumb.y;
  press.constraints = { horizontal: 'MIN', vertical: 'CENTER' };
  comp.appendChild(press);

  // Click/drag surface — just the slider ROW (not the Label strip), so a click on
  // the label text doesn't jump the value.
  const hit = solidRect('HitArea', W, ROW, 0, { r: 0, g: 0, b: 0 }, 0);
  hit.x = 0; hit.y = LABEL_H; hit.constraints = { horizontal: 'STRETCH', vertical: 'STRETCH' };
  comp.appendChild(hit);

  const label = figma.createText();
  label.fontName = font; label.name = 'Label'; label.characters = 'Slider';
  label.fontSize = 13; label.fills = [{ type: 'SOLID', color: { r: 0.1, g: 0.1, b: 0.12 } }];
  comp.appendChild(label);
  label.x = 0; label.y = (LABEL_H - label.height) / 2;
  label.constraints = { horizontal: 'STRETCH', vertical: 'MIN' };

  // Live value read-out — a right-aligned text in the label strip. The shown text
  // is just the initial-value preview; Unity rewrites it on every value change.
  // Appended AFTER Label so the instance-label capture (first text) stays the Label.
  if (opts.value) {
    const val = figma.createText();
    val.fontName = font; val.name = 'Value';
    val.characters = fmtSliderNum(opts.min + (opts.max - opts.min) * ratio);
    val.fontSize = 13; val.fills = [{ type: 'SOLID', color: { r: 0.1, g: 0.1, b: 0.12 } }];
    val.textAlignHorizontal = 'RIGHT';
    comp.appendChild(val);
    val.x = W - val.width; val.y = (LABEL_H - val.height) / 2;
    val.constraints = { horizontal: 'MAX', vertical: 'MIN' };
  }

  // Tag carries the numeric behaviour: range + slot count (identity-level config,
  // like the ref) and the authored initial value (raw, within [min..max]).
  comp.setSharedPluginData('figforge', 'canonical', JSON.stringify({
    kind: 'slider', ref: name,
    value: fmtSliderNum(opts.min + (opts.max - opts.min) * ratio),
    minValue: opts.min, maxValue: opts.max,
    slots: opts.slots >= 2 ? opts.slots : undefined,
  }));
  parkMaster(comp);
  placeInstance(comp);
  return comp;
}

// Which Progress bar variant to create. Each combination is its own master/
// canonical ref ('Progress', 'Progress Ring Indet', 'Progress Seg10 V') — the
// Slider variants convention. Styles:
//   bar      — the classic horizontal rail (Track + Fill rects)
//   ring     — radial donut; the Fill is an arc sweeping clockwise from 12 o'clock
//   gauge    — a ring with a 270° span and the gap at the bottom (speedometer)
//   segments — N discrete blocks that light up left-to-right (battery/steps)
export type ProgressStyle = 'bar' | 'ring' | 'gauge' | 'segments';
export type ProgressOptions = {
  style: ProgressStyle;
  segments: number;       // segments style: block count (2..50)
  indeterminate: boolean; // animated (unknown duration): bar sweeps, ring/gauge spins, segments scan
  value: boolean;         // live percentage read-out (label strip for bar/segments, centered for ring/gauge)
};
const PROGRESS_DEFAULTS: ProgressOptions = { style: 'bar', segments: 10, indeterminate: false, value: false };

function normalizeProgressOptions(o: Partial<ProgressOptions> | undefined): ProgressOptions {
  const p = { ...PROGRESS_DEFAULTS, ...(o ?? {}) };
  if (!['bar', 'ring', 'gauge', 'segments'].includes(p.style)) p.style = 'bar';
  p.segments = Math.min(50, Math.max(2, Math.round(p.segments || 0) || 10));
  return p;
}

function progressVariantName(o: ProgressOptions): string {
  const p: string[] = [];
  if (o.style === 'ring') p.push('Ring');
  if (o.style === 'gauge') p.push('Gauge');
  if (o.style === 'segments') p.push('Seg' + o.segments);
  if (o.indeterminate) p.push('Indet');
  if (o.value) p.push('V');
  return 'Progress' + (p.length ? ' ' + p.join(' ') : '');
}

const PROGRESS_TRACK_RGB = { r: 0.85, g: 0.86, b: 0.9 };
const PROGRESS_FILL_RGB = { r: 0.49, g: 0.36, b: 1 };
const PROGRESS_TEXT_RGB = { r: 0.1, g: 0.1, b: 0.12 };

// An arc ellipse for ring/gauge layers. Figma arc angles are radians with 0 at
// 3 o'clock, clockwise positive (screen coords, y down).
function progressArc(name: string, d: number, startRad: number, sweepRad: number,
                     inner: number, color: RGB, alpha = 1): EllipseNode {
  const e = figma.createEllipse();
  e.name = name; e.resize(d, d);
  e.fills = [{ type: 'SOLID', color, opacity: alpha }];
  e.arcData = { startingAngle: startRad, endingAngle: startRad + sweepRad, innerRadius: inner };
  return e;
}

// Progress bar: a Slider with no Thumb and no input, in four shapes. The Track
// layer's TYPE picks the style at export: an arc ELLIPSE → ring/gauge (its
// arcData IS the geometry — re-sweep it to restyle), a frame of blocks →
// segments, plain rects → bar. The Fill layer carries the initial value: its
// width ÷ Track width (bar), its arc sweep ÷ the Track's sweep (ring/gauge), or
// its visible block count (segments — hide blocks to lower the default). Every
// variant: Label + optional live 'Value' percentage read-out. The Indeterminate
// variant adds a HIDDEN layer named 'Indeterminate': its PRESENCE flags animated
// mode (bar sweeps, ring/gauge spins, segments scan) — delete the layer to make
// the master determinate again, or add one to any progress master to animate it.
async function createProgress(optsIn?: Partial<ProgressOptions>): Promise<ComponentNode> {
  const opts = normalizeProgressOptions(optsIn);
  const name = progressVariantName(opts);
  const reuse = findMaster(name);
  if (reuse) { placeInstance(reuse); return reuse; }

  const font = await loadUiFont();
  const ratio = 0.5;
  const comp = figma.createComponent();
  comp.name = name; comp.fills = [];

  const ringLike = opts.style === 'ring' || opts.style === 'gauge';
  // Layout: bar/segments use the horizontal label-strip layout; ring/gauge are a
  // square dial with the label strip below (and the read-out centered inside).
  const W = ringLike ? 96 : 240;
  const LABEL_H = 20;
  const DIAL = 72, INNER = 0.78; // ring outer diameter / donut hole fraction
  const ROW = 16, TRACK = 8;
  const H = ringLike ? DIAL + 8 + LABEL_H : LABEL_H + ROW;
  comp.resize(W, H);
  const trackY = LABEL_H + (ROW - TRACK) / 2;

  if (opts.style === 'bar') {
    const track = solidRect('Track', W, TRACK, TRACK / 2, PROGRESS_TRACK_RGB);
    track.x = 0; track.y = trackY; track.constraints = { horizontal: 'STRETCH', vertical: 'CENTER' };
    comp.appendChild(track);

    const fill = solidRect('Fill', W * ratio, TRACK, TRACK / 2, PROGRESS_FILL_RGB);
    fill.x = 0; fill.y = trackY; fill.constraints = { horizontal: 'MIN', vertical: 'CENTER' };
    comp.appendChild(fill);

    if (opts.indeterminate) {
      const seg = solidRect('Indeterminate', W * 0.3, TRACK, TRACK / 2, PROGRESS_FILL_RGB, 0.5);
      seg.visible = false;
      seg.x = W * 0.35; seg.y = trackY;
      seg.constraints = { horizontal: 'SCALE', vertical: 'CENTER' };
      comp.appendChild(seg);
    }
  } else if (ringLike) {
    // Ring: full 360° from 12 o'clock (-π/2). Gauge: 270° with the gap centered
    // at the bottom — start at 7:30 (3π/4), sweep clockwise to 4:30.
    const start = opts.style === 'gauge' ? Math.PI * 0.75 : -Math.PI / 2;
    const span = opts.style === 'gauge' ? Math.PI * 1.5 : Math.PI * 2;
    const dialX = (W - DIAL) / 2, dialY = 0;

    const track = progressArc('Track', DIAL, start, span, INNER, PROGRESS_TRACK_RGB);
    track.x = dialX; track.y = dialY;
    track.constraints = { horizontal: 'SCALE', vertical: 'SCALE' };
    comp.appendChild(track);

    const fill = progressArc('Fill', DIAL, start, span * ratio, INNER, PROGRESS_FILL_RGB);
    fill.x = dialX; fill.y = dialY;
    fill.constraints = { horizontal: 'SCALE', vertical: 'SCALE' };
    comp.appendChild(fill);

    if (opts.indeterminate) {
      const seg = progressArc('Indeterminate', DIAL, start, span * 0.3, INNER, PROGRESS_FILL_RGB, 0.5);
      seg.visible = false;
      seg.x = dialX; seg.y = dialY;
      seg.constraints = { horizontal: 'SCALE', vertical: 'SCALE' };
      comp.appendChild(seg);
    }
  } else {
    // Segments: a Track frame of N grey blocks + a Fill frame holding the lit
    // overlay blocks (visible count = the initial value; hide blocks per
    // instance to lower it). Both frames span the row; blocks SCALE with width.
    const N = opts.segments, GAP = 4, SEG_H = 10;
    const segW = (W - GAP * (N - 1)) / N;
    const segY = LABEL_H + (ROW - SEG_H) / 2;
    const mkRow = (rowName: string, count: number, color: RGB) => {
      const f = figma.createFrame();
      f.name = rowName; f.fills = []; f.clipsContent = false;
      f.resize(W, SEG_H); f.x = 0; f.y = segY;
      f.constraints = { horizontal: 'STRETCH', vertical: 'CENTER' };
      for (let i = 0; i < count; i++) {
        const s = solidRect('Seg', segW, SEG_H, 3, color);
        s.x = i * (segW + GAP); s.y = 0;
        s.constraints = { horizontal: 'SCALE', vertical: 'STRETCH' };
        f.appendChild(s);
      }
      return f;
    };
    comp.appendChild(mkRow('Track', N, PROGRESS_TRACK_RGB));
    comp.appendChild(mkRow('Fill', Math.round(N * ratio), PROGRESS_FILL_RGB));

    if (opts.indeterminate) {
      const seg = solidRect('Indeterminate', segW, SEG_H, 3, PROGRESS_FILL_RGB, 0.5);
      seg.visible = false;
      seg.x = 0; seg.y = segY;
      seg.constraints = { horizontal: 'SCALE', vertical: 'CENTER' };
      comp.appendChild(seg);
    }
  }

  const label = figma.createText();
  label.fontName = font; label.name = 'Label'; label.characters = 'Progress';
  label.fontSize = 13; label.fills = [{ type: 'SOLID', color: PROGRESS_TEXT_RGB }];
  if (ringLike) label.textAlignHorizontal = 'CENTER';
  comp.appendChild(label);
  if (ringLike) {
    label.resize(W, label.height);
    label.x = 0; label.y = H - LABEL_H + (LABEL_H - label.height) / 2;
    label.constraints = { horizontal: 'STRETCH', vertical: 'MAX' };
  } else {
    label.x = 0; label.y = (LABEL_H - label.height) / 2;
    label.constraints = { horizontal: 'STRETCH', vertical: 'MIN' };
  }

  // Live percentage read-out — the initial-value preview; Unity rewrites it on
  // every value change. Bar/segments: right-aligned in the label strip.
  // Ring/gauge: centered inside the dial. Appended AFTER Label so the
  // instance-label capture (first text) stays the Label.
  if (opts.value) {
    const val = figma.createText();
    val.fontName = font; val.name = 'Value';
    val.characters = `${Math.round(ratio * 100)}%`;
    val.fills = [{ type: 'SOLID', color: PROGRESS_TEXT_RGB }];
    comp.appendChild(val);
    if (ringLike) {
      val.fontSize = 14;
      val.textAlignHorizontal = 'CENTER';
      val.resize(DIAL * INNER, val.height);
      val.x = (W - DIAL * INNER) / 2; val.y = DIAL / 2 - val.height / 2;
      val.constraints = { horizontal: 'SCALE', vertical: 'SCALE' };
    } else {
      val.fontSize = 13;
      val.textAlignHorizontal = 'RIGHT';
      val.x = W - val.width; val.y = (LABEL_H - val.height) / 2;
      val.constraints = { horizontal: 'MAX', vertical: 'MIN' };
    }
  }

  comp.setSharedPluginData('figforge', 'canonical', JSON.stringify({
    kind: 'progress', ref: name,
    value: fmtSliderNum(ratio),
    style: opts.style === 'gauge' ? 'ring' : opts.style, // gauge IS a ring with a 270° arc
    indeterminate: opts.indeterminate || undefined,
  }));
  parkMaster(comp);
  placeInstance(comp);
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
  // Extra right padding keeps the trailing Accessory chevron 10px off the edge.
  content.paddingLeft = PAD; content.paddingRight = PAD + 10;
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
// Bring EVERY list master in the document up to the current conventions — so one
// +List click repairs all variants, not just the one being (re)created. Repairs:
// missing Scrollbar/Mask layers, the interim radius-0 Mask, the pristine old 6px
// default scrollbar, the old-default 1px Background border, and the rounded
// component clip. Designer customisations are never touched (repairs match exact
// old defaults only).
function upgradeAllListMasters(): void {
  for (const page of figma.root.children) {
    for (const n of page.children) {
      if (n.type !== 'COMPONENT') continue;
      // ListItem masters carry no canonical tag — match by name.
      if (n.name === 'ListItem' || n.name.startsWith('ListItem ')) {
        repairListItemChevronPadding(n as ComponentNode);
        continue;
      }
      const tag = (n as ComponentNode).getSharedPluginData('figforge', 'canonical');
      if (!tag) continue;
      // Tables share the List's scroll plumbing (Scrollbar/Mask/rounded clip), so the
      // same repairs keep both kinds of master up to the current conventions.
      try { const k = (JSON.parse(tag) as { kind?: string }).kind; if (k !== 'list' && k !== 'table') continue; } catch { continue; }
      const comp = n as ComponentNode;
      repairOldDefaultScrollbar(comp);
      repairDefaultBackgroundStroke(comp);
      ensureListScrollbar(comp);
      ensureListRoundedClip(comp);
      ensureListMask(comp);
    }
  }
}

// A Background still wearing the PRISTINE old-default border (1px solid #D1D4E0)
// predates the borderless default — strip it (it read as a mystery grey outline
// around the panel in Unity). Any other stroke is a designer's border: left alone.
function repairDefaultBackgroundStroke(comp: ComponentNode): void {
  if (!('children' in comp)) return;
  const bg = (comp as ChildrenMixin).children.find((c) => c.name === 'Background');
  if (!bg || !('strokes' in bg)) return;
  const g = bg as unknown as { strokes: readonly Paint[]; strokeWeight: number | symbol };
  if (g.strokeWeight !== 1 || g.strokes.length !== 1) return;
  const s = g.strokes[0];
  if (s.type !== 'SOLID' || s.visible === false) return;
  const near = (a: number, b: number) => Math.abs(a - b) < 0.005;
  if (!near(s.color.r, 0.82) || !near(s.color.g, 0.83) || !near(s.color.b, 0.88)) return;
  (bg as unknown as { strokes: Paint[] }).strokes = [];
}

// Pristine old-default rows had the Accessory chevron flush at paddingRight 14 —
// nudge it 10px in. Only the exact old default is touched.
function repairListItemChevronPadding(item: ComponentNode): void {
  if (!('children' in item)) return;
  const content = (item as ChildrenMixin).children.find((c) => c.name === 'Content');
  if (!content || !('paddingRight' in content)) return;
  const f = content as FrameNode;
  if (f.paddingRight === 14) f.paddingRight = 24;
}

// A Scrollbar frame still at the PRISTINE old default (6px wide, 3px thumb radius)
// predates the thicker default — rebuild it at the current default. Any other size
// or radius means the designer touched it: leave it alone.
function repairOldDefaultScrollbar(comp: ComponentNode): void {
  if (!('children' in comp)) return;
  const sb = (comp as ChildrenMixin).children.find((c) => c.name === 'Scrollbar');
  if (!sb || Math.round((sb as unknown as { width: number }).width) !== 6) return;
  const thumb = 'children' in sb
    ? ((sb as ChildrenMixin).children as SceneNode[]).find((c) => c.name === 'Thumb') : undefined;
  const tr = thumb && typeof (thumb as unknown as { cornerRadius?: unknown }).cornerRadius === 'number'
    ? (thumb as unknown as { cornerRadius: number }).cornerRadius : -1;
  if (tr !== 3) return;
  sb.remove(); // ensureListScrollbar recreates it at the current default
}

async function createList(opts: ListOptions = LIST_DEFAULTS): Promise<ComponentNode> {
  upgradeAllListMasters();
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

  // Borderless: the panel reads as a card via its drop shadow alone — a 1px grey
  // stroke here looked like a mystery outline in Unity (designers add one back in
  // Figma if they want a bordered list).
  const bg = solidRect('Background', W, H, R, { r: 1, g: 1, b: 1 });
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
  const W = comp.width, H = comp.height;
  const header = kids.find((c) => c.name === 'Header');
  const HEADER = header ? (header as unknown as { height: number }).height : 0;
  const bg = kids.find((c) => c.name === 'Background');
  const strokeW = bg && typeof (bg as unknown as { strokeWeight?: unknown }).strokeWeight === 'number'
    ? (bg as unknown as { strokeWeight: number }).strokeWeight : 0;
  const bgRadius = bg && typeof (bg as unknown as { cornerRadius?: unknown }).cornerRadius === 'number'
    ? (bg as unknown as { cornerRadius: number }).cornerRadius : 0;
  const inset = strokeW + 1;

  const existing = kids.find((c) => c.name === 'Mask');
  if (existing) {
    // Repair masks created by the interim retrofit, which defaulted to radius 0 —
    // a square clip lets row fills poke past the rounded Background at the corners.
    // Only a radius of EXACTLY 0 is touched; any designer-set rounding is kept.
    const er = typeof (existing as unknown as { cornerRadius?: unknown }).cornerRadius === 'number'
      ? (existing as unknown as { cornerRadius: number }).cornerRadius : -1;
    if (er === 0 && bgRadius > 0)
      (existing as unknown as { cornerRadius: number }).cornerRadius = Math.max(0, bgRadius - inset);
    return;
  }

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

// ---------------------------------------------------------------------------
// Canonical Table — an n×m grid: an optional pinned Header of column titles +
// n TableRow instances of m equal columns, on the List's scroll plumbing
// (Scrollbar, Mask, rounded clip — the same ensure* helpers repair both kinds).
// ---------------------------------------------------------------------------
export type TableOptions = { rows: number; cols: number; header: boolean; scrollbarWidth: number };
const TABLE_DEFAULTS: TableOptions = { rows: 4, cols: 3, header: true, scrollbarWidth: DEFAULT_SCROLLBAR_WIDTH };
const TABLE_PAD = 14;

function clampTableDim(v: unknown, def: number, max: number): number {
  const n = typeof v === 'number' && isFinite(v) ? Math.round(v) : def;
  return Math.min(max, Math.max(1, n));
}

// Each distinct configuration is its own master ('Table', 'Table 6x4', 'Table
// 6x4 NoHeader SB14', …) — the slider-variant convention.
function tableVariantName(o: TableOptions): string {
  const p: string[] = [];
  if (o.rows !== TABLE_DEFAULTS.rows || o.cols !== TABLE_DEFAULTS.cols) p.push(`${o.rows}x${o.cols}`);
  if (!o.header) p.push('NoHeader');
  const sb = clampScrollbarWidth(o.scrollbarWidth);
  if (sb !== DEFAULT_SCROLLBAR_WIDTH) p.push('SB' + sb);
  return 'Table' + (p.length ? ' ' + p.join(' ') : '');
}

// The row master only varies by column count — tables with the same m share it.
function tableRowVariantName(cols: number): string {
  return cols === TABLE_DEFAULTS.cols ? 'TableRow' : `TableRow C${cols}`;
}

// A horizontal auto-layout strip of m text cells named Cell1..CellM (no spaces:
// the exporter sanitizes subtree names to lowercase and Unity binds row data by
// these names). Every cell has layoutGrow=1 so columns share the width equally
// and reflow when the strip stretches; grow/shrink a cell in Figma to resize a
// column — rows and header reflow alike since both use this strip.
function tableCellStrip(W: number, H: number, cols: number, font: FontName,
                        textFor: (c: number) => string, fontSize: number, color: RGB): FrameNode {
  const strip = figma.createFrame();
  strip.name = 'Content'; strip.fills = []; strip.clipsContent = false;
  strip.x = 0; strip.y = 0; strip.resize(W, H);
  strip.layoutMode = 'HORIZONTAL';
  strip.primaryAxisSizingMode = 'FIXED';
  strip.counterAxisSizingMode = 'FIXED';
  strip.primaryAxisAlignItems = 'MIN';
  strip.counterAxisAlignItems = 'CENTER';
  strip.itemSpacing = 12;
  strip.paddingLeft = TABLE_PAD; strip.paddingRight = TABLE_PAD;
  strip.constraints = { horizontal: 'STRETCH', vertical: 'STRETCH' };
  for (let c = 1; c <= cols; c++) {
    const cell = figma.createText();
    cell.fontName = font; cell.name = 'Cell' + c; cell.characters = textFor(c);
    cell.fontSize = fontSize; cell.fills = [{ type: 'SOLID', color }];
    strip.appendChild(cell);
    cell.textAutoResize = 'HEIGHT';
    cell.layoutGrow = 1;
  }
  return strip;
}

async function ensureTableRow(font: FontName, W: number, ROW: number, cols: number): Promise<ComponentNode> {
  const rowName = tableRowVariantName(cols);
  const found = findMaster(rowName);
  if (found) return found;
  const item = figma.createComponent();
  item.name = rowName; item.resize(W, ROW); item.fills = []; item.clipsContent = true;

  // Pointer/selection state backgrounds (full-bleed) — the ListItem convention:
  // only Regular shows; Unity swaps these on hover/press/selection.
  const reg = solidRect('Regular', W, ROW, 0, { r: 1, g: 1, b: 1 });
  reg.constraints = { horizontal: 'STRETCH', vertical: 'STRETCH' }; item.appendChild(reg);
  const roll = solidRect('Rollover', W, ROW, 0, { r: 0.96, g: 0.96, b: 1 });
  roll.visible = false; roll.constraints = { horizontal: 'STRETCH', vertical: 'STRETCH' }; item.appendChild(roll);
  const press = solidRect('Pressed', W, ROW, 0, { r: 0.92, g: 0.92, b: 1 });
  press.visible = false; press.constraints = { horizontal: 'STRETCH', vertical: 'STRETCH' }; item.appendChild(press);
  const sel = solidRect('Selected', W, ROW, 0, { r: 0.90, g: 0.91, b: 1 });
  sel.visible = false; sel.constraints = { horizontal: 'STRETCH', vertical: 'STRETCH' }; item.appendChild(sel);

  const strip = tableCellStrip(W, ROW, cols, font, (c) => `Cell ${c}`, 13, { r: 0.1, g: 0.1, b: 0.12 });
  item.appendChild(strip);

  // Bottom divider separator between rows.
  const div = solidRect('Divider', W - TABLE_PAD * 2, 1, 0, { r: 0.9, g: 0.9, b: 0.93 });
  div.x = TABLE_PAD; div.y = ROW - 1; div.constraints = { horizontal: 'STRETCH', vertical: 'MAX' }; item.appendChild(div);

  // Transparent full-bleed hit target — last so it sits on top.
  const hit = solidRect('HitArea', W, ROW, 0, { r: 0, g: 0, b: 0 }, 0);
  hit.constraints = { horizontal: 'STRETCH', vertical: 'STRETCH' }; item.appendChild(hit);

  parkMaster(item);
  return item;
}

// Table: rounded clipped Background + optional pinned Header (column titles over a
// tinted band) + n TableRow instances. The rows you see in Figma ARE the exported
// cell data — edit a cell text on a placed Row to set that cell. In Unity the rows
// scroll under the pinned header and the scrollbar appears only when they overflow
// the instance's height (resize the instance to set the visible row count).
async function createTable(opts: TableOptions = TABLE_DEFAULTS): Promise<ComponentNode> {
  upgradeAllListMasters();
  const rows = clampTableDim(opts.rows, TABLE_DEFAULTS.rows, 100);
  const cols = clampTableDim(opts.cols, TABLE_DEFAULTS.cols, 12);
  const sbWidth = clampScrollbarWidth(opts.scrollbarWidth);
  const name = tableVariantName({ ...opts, rows, cols, scrollbarWidth: sbWidth });
  const reuse = findMaster(name);
  if (reuse) { ensureListScrollbar(reuse, sbWidth); ensureListRoundedClip(reuse); ensureListMask(reuse); placeInstance(reuse); return reuse; }

  const font = await loadUiFont();
  const titleFont = await loadBoldFont(font);
  const W = Math.max(320, cols * 120), ROW = 40, HEADER = opts.header ? 40 : 0, H = HEADER + ROW * rows, R = 14;
  const rowComp = await ensureTableRow(font, W, ROW, cols);

  const comp = figma.createComponent();
  comp.name = name; comp.resize(W, H); comp.fills = []; comp.clipsContent = true;

  // Borderless, like the List — the drop shadow defines the card edge.
  const bg = solidRect('Background', W, H, R, { r: 1, g: 1, b: 1 });
  bg.x = 0; bg.y = 0; bg.constraints = { horizontal: 'STRETCH', vertical: 'STRETCH' };
  comp.appendChild(bg);

  // Header — column titles pinned above the scrolling rows in Unity. Its cell strip
  // mirrors the row geometry exactly, so titles align with their columns at any
  // width. The band's top corners follow the Background radius (the header sits
  // OUTSIDE Unity's scroll mask, so a square band would poke past the rounding).
  if (opts.header) {
    const header = figma.createFrame();
    header.name = 'Header'; header.resize(W, HEADER); header.fills = []; header.x = 0; header.y = 0;
    header.constraints = { horizontal: 'STRETCH', vertical: 'MIN' };
    comp.appendChild(header);
    const hbg = solidRect('HeaderBg', W, HEADER, 0, { r: 0.97, g: 0.97, b: 0.99 });
    hbg.topLeftRadius = R; hbg.topRightRadius = R;
    hbg.constraints = { horizontal: 'STRETCH', vertical: 'STRETCH' };
    header.appendChild(hbg);
    header.appendChild(tableCellStrip(W, HEADER, cols, titleFont, (c) => `Column ${c}`, 12, { r: 0.45, g: 0.45, b: 0.5 }));
    const hdiv = solidRect('Divider', W, 1, 0, { r: 0.88, g: 0.88, b: 0.92 });
    hdiv.x = 0; hdiv.y = HEADER - 1; hdiv.constraints = { horizontal: 'STRETCH', vertical: 'MAX' };
    header.appendChild(hdiv);
  }

  for (let r = 0; r < rows; r++) {
    const ins = rowComp.createInstance();
    ins.name = 'Row'; ins.resize(W, ROW); ins.x = 0; ins.y = HEADER + r * ROW;
    ins.constraints = { horizontal: 'STRETCH', vertical: 'MIN' };
    comp.appendChild(ins);
    for (let c = 1; c <= cols; c++) {
      const cell = ins.findOne((n) => n.type === 'TEXT' && n.name === 'Cell' + c) as TextNode | null;
      if (cell) cell.characters = c === 1 ? `Item ${r + 1}` : `R${r + 1}C${c}`;
    }
    if (r === 0) {
      const selected = ins.findOne((n) => n.name === 'Selected');
      if (selected) (selected as unknown as { visible: boolean }).visible = true;
    }
  }

  ensureListScrollbar(comp, sbWidth);
  ensureListRoundedClip(comp);
  ensureListMask(comp);
  comp.setSharedPluginData('figforge', 'canonical', JSON.stringify({ kind: 'table', ref: name }));
  parkMaster(comp);
  placeInstance(comp);
  return comp;
}
