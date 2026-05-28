// =============================================================================
// FigForge — plugin main thread (Figma sandbox)
//
// Owns the document: builds the layer tree for the UI, runs exports, and serves
// MCP requests forwarded by the UI from the bridge server.
// =============================================================================

import {
  DEFAULT_EXPORT_OPTIONS,
  DEFAULT_EXPORT_SCALE,
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
      const frames = figma.currentPage.children.filter(
        (n) => ['FRAME', 'COMPONENT'].includes(n.type) && (n as SceneNode).visible !== false
      ) as SceneNode[];
      if (frames.length === 0) {
        figma.ui.postMessage({ type: 'export-error', message: 'No top-level frames on this page.' });
        break;
      }
      try {
        const scale = (msg.scale as ExportScale) || DEFAULT_EXPORT_SCALE;
        const options = (msg.options as ExportOptions) || DEFAULT_EXPORT_OPTIONS;
        const screens: { name: string; manifest: string; assets: { name: string; data: number[] }[] }[] = [];
        for (let i = 0; i < frames.length; i++) {
          figma.ui.postMessage({ type: 'progress', current: i, total: frames.length, label: frames[i].name });
          const result = await exportDesign(frames[i], scale, options);
          screens.push({
            name: sanitize(frames[i].name),
            manifest: JSON.stringify(result.manifest, null, 2),
            assets: result.assets,
          });
        }
        figma.ui.postMessage({
          type: 'export-page-complete',
          project: { name: figma.currentPage.name, initial: screens[0] ? screens[0].name : '' },
          screens,
        });
      } catch (e) {
        figma.ui.postMessage({ type: 'export-error', message: String((e as Error)?.message || e) });
      }
      break;
    }

    case 'create-button': {
      try {
        const comp = await createCanonicalButton();
        figma.ui.postMessage({
          type: 'status',
          message: `Button "${comp.name}" ready (Regular/Rollover/Pressed/HitArea/Label) — skin it, then drop instances into your screens.`,
        });
      } catch (e) {
        figma.ui.postMessage({ type: 'export-error', message: 'Create button failed: ' + String((e as Error)?.message || e) });
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

async function createCanonicalButton(): Promise<ComponentNode> {
  let page = figma.root.children.find((p) => p.name === COMPONENTS_PAGE) as PageNode | undefined;
  if (!page) {
    page = figma.createPage();
    page.name = COMPONENTS_PAGE;
  }

  // Reuse the default "Button" master if it already exists — don't spam Button2/3.
  const existing = page.children.find(
    (n) => n.type === 'COMPONENT' && n.name === 'Button'
  ) as ComponentNode | undefined;
  if (existing) {
    jumpTo(page, existing);
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
  jumpTo(page, comp);
  return comp;
}
