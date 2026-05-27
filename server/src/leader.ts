// =============================================================================
// Leader — owns the HTTP + WebSocket server. Exactly one process is leader; it
// holds the plugin connection and serves followers via /rpc.
//   GET  /ping  → health { status, version }
//   POST /rpc   → proxy a tool call to the plugin
//   WS   /ws    → the Figma plugin connects here
// =============================================================================

import http from 'node:http';
import { WebSocketServer } from 'ws';
import { Bridge } from './bridge.js';
import type { PluginSender, RpcRequest, RpcResponse } from './types.js';
import { BRIDGE_PORT, VERSION } from './version.js';

export class Leader implements PluginSender {
  private server: http.Server;
  private wss: WebSocketServer;
  readonly bridge = new Bridge();

  constructor() {
    this.wss = new WebSocketServer({ noServer: true });
    this.server = http.createServer((req, res) => this.onRequest(req, res));
    this.server.on('upgrade', (req, socket, head) => {
      if (req.url === '/ws') {
        this.wss.handleUpgrade(req, socket, head, (ws) => this.bridge.attach(ws));
      } else {
        socket.destroy();
      }
    });
  }

  /** Resolves once bound, rejects with EADDRINUSE if another leader exists. */
  listen(): Promise<void> {
    return new Promise((resolve, reject) => {
      this.server.once('error', reject);
      this.server.listen(BRIDGE_PORT, '127.0.0.1', () => {
        this.server.off('error', reject);
        resolve();
      });
    });
  }

  send(tool: string, nodeIds?: string[], params?: Record<string, unknown>): Promise<RpcResponse> {
    return this.bridge.send(tool, nodeIds, params);
  }

  private onRequest(req: http.IncomingMessage, res: http.ServerResponse): void {
    if (req.method === 'GET' && req.url === '/ping') {
      res.writeHead(200, { 'content-type': 'application/json' });
      res.end(JSON.stringify({ status: 'ok', version: VERSION, pluginConnected: this.bridge.connected }));
      return;
    }
    if (req.method === 'POST' && req.url === '/rpc') {
      let body = '';
      req.on('data', (c) => (body += c));
      req.on('end', async () => {
        let result: RpcResponse;
        try {
          const rpc = JSON.parse(body) as RpcRequest;
          result = await this.bridge.send(rpc.tool, rpc.nodeIds, rpc.params);
        } catch (e) {
          result = { error: e instanceof Error ? e.message : String(e) };
        }
        res.writeHead(200, { 'content-type': 'application/json' });
        res.end(JSON.stringify(result));
      });
      return;
    }
    res.writeHead(404);
    res.end();
  }

  close(): void {
    try { this.wss.close(); } catch { /* ignore */ }
    try { this.server.close(); } catch { /* ignore */ }
  }
}
