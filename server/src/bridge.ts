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
    }
    this.socket = socket;
    socket.on('message', (raw) => this.onMessage(raw.toString()));
    socket.on('close', () => {
      if (this.socket === socket) this.socket = null;
    });
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
