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

export const exportUnityInput = {
  nodeId: figmaNodeId.describe('The frame node id to export'),
  outDir: z
    .string()
    .min(1)
    .describe('Output directory (relative to the bridge cwd) for manifest.json + PNGs'),
};
