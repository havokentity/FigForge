#!/usr/bin/env node
// =============================================================================
// FigForge MCP bridge — entry point.
// Speaks MCP over stdio to the AI client; bridges to the Figma plugin over a
// local WebSocket (leader) or proxies to whichever process holds it (follower).
// =============================================================================

import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js';
import { FigForgeNode } from './election.js';
import { registerTools } from './tools.js';
import { VERSION } from './version.js';

async function main() {
  const node = new FigForgeNode();
  const role = await node.start();
  // stderr only — stdout is reserved for the MCP stdio transport.
  console.error(`[FigForge] bridge ${VERSION} started as ${role}`);

  const server = new McpServer({ name: 'figforge-bridge', version: VERSION });
  registerTools(server, node, process.cwd());

  const transport = new StdioServerTransport();
  await server.connect(transport);
}

main().catch((e) => {
  console.error('[FigForge] fatal:', e);
  process.exit(1);
});
