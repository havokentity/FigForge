// =============================================================================
// MCP tool registration. Each tool forwards to the Figma plugin via the
// PluginSender (leader bridge or follower RPC) and returns text content.
// =============================================================================

import path from 'node:path';
import { mkdir, writeFile } from 'node:fs/promises';
import type { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import type { PluginSender, RpcResponse, ToolResult } from './types.js';
import {
  designContextInput,
  exportUnityInput,
  figmaNodeId,
  getNodeInput,
  saveScreenshotsInput,
  screenshotInput,
} from './schema.js';
import { z } from 'zod';

function ok(data: unknown): ToolResult {
  return { content: [{ type: 'text', text: JSON.stringify(data, null, 2) }] };
}
function fail(message: string): ToolResult {
  return { content: [{ type: 'text', text: message }], isError: true };
}
function unwrap(r: RpcResponse): ToolResult {
  return r.error ? fail(r.error) : ok(r.data);
}

/** Resolve `outDir` against the bridge cwd and refuse paths that escape it. */
export function resolveAndValidateOutputPath(outDir: string, workspaceRoot: string): string {
  const resolved = path.resolve(workspaceRoot, outDir);
  const root = path.resolve(workspaceRoot);
  if (resolved !== root && !resolved.startsWith(root + path.sep)) {
    throw new Error(`Refusing to write outside the bridge working directory: ${outDir}`);
  }
  return resolved;
}

interface UnityExport {
  manifest?: { screen?: { name?: string }; elements?: unknown[] };
  assets?: Array<{ name?: unknown; data?: unknown }>;
}

/** Asset/screenshot bytes off the plugin wire. v2.0 plugins send base64
 * strings (~1.33× binary size); v1.0 plugins sent number[] (~3.7× as decimal
 * JSON text). Accept both so an older plugin keeps working against this
 * bridge during the transition. */
function wireBytesToBuffer(data: unknown): Buffer | null {
  if (typeof data === 'string') return Buffer.from(data, 'base64');
  if (Array.isArray(data)) return Buffer.from(data as number[]);
  return null;
}

export async function executeExportUnity(
  sender: PluginSender,
  nodeId: string,
  outDir: string,
  workspaceRoot: string
): Promise<{ outDir: string; screenName?: string; manifestPath: string; assetCount: number; elementCount: number }> {
  const resolvedDir = resolveAndValidateOutputPath(outDir, workspaceRoot);
  const resp = await sender.send('export_unity', [nodeId]);
  if (resp.error) throw new Error(resp.error);

  const data = resp.data as { exports?: UnityExport[] } | undefined;
  const exp = data?.exports?.[0];
  if (!exp || !exp.manifest) throw new Error('No Unity export returned by the plugin.');

  await mkdir(resolvedDir, { recursive: true });
  const manifestPath = path.join(resolvedDir, 'manifest.json');
  await writeFile(manifestPath, JSON.stringify(exp.manifest, null, 2), 'utf8');

  let written = 0;
  for (const asset of Array.isArray(exp.assets) ? exp.assets : []) {
    if (!asset || typeof asset.name !== 'string') continue;
    const bytes = wireBytesToBuffer(asset.data);
    if (!bytes) continue;
    // Flatten to the bare file name so a "../"-bearing asset name can't escape
    // the validated output dir (path.basename strips all directory components).
    const safeName = path.basename(asset.name);
    if (!safeName || safeName === '.' || safeName === '..') continue;
    await writeFile(path.join(resolvedDir, safeName), bytes);
    written++;
  }

  return {
    outDir: resolvedDir,
    screenName: exp.manifest.screen?.name,
    manifestPath,
    assetCount: written,
    elementCount: Array.isArray(exp.manifest.elements) ? exp.manifest.elements.length : 0,
  };
}

export function registerTools(server: McpServer, sender: PluginSender, workspaceRoot: string): void {
  server.tool('get_metadata', 'Get the Figma file name, pages, and current page.', {}, async () =>
    unwrap(await sender.send('get_metadata'))
  );

  server.tool('get_document', 'Get the current page document tree (depth 2).', {}, async () =>
    unwrap(await sender.send('get_document'))
  );

  server.tool('get_selection', 'Get the currently selected nodes.', {}, async () =>
    unwrap(await sender.send('get_selection'))
  );

  server.tool('get_node', 'Get a specific node by id (deep).', getNodeInput, async ({ nodeId }) =>
    unwrap(await sender.send('get_node', [nodeId], { nodeId }))
  );

  server.tool(
    'get_design_context',
    'Get a summarized design tree of the current page.',
    designContextInput,
    async ({ depth }) => unwrap(await sender.send('get_design_context', undefined, { depth }))
  );

  server.tool(
    'get_screenshot',
    'Render node(s) to PNG and return them base64-encoded.',
    screenshotInput,
    async ({ nodeIds, scale }) => {
      const r = await sender.send('get_screenshot', nodeIds, { scale });
      if (r.error) return fail(r.error);
      const shots = (r.data as { screenshots?: { nodeId: string; data: string | number[] }[] })?.screenshots || [];
      return ok(
        shots
          .map((s) => ({ nodeId: s.nodeId, bytes: wireBytesToBuffer(s.data) }))
          .filter((s) => s.bytes)
          .map((s) => ({ nodeId: s.nodeId, base64: (s.bytes as Buffer).toString('base64') }))
      );
    }
  );

  server.tool(
    'save_screenshots',
    'Render node(s) and write each PNG to disk (paths relative to the bridge cwd).',
    saveScreenshotsInput,
    async ({ items, scale }) => {
      const ids = items.map((i) => i.nodeId);
      const r = await sender.send('get_screenshot', ids, { scale });
      if (r.error) return fail(r.error);
      const shots = (r.data as { screenshots?: { nodeId: string; data: string | number[] }[] })?.screenshots || [];
      const byId = new Map(shots.map((s) => [s.nodeId, s.data]));
      const written: string[] = [];
      for (const item of items) {
        const bytes = wireBytesToBuffer(byId.get(item.nodeId));
        if (!bytes) continue;
        const resolved = resolveAndValidateOutputPath(item.outputPath, workspaceRoot);
        await mkdir(path.dirname(resolved), { recursive: true });
        await writeFile(resolved, bytes);
        written.push(resolved);
      }
      return ok({ written });
    }
  );

  server.tool(
    'export_unity',
    'Export a Figma frame as a FigForge Unity package (manifest.json + PNG assets) into a folder on disk, using the plugin exporter. Params: { nodeId, outDir }.',
    exportUnityInput,
    async ({ nodeId, outDir }) => {
      try {
        return ok(await executeExportUnity(sender, nodeId, outDir, workspaceRoot));
      } catch (e) {
        return fail(e instanceof Error ? e.message : String(e));
      }
    }
  );
}
