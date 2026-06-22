// Zod input schemas for MCP tools.
import { z } from 'zod';

// Figma node ids use a colon (e.g. "123:456"), never a hyphen. Nodes inside
// component instances get instance-path ids: "I" + one or more ";"-separated
// "num:num" segments (e.g. "I123:4;567:8" = node 567:8 inside instance 123:4).
// get_selection/get_document hand those back verbatim, so the tools must
// accept them too — a plain-id-only regex made every instance descendant
// unaddressable.
export const figmaNodeId = z
  .string()
  .regex(
    /^(?:[0-9]+:[0-9]+|I[0-9]+:[0-9]+(?:;[0-9]+:[0-9]+)*)$/,
    'Expected a Figma node id like "123:456" or an instance path like "I123:4;567:8" (colon, not hyphen).'
  );

export const screenshotFormat = z.enum(['PNG', 'SVG', 'JPG']).default('PNG');

export const getNodeInput = { nodeId: figmaNodeId.describe('Node id to fetch') };

export const designContextInput = {
  depth: z.number().int().min(1).max(6).default(2).describe('Tree depth to summarize'),
};

export const screenshotInput = {
  nodeIds: z.array(figmaNodeId).optional().describe('Nodes to render (defaults to selection)'),
  scale: z.number().min(0.25).max(4).default(2).describe('Export scale'),
};

export const saveScreenshotsInput = {
  items: z
    .array(z.object({ nodeId: figmaNodeId, outputPath: z.string().min(1) }))
    .min(1)
    .describe('Per-node output paths (relative to the bridge working directory)'),
  scale: z.number().min(0.25).max(4).default(2),
};

export const exportScale = z.discriminatedUnion('type', [
  z.object({ type: z.literal('scale'), value: z.number().min(0.25).max(4) }),
  z.object({ type: z.literal('width'), value: z.number().min(1) }),
  z.object({ type: z.literal('height'), value: z.number().min(1) }),
]);

export const exportOptions = z
  .object({
    autoMerge: z.boolean().optional(),
    rasterizeStrokes: z.boolean().optional(),
    emitGradients: z.boolean().optional(),
    emitImageFills: z.boolean().optional(),
    fontFaceDilate: z.number().min(0).max(1).optional(),
    vanilla: z.boolean().optional(),
  })
  .describe('Exporter options; omitted fields use the plugin panel defaults/current values.');

export const elementConfig = z.object({
  id: figmaNodeId,
  excluded: z.boolean().optional(),
  merged: z.boolean().optional(),
  rasterize: z.boolean().optional(),
  passthrough: z.boolean().optional(),
});

export const exportUnityInput = {
  nodeId: figmaNodeId.describe('The frame node id to export'),
  outDir: z
    .string()
    .min(1)
    .describe('Output directory (relative to the bridge cwd) for manifest.json + PNGs'),
  scale: exportScale.optional().describe('Unity export scale. Defaults to the plugin panel scale.'),
  options: exportOptions.optional(),
  elementConfigs: z
    .array(elementConfig)
    .optional()
    .describe('Per-node export overrides matching the plugin panel toggles: excluded, merged, and rasterize.'),
};

export const listFramesInput = {
  currentPageOnly: z.boolean().default(true).describe('When true, list only the current Figma page.'),
  includeHidden: z.boolean().default(false).describe('Include hidden frames/screens.'),
};

export const nodeDetailsInput = {
  nodeId: figmaNodeId.describe('Node id to inspect'),
};

export const exportProjectUnityInput = {
  outDir: z
    .string()
    .min(1)
    .describe('Output directory (relative to the bridge cwd) for project.json, per-screen manifests, and PNGs'),
  scale: exportScale.optional().describe('Unity export scale. Defaults to the plugin panel scale.'),
  options: exportOptions.optional(),
  elementConfigs: z
    .array(elementConfig)
    .optional()
    .describe('Per-node export overrides matching the plugin panel toggles: excluded, merged, and rasterize.'),
};

export const validateManifestContractInput = {
  manifest: z.unknown().optional().describe('A FigForge manifest object, if already parsed.'),
  manifestJson: z.string().optional().describe('A FigForge manifest JSON string.'),
  project: z.unknown().optional().describe('A FigForge project.json object, if already parsed.'),
  projectJson: z.string().optional().describe('A FigForge project.json JSON string.'),
};

export const canonicalKind = z.enum([
  'button',
  'toggle',
  'radio',
  'switch',
  'input',
  'stepper',
  'dropdown',
  'slider',
  'progress',
  'list',
  'table',
  'modal',
  'toast',
]);

export const createCanonicalInput = {
  kind: canonicalKind.describe('Canonical control kind to scaffold.'),
  componentsPage: z.boolean().default(true).describe('Place/reuse masters on the FigForge Components page.'),
  listOpts: z.record(z.unknown()).optional().describe('List creator options, matching the plugin UI.'),
  sliderOpts: z.record(z.unknown()).optional().describe('Slider creator options, matching the plugin UI.'),
  tableOpts: z.record(z.unknown()).optional().describe('Table creator options, matching the plugin UI.'),
  progressOpts: z.record(z.unknown()).optional().describe('Progress creator options, matching the plugin UI.'),
  stepperOpts: z.record(z.unknown()).optional().describe('Stepper creator options, matching the plugin UI. Supports orientation: horizontal | vertical.'),
};

export const createShellInput = {
  componentsPage: z.boolean().default(true).describe('Place/reuse canonical masters on the FigForge Components page.'),
  shellOpts: z
    .object({
      name: z.string().optional(),
      nav: z.enum(['left', 'top', 'right', 'bottom']).optional(),
      header: z.enum(['left', 'top', 'right', 'bottom']).optional(),
      width: z.number().min(320).max(10000).optional(),
      height: z.number().min(240).max(10000).optional(),
    })
    .optional()
    .describe('Shell scaffold options, matching the plugin UI.'),
};

// Allow-list of bridge tool names the plugin understands — these are the
// message `type`s the leader forwards over the WebSocket (the literals passed
// to `sender.send(...)` in tools.ts). The follower /rpc handler validates
// against this set so a hostile local process can't drive the plugin with an
// arbitrary tool name. Keep in sync with the sender.send calls in tools.ts.
export const bridgeToolName = z.enum([
  'get_metadata',
  'get_document',
  'get_selection',
  'get_node',
  'list_frames',
  'list_screens',
  'get_node_details',
  'get_design_context',
  'get_screenshot',
  'export_unity',
  'export_project_unity',
  'create_canonical',
  'create_shell',
]);

// Follower → Leader /rpc envelope. The leader is loopback-bound, but any local
// process can still reach it, so its shape is validated (not trusted via a bare
// cast) before being proxied to the plugin: the tool must be allow-listed and
// node ids must be well-formed Figma ids. Unknown keys are stripped.
export const rpcRequestSchema = z.object({
  tool: bridgeToolName,
  nodeIds: z.array(figmaNodeId).optional(),
  params: z.record(z.unknown()).optional(),
});
