// =============================================================================
// Follower — proxies tool calls to the active Leader over HTTP /rpc.
// =============================================================================

import type { PluginSender, RpcRequest, RpcResponse } from './types.js';
import { BRIDGE_PORT, EXPORT_TIMEOUT_MS } from './version.js';

// Sit above the leader's longest per-tool budget (export round-trips) so the
// proxy fetch never aborts before the leader's export actually completes.
const RPC_FETCH_TIMEOUT_MS = EXPORT_TIMEOUT_MS + 30_000;

export class Follower implements PluginSender {
  private base = `http://127.0.0.1:${BRIDGE_PORT}`;

  async ping(): Promise<boolean> {
    return (await this.pingStatus()).reachable;
  }

  /**
   * Probe the leader's /ping. `reachable` means a leader answered; `pluginConnected`
   * reflects whether that leader currently holds the Figma plugin WebSocket (the
   * /ping body exposes it — see Leader.onRequest). A freshly-elected leader is
   * reachable but not yet plugin-connected during the plugin's reconnect window.
   */
  async pingStatus(): Promise<{ reachable: boolean; pluginConnected: boolean }> {
    try {
      const r = await fetch(`${this.base}/ping`, { signal: AbortSignal.timeout(2000) });
      if (!r.ok) return { reachable: false, pluginConnected: false };
      const body = (await r.json()) as { pluginConnected?: unknown };
      return { reachable: true, pluginConnected: body?.pluginConnected === true };
    } catch {
      return { reachable: false, pluginConnected: false };
    }
  }

  // timeoutMs is accepted for the PluginSender contract; the leader derives the
  // per-tool plugin budget from the tool name, so the follower only needs its
  // proxy fetch to outlast the longest of those budgets.
  async send(tool: string, nodeIds?: string[], params?: Record<string, unknown>, _timeoutMs?: number): Promise<RpcResponse> {
    const body: RpcRequest = { tool, nodeIds, params };
    try {
      const r = await fetch(`${this.base}/rpc`, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify(body),
        signal: AbortSignal.timeout(RPC_FETCH_TIMEOUT_MS),
      });
      if (!r.ok) return { error: `Leader returned ${r.status}` };
      return (await r.json()) as RpcResponse;
    } catch (e) {
      return { error: `Leader unreachable: ${e instanceof Error ? e.message : String(e)}` };
    }
  }
}
