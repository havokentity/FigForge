// =============================================================================
// Bridge — owns the single WebSocket connection to the Figma plugin and
// correlates request/response pairs by id.
// =============================================================================

import type { WebSocket } from 'ws';
import type { BridgeResponse, RpcResponse } from './types.js';

const REQUEST_TIMEOUT_MS = 30_000;

interface Pending {
  resolve: (r: RpcResponse) => void;
  timer: ReturnType<typeof setTimeout>;
}

export class Bridge {
  private socket: WebSocket | null = null;
  private pending = new Map<string, Pending>();
  private counter = 0;

  get connected(): boolean {
    return this.socket !== null && this.socket.readyState === 1; // OPEN
  }

  /** Register the plugin's WebSocket. Only one is kept; a new one replaces it. */
  attach(socket: WebSocket): void {
    if (this.socket) {
      try { this.socket.close(); } catch { /* ignore */ }
      // In-flight requests went out over the old connection; the replacement
      // plugin session has no knowledge of their requestIds, so no response
      // can ever arrive. Fail them now instead of letting each caller sit
      // out the full timeout.
      this.failAllPending('Figma plugin reconnected; in-flight request dropped.');
    }
    this.socket = socket;
    socket.on('message', (raw) => this.onMessage(raw.toString()));
    // Fail pending work the moment the plugin goes away — otherwise a
    // mid-export disconnect leaves every caller hanging for the full
    // REQUEST_TIMEOUT_MS before it learns anything.
    const detach = () => {
      if (this.socket !== socket) return; // an attach() already replaced us
      this.socket = null;
      this.failAllPending('Figma plugin disconnected.');
    };
    socket.on('close', detach);
    // ws emits 'close' after 'error', but handle 'error' directly too: it
    // fails callers without waiting on the close handshake, and an unhandled
    // 'error' event on the socket would crash the process.
    socket.on('error', detach);
  }

  /** Resolve every pending request with an error and cancel its timer. */
  private failAllPending(message: string): void {
    for (const p of this.pending.values()) {
      clearTimeout(p.timer);
      p.resolve({ error: message });
    }
    this.pending.clear();
  }

  private onMessage(raw: string): void {
    let msg: BridgeResponse;
    try {
      msg = JSON.parse(raw);
    } catch {
      return;
    }
    const p = this.pending.get(msg.requestId);
    if (!p) return;
    clearTimeout(p.timer);
    this.pending.delete(msg.requestId);
    p.resolve({ data: msg.data, error: msg.error });
  }

  private nextId(): string {
    const now = new Date();
    const hhmmss =
      `${now.getHours()}`.padStart(2, '0') +
      `${now.getMinutes()}`.padStart(2, '0') +
      `${now.getSeconds()}`.padStart(2, '0');
    return `req-${hhmmss}-${++this.counter}`;
  }

  send(tool: string, nodeIds?: string[], params?: Record<string, unknown>): Promise<RpcResponse> {
    if (!this.connected || !this.socket) {
      return Promise.resolve({ error: 'Figma plugin is not connected to the bridge.' });
    }
    const requestId = this.nextId();
    const payload = { type: tool, requestId, nodeIds, params };
    return new Promise<RpcResponse>((resolve) => {
      const timer = setTimeout(() => {
        this.pending.delete(requestId);
        resolve({ error: `Timed out waiting for plugin (${tool}).` });
      }, REQUEST_TIMEOUT_MS);
      this.pending.set(requestId, { resolve, timer });
      this.socket!.send(JSON.stringify(payload));
    });
  }
}
