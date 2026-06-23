// =============================================================================
// MCP tool registration. Each tool forwards to the Figma plugin via the
// PluginSender (leader bridge or follower RPC) and returns text content.
// =============================================================================

import path from 'node:path';
import { realpathSync } from 'node:fs';
import { mkdir, writeFile } from 'node:fs/promises';
import type { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import type { PluginSender, RpcResponse, ToolResult } from './types.js';
import {
  createCanonicalInput,
  createShellInput,
  designContextInput,
  exportProjectUnityInput,
  exportUnityInput,
  getNodeInput,
  listFramesInput,
  nodeDetailsInput,
  saveScreenshotsInput,
  screenshotInput,
  validateManifestContractInput,
} from './schema.js';
import { EXPORT_TIMEOUT_MS } from './version.js';

function ok(data: unknown): ToolResult {
  return { content: [{ type: 'text', text: JSON.stringify(data, null, 2) }] };
}
function fail(message: string): ToolResult {
  return { content: [{ type: 'text', text: message }], isError: true };
}
function unwrap(r: RpcResponse): ToolResult {
  return r.error ? fail(r.error) : ok(r.data);
}

/** Canonicalize `p` by realpath-ing its deepest EXISTING ancestor (resolving
 * any symlinks along the way) and re-appending the non-existing tail. A plain
 * realpathSync would throw on a not-yet-created output dir, so we walk up until
 * a real dir is found, canonicalize that, then rejoin the remainder. */
function canonicalizeExisting(p: string): string {
  let existing = p;
  const tail: string[] = [];
  // Walk up until realpathSync resolves (the deepest ancestor that exists).
  for (;;) {
    try {
      const real = realpathSync(existing);
      return tail.length ? path.join(real, ...tail) : real;
    } catch {
      const parent = path.dirname(existing);
      // Reached the filesystem root without finding an existing ancestor:
      // nothing to canonicalize, fall back to the lexically resolved path.
      if (parent === existing) return p;
      tail.unshift(path.basename(existing));
      existing = parent;
    }
  }
}

/** Resolve `outDir` against the bridge cwd and refuse paths that escape it.
 * Symlinks are resolved before the containment check so a symlinked component
 * inside the workspace cannot redirect a write outside it. */
export function resolveAndValidateOutputPath(outDir: string, workspaceRoot: string): string {
  const resolved = path.resolve(workspaceRoot, outDir);
  const root = path.resolve(workspaceRoot);
  const realResolved = canonicalizeExisting(resolved);
  const realRoot = canonicalizeExisting(root);
  if (realResolved !== realRoot && !realResolved.startsWith(realRoot + path.sep)) {
    throw new Error(`Refusing to write outside the bridge working directory: ${outDir}`);
  }
  // Return the canonicalized path that was actually validated (not the raw
  // `resolved`), so the bytes land at the location whose containment we checked
  // — a symlinked ancestor can't redirect the write between check and use.
  return realResolved;
}

interface UnityExport {
  manifest?: { screen?: { name?: string }; elements?: unknown[] };
  assets?: Array<{ name?: unknown; data?: unknown }>;
}

interface ProjectExportScreen {
  name?: string;
  manifest?: { screen?: { name?: string }; elements?: unknown[] };
  assets?: Array<{ name?: unknown; data?: unknown }>;
  section?: string;
  role?: string;
}

interface ProjectExport {
  project?: {
    name?: string;
    initial?: string;
  };
  screens?: ProjectExportScreen[];
}

interface ExportUnityParams {
  scale?: unknown;
  options?: unknown;
  elementConfigs?: unknown;
}

function safeFolderName(raw: unknown, fallback: string): string {
  const base = typeof raw === 'string' && raw.trim() ? raw : fallback;
  const safe = base
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '_')
    .replace(/_+/g, '_')
    .replace(/^_+|_+$/g, '');
  return safe || fallback;
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
  workspaceRoot: string,
  params: ExportUnityParams = {}
): Promise<{ outDir: string; screenName?: string; manifestPath: string; assetCount: number; elementCount: number }> {
  const resolvedDir = resolveAndValidateOutputPath(outDir, workspaceRoot);
  const resp = await sender.send('export_unity', [nodeId], params as Record<string, unknown>, EXPORT_TIMEOUT_MS);
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

export async function executeExportProjectUnity(
  sender: PluginSender,
  outDir: string,
  workspaceRoot: string,
  params: ExportUnityParams = {}
): Promise<{
  outDir: string;
  projectPath: string;
  projectName?: string;
  initial?: string;
  screenCount: number;
  assetCount: number;
  screens: Array<{ name?: string; role: string; section: string; manifestPath: string; assetCount: number }>;
}> {
  const resolvedDir = resolveAndValidateOutputPath(outDir, workspaceRoot);
  const resp = await sender.send('export_project_unity', undefined, params as Record<string, unknown>, EXPORT_TIMEOUT_MS);
  if (resp.error) throw new Error(resp.error);

  const data = resp.data as ProjectExport | undefined;
  const screens = Array.isArray(data?.screens) ? data.screens : [];
  if (screens.length === 0) throw new Error('No project screens returned by the plugin.');

  await mkdir(resolvedDir, { recursive: true });
  const used = new Set<string>();
  const indexScreens: Array<{ name: string; manifest: string; section: string; role: string }> = [];
  const writtenScreens: Array<{ name?: string; role: string; section: string; manifestPath: string; assetCount: number }> = [];
  let totalAssets = 0;

  for (let i = 0; i < screens.length; i++) {
    const screen = screens[i];
    if (!screen?.manifest) continue;
    const base = safeFolderName(screen.name || screen.manifest.screen?.name, `screen_${i + 1}`);
    let folder = base;
    let n = 1;
    while (used.has(folder)) folder = `${base}_${n++}`;
    used.add(folder);

    const screenDir = path.join(resolvedDir, folder);
    await mkdir(screenDir, { recursive: true });
    const manifestPath = path.join(screenDir, 'manifest.json');
    await writeFile(manifestPath, JSON.stringify(screen.manifest, null, 2), 'utf8');

    let written = 0;
    for (const asset of Array.isArray(screen.assets) ? screen.assets : []) {
      if (!asset || typeof asset.name !== 'string') continue;
      const bytes = wireBytesToBuffer(asset.data);
      if (!bytes) continue;
      const safeName = path.basename(asset.name);
      if (!safeName || safeName === '.' || safeName === '..') continue;
      await writeFile(path.join(screenDir, safeName), bytes);
      written++;
    }
    totalAssets += written;

    const name = screen.name || screen.manifest.screen?.name || folder;
    const role = typeof screen.role === 'string' && screen.role ? screen.role : 'screen';
    const section = typeof screen.section === 'string' ? screen.section : '';
    indexScreens.push({ name, manifest: `${folder}/manifest.json`, section, role });
    writtenScreens.push({ name, role, section, manifestPath, assetCount: written });
  }

  if (indexScreens.length === 0) throw new Error('No valid screen manifests returned by the plugin.');

  const project = {
    schema: 'figforge/project',
    version: '2.0',
    generator: 'FigForge',
    name: data?.project?.name || 'FigForge Project',
    exportedAt: new Date().toISOString(),
    initial: data?.project?.initial || indexScreens.find((s) => s.role !== 'shell')?.name || indexScreens[0].name,
    screens: indexScreens,
  };
  const projectPath = path.join(resolvedDir, 'project.json');
  await writeFile(projectPath, JSON.stringify(project, null, 2), 'utf8');

  return {
    outDir: resolvedDir,
    projectPath,
    projectName: project.name,
    initial: project.initial,
    screenCount: indexScreens.length,
    assetCount: totalAssets,
    screens: writtenScreens,
  };
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return !!value && typeof value === 'object' && !Array.isArray(value);
}

function readJsonObject(json: string | undefined, label: string, errors: string[]): unknown {
  if (!json) return undefined;
  try {
    return JSON.parse(json);
  } catch (e) {
    errors.push(`${label} is not valid JSON: ${e instanceof Error ? e.message : String(e)}`);
    return undefined;
  }
}

function hasNumberRecord(value: unknown, keys: string[]): boolean {
  return isRecord(value) && keys.every((k) => typeof value[k] === 'number' && Number.isFinite(value[k]));
}

function numberArray(value: unknown, length: number): boolean {
  return Array.isArray(value) && value.length === length && value.every((v) => typeof v === 'number' && Number.isFinite(v));
}

const diagnosticCategories = new Set(['missingFonts', 'unsupportedFills', 'rasterFallbacks', 'oversizedPngs', 'blendModeCaveats', 'variantExtraction']);
const diagnosticSeverities = new Set(['info', 'warning']);

export function validateManifest(manifest: unknown, prefix = 'manifest'): { errors: string[]; warnings: string[]; summary: Record<string, unknown> } {
  const errors: string[] = [];
  const warnings: string[] = [];
  const summary: Record<string, unknown> = {};
  if (!isRecord(manifest)) {
    return { errors: [`${prefix} must be an object.`], warnings, summary };
  }

  if (manifest.schema !== 'figforge/manifest') errors.push(`${prefix}.schema must be "figforge/manifest".`);
  if (typeof manifest.version !== 'string') errors.push(`${prefix}.version must be a string.`);
  if (manifest.generator !== 'FigForge') errors.push(`${prefix}.generator must be "FigForge".`);
  if (typeof manifest.exportedAt !== 'string') warnings.push(`${prefix}.exportedAt should be an ISO timestamp string.`);

  const screen = manifest.screen;
  if (!isRecord(screen)) {
    errors.push(`${prefix}.screen must be an object.`);
  } else {
    for (const key of ['id', 'name', 'displayName']) {
      if (typeof screen[key] !== 'string') errors.push(`${prefix}.screen.${key} must be a string.`);
    }
    if (!hasNumberRecord(screen.figmaSize, ['w', 'h'])) errors.push(`${prefix}.screen.figmaSize must have numeric w/h.`);
    if (!hasNumberRecord(screen.referenceResolution, ['w', 'h'])) errors.push(`${prefix}.screen.referenceResolution must have numeric w/h.`);
    if (typeof screen.exportScale !== 'number') errors.push(`${prefix}.screen.exportScale must be a number.`);
  }

  const assets = Array.isArray(manifest.assets) ? manifest.assets : [];
  if (!Array.isArray(manifest.assets)) errors.push(`${prefix}.assets must be an array.`);
  const assetFiles = new Set<string>();
  for (let i = 0; i < assets.length; i++) {
    const asset = assets[i];
    if (!isRecord(asset)) {
      errors.push(`${prefix}.assets[${i}] must be an object.`);
      continue;
    }
    if (typeof asset.file !== 'string' || !asset.file) errors.push(`${prefix}.assets[${i}].file must be a non-empty string.`);
    else assetFiles.add(asset.file);
    if (typeof asset.nodeId !== 'string') errors.push(`${prefix}.assets[${i}].nodeId must be a string.`);
    if (typeof asset.scale !== 'number') warnings.push(`${prefix}.assets[${i}].scale should be a number.`);
  }

  const elements = Array.isArray(manifest.elements) ? manifest.elements : [];
  if (!Array.isArray(manifest.elements)) errors.push(`${prefix}.elements must be an array.`);
  const elementIds = new Set<string>();
  for (let i = 0; i < elements.length; i++) {
    const el = elements[i];
    const ep = `${prefix}.elements[${i}]`;
    if (!isRecord(el)) {
      errors.push(`${ep} must be an object.`);
      continue;
    }
    for (const key of ['id', 'name', 'displayName', 'type']) {
      if (typeof el[key] !== 'string') errors.push(`${ep}.${key} must be a string.`);
    }
    if (typeof el.id === 'string') {
      if (elementIds.has(el.id)) errors.push(`${ep}.id duplicates another element id: ${el.id}`);
      elementIds.add(el.id);
    }
    if (!(typeof el.parentId === 'string' || el.parentId === null)) errors.push(`${ep}.parentId must be a string or null.`);
    if (!hasNumberRecord(el.rect, ['x', 'y', 'w', 'h'])) errors.push(`${ep}.rect must have numeric x/y/w/h.`);
    const transform = el.transform;
    if (!isRecord(transform)) {
      errors.push(`${ep}.transform must be an object.`);
    } else {
      for (const key of ['anchorMin', 'anchorMax', 'pivot', 'offsetMin', 'offsetMax']) {
        if (!numberArray(transform[key], 2)) errors.push(`${ep}.transform.${key} must be a numeric [x,y] array.`);
      }
      if (typeof transform.rotationZ !== 'number') errors.push(`${ep}.transform.rotationZ must be a number.`);
    }
    if (!Array.isArray(el.components)) errors.push(`${ep}.components must be an array.`);
    if (!Array.isArray(el.children)) errors.push(`${ep}.children must be an array.`);
    if (typeof el.asset === 'string' && !assetFiles.has(el.asset)) warnings.push(`${ep}.asset "${el.asset}" is not listed in ${prefix}.assets.`);
    if (isRecord(el.canonical)) {
      for (const key of ['kind', 'ref', 'instanceName']) {
        if (typeof el.canonical[key] !== 'string') errors.push(`${ep}.canonical.${key} must be a string.`);
      }
      if (el.canonical.variantProps !== undefined) {
        const vp = el.canonical.variantProps;
        if (!isRecord(vp)) {
          errors.push(`${ep}.canonical.variantProps must be an object when present.`);
        } else {
          if (!isRecord(vp.axes)) errors.push(`${ep}.canonical.variantProps.axes must be an object.`);
          if (!Array.isArray(vp.raw)) {
            errors.push(`${ep}.canonical.variantProps.raw must be an array.`);
          } else {
            for (let j = 0; j < vp.raw.length; j++) {
              const raw = vp.raw[j];
              const rp = `${ep}.canonical.variantProps.raw[${j}]`;
              if (!isRecord(raw)) {
                errors.push(`${rp} must be an object.`);
                continue;
              }
              for (const key of ['axis', 'value', 'originalName', 'originalValue', 'source']) {
                if (typeof raw[key] !== 'string') errors.push(`${rp}.${key} must be a string.`);
              }
            }
          }
        }
      }
    }
  }

  for (let i = 0; i < elements.length; i++) {
    const el = elements[i];
    if (!isRecord(el)) continue;
    if (typeof el.parentId === 'string' && !elementIds.has(el.parentId)) {
      warnings.push(`${prefix}.elements[${i}].parentId "${el.parentId}" does not match another element.`);
    }
    if (Array.isArray(el.children)) {
      for (const child of el.children) {
        if (typeof child === 'string' && !elementIds.has(child)) warnings.push(`${prefix}.elements[${i}].children references missing id "${child}".`);
      }
    }
  }

  if (!Array.isArray(manifest.fonts)) errors.push(`${prefix}.fonts must be an array.`);
  if (manifest.diagnostics !== undefined) {
    if (!isRecord(manifest.diagnostics)) {
      errors.push(`${prefix}.diagnostics must be an object when present.`);
    } else {
      const diag = manifest.diagnostics;
      if (!isRecord(diag.summary)) warnings.push(`${prefix}.diagnostics.summary should be an object.`);
      if (!Array.isArray(diag.issues)) {
        warnings.push(`${prefix}.diagnostics.issues should be an array.`);
      } else {
        for (let i = 0; i < diag.issues.length; i++) {
          const issue = diag.issues[i];
          const ip = `${prefix}.diagnostics.issues[${i}]`;
          if (!isRecord(issue)) {
            warnings.push(`${ip} should be an object.`);
            continue;
          }
          if (typeof issue.category !== 'string' || !diagnosticCategories.has(issue.category)) warnings.push(`${ip}.category is not a known diagnostic category.`);
          if (typeof issue.severity !== 'string' || !diagnosticSeverities.has(issue.severity)) warnings.push(`${ip}.severity should be "info" or "warning".`);
          if (typeof issue.message !== 'string' || !issue.message) warnings.push(`${ip}.message should be a non-empty string.`);
        }
      }
    }
  }
  if (!Array.isArray(manifest.canonicalRefs)) errors.push(`${prefix}.canonicalRefs must be an array.`);

  summary.screen = isRecord(screen) ? screen.name : undefined;
  summary.elements = elements.length;
  summary.assets = assets.length;
  summary.diagnostics = isRecord(manifest.diagnostics) && Array.isArray(manifest.diagnostics.issues)
    ? manifest.diagnostics.issues.length
    : undefined;
  summary.canonicalRefs = Array.isArray(manifest.canonicalRefs) ? manifest.canonicalRefs.length : undefined;
  return { errors, warnings, summary };
}

function validateProject(project: unknown): { errors: string[]; warnings: string[]; summary: Record<string, unknown> } {
  const errors: string[] = [];
  const warnings: string[] = [];
  const summary: Record<string, unknown> = {};
  if (!isRecord(project)) {
    return { errors: ['project must be an object.'], warnings, summary };
  }
  if (project.schema !== 'figforge/project') errors.push('project.schema must be "figforge/project".');
  if (typeof project.version !== 'string') errors.push('project.version must be a string.');
  if (project.generator !== 'FigForge') errors.push('project.generator must be "FigForge".');
  if (typeof project.name !== 'string') errors.push('project.name must be a string.');
  if (typeof project.initial !== 'string') errors.push('project.initial must be a string.');
  const screens = Array.isArray(project.screens) ? project.screens : [];
  if (!Array.isArray(project.screens)) errors.push('project.screens must be an array.');
  const names = new Set<string>();
  for (let i = 0; i < screens.length; i++) {
    const screen = screens[i];
    if (!isRecord(screen)) {
      errors.push(`project.screens[${i}] must be an object.`);
      continue;
    }
    for (const key of ['name', 'manifest', 'section', 'role']) {
      if (typeof screen[key] !== 'string') errors.push(`project.screens[${i}].${key} must be a string.`);
    }
    if (typeof screen.name === 'string') names.add(screen.name);
    if (typeof screen.manifest === 'string' && !screen.manifest.endsWith('/manifest.json')) {
      warnings.push(`project.screens[${i}].manifest usually points at "<screen>/manifest.json".`);
    }
  }
  if (typeof project.initial === 'string' && screens.length > 0 && !names.has(project.initial)) {
    warnings.push(`project.initial "${project.initial}" is not one of project.screens[].name.`);
  }
  summary.name = project.name;
  summary.screens = screens.length;
  summary.initial = project.initial;
  return { errors, warnings, summary };
}

export function registerTools(server: McpServer, sender: PluginSender, workspaceRoot: string): void {
  server.tool('get_metadata', 'Get the Figma file name, pages, and current page.', {}, async () =>
    unwrap(await sender.send('get_metadata'))
  );

  server.tool('get_document', 'Get the current page document tree (depth 2) with layout, style, text, canonical, and export metadata.', {}, async () =>
    unwrap(await sender.send('get_document'))
  );

  server.tool('get_selection', 'Get the currently selected nodes with layout, style, text, canonical, and export metadata.', {}, async () =>
    unwrap(await sender.send('get_selection'))
  );

  server.tool('get_node', 'Get a specific node by id (deep) with layout, style, text, canonical, and export metadata.', getNodeInput, async ({ nodeId }) =>
    unwrap(await sender.send('get_node', [nodeId], { nodeId }))
  );

  server.tool(
    'list_frames',
    'List exportable top-level Figma frames on the current page or all pages, including page, section, role, bounds, and frame ids.',
    listFramesInput,
    async ({ currentPageOnly, includeHidden }) =>
      unwrap(await sender.send('list_frames', undefined, { currentPageOnly, includeHidden }))
  );

  server.tool(
    'list_screens',
    'List FigForge screen frames eligible for page/project export, including page, section, role, bounds, and frame ids.',
    listFramesInput,
    async ({ currentPageOnly, includeHidden }) =>
      unwrap(await sender.send('list_screens', undefined, { currentPageOnly, includeHidden }))
  );

  server.tool(
    'get_node_details',
    'Get detailed Figma node data: bounds, constraints, text, fills/strokes, effects, canonical binding, and exportability hints.',
    nodeDetailsInput,
    async ({ nodeId }) => unwrap(await sender.send('get_node_details', [nodeId], { nodeId }))
  );

  server.tool(
    'get_design_context',
    'Get a layout-aware design tree of the current page, including bounds, constraints, style, text, canonical, and export metadata.',
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
      // Bulk screenshot saves can return many large base64 PNGs at once, so
      // give them the same long round-trip budget as exports.
      const r = await sender.send('get_screenshot', ids, { scale }, EXPORT_TIMEOUT_MS);
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
    'Export a Figma frame as a FigForge Unity package (manifest.json + PNG assets) into a folder on disk, using the plugin exporter. Optional scale, options, and elementConfigs match the plugin panel export controls.',
    exportUnityInput,
    async ({ nodeId, outDir, scale, options, elementConfigs }) => {
      try {
        return ok(await executeExportUnity(sender, nodeId, outDir, workspaceRoot, { scale, options, elementConfigs }));
      } catch (e) {
        return fail(e instanceof Error ? e.message : String(e));
      }
    }
  );

  server.tool(
    'export_project_unity',
    'Export the current Figma page as a FigForge Unity project bundle (project.json + per-screen manifest/assets) into a folder on disk.',
    exportProjectUnityInput,
    async ({ outDir, scale, options, elementConfigs }) => {
      try {
        return ok(await executeExportProjectUnity(sender, outDir, workspaceRoot, { scale, options, elementConfigs }));
      } catch (e) {
        return fail(e instanceof Error ? e.message : String(e));
      }
    }
  );

  server.tool(
    'validate_manifest_contract',
    'Validate a FigForge manifest or project JSON object/string against the Unity importer DTO contract expectations.',
    validateManifestContractInput,
    async ({ manifest, manifestJson, project, projectJson }) => {
      const parseErrors: string[] = [];
      const manifestData = manifest !== undefined ? manifest : readJsonObject(manifestJson, 'manifestJson', parseErrors);
      const projectData = project !== undefined ? project : readJsonObject(projectJson, 'projectJson', parseErrors);
      const results: Record<string, unknown> = {};
      const errors = [...parseErrors];
      const warnings: string[] = [];

      if (manifestData !== undefined) {
        const r = validateManifest(manifestData);
        errors.push(...r.errors);
        warnings.push(...r.warnings);
        results.manifest = r.summary;
      }
      if (projectData !== undefined) {
        const r = validateProject(projectData);
        errors.push(...r.errors);
        warnings.push(...r.warnings);
        results.project = r.summary;
      }
      if (manifestData === undefined && projectData === undefined && parseErrors.length === 0) {
        errors.push('Provide manifest, manifestJson, project, or projectJson.');
      }

      return ok({ valid: errors.length === 0, errors, warnings, ...results });
    }
  );

  server.tool(
    'create_canonical',
    'Create or reuse a FigForge canonical control master and place an instance in the current design frame.',
    createCanonicalInput,
    async ({ kind, componentsPage, listOpts, sliderOpts, tableOpts, progressOpts, stepperOpts }) =>
      unwrap(await sender.send('create_canonical', undefined, { kind, componentsPage, listOpts, sliderOpts, tableOpts, progressOpts, stepperOpts }))
  );

  server.tool(
    'create_shell',
    'Create a FigForge shell scaffold with a shell frame, content slot, sample screens, and navigation buttons.',
    createShellInput,
    async ({ componentsPage, shellOpts }) => unwrap(await sender.send('create_shell', undefined, { componentsPage, shellOpts }))
  );
}
