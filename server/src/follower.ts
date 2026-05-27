// =============================================================================
// Follower — proxies tool calls to the active Leader over HTTP /rpc.
// =============================================================================

import type { PluginSender, RpcRequest, RpcResponse } from './types.js';
import { BRIDGE_PORT } from './version.js';

export class Follower implements PluginSender {
  private base = `http://127.0.0.1:${BRIDGE_PORT}`;

  async ping(): Promise<boolean> {
    try {
      const r = await fetch(`${this.base}/ping`, { signal: AbortSignal.timeout(2000) });
      return r.ok;
    } catch {
      return false;
    }
  }

  async send(tool: string, nodeIds?: string[], params?: Record<string, unknown>): Promise<RpcResponse> {
    const body: RpcRequest = { tool, nodeIds, params };
    try {
      const r = await fetch(`${this.base}/rpc`, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify(body),
        signal: AbortSignal.timeout(35_000),
      });
      if (!r.ok) return { error: `Leader returned ${r.status}` };
      return (await r.json()) as RpcResponse;
    } catch (e) {
      return { error: `Leader unreachable: ${e instanceof Error ? e.message : String(e)}` };
    }
  }
}
