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
import { rpcRequestSchema } from './schema.js';
import type { PluginSender, RpcResponse } from './types.js';
import { BRIDGE_PORT, EXPORT_TIMEOUT_MS, VERSION } from './version.js';

// Tools that ship large base64 payloads and need the long round-trip budget
// when proxied through a follower (which carries no explicit timeout over /rpc).
const EXPORT_TOOLS = new Set(['export_unity', 'export_project_unity', 'get_screenshot']);

export const RPC_MAX_BODY_BYTES = 1_048_576;

// Generous cap for large exports; well under the ws default of 100 MiB so a
// runaway/hostile frame can't exhaust memory but a legitimate export fits.
export const WS_MAX_PAYLOAD_BYTES = 64 * 1024 * 1024;

/** Loopback addresses the dev bridge accepts upgrades from. */
const LOOPBACK_ADDRESSES = new Set(['127.0.0.1', '::1', '::ffff:127.0.0.1']);

function isLoopbackAddress(address: string | undefined): boolean {
  return address !== undefined && LOOPBACK_ADDRESSES.has(address);
}

export class RpcBodyTooLargeError extends Error {
  constructor(limitBytes = RPC_MAX_BODY_BYTES) {
    super(`RPC body exceeds ${limitBytes} bytes.`);
    this.name = 'RpcBodyTooLargeError';
  }
}

export function readRpcBody(req: http.IncomingMessage, limitBytes = RPC_MAX_BODY_BYTES): Promise<string> {
  const contentLength = req.headers['content-length'];
  const declaredBytes = typeof contentLength === 'string' ? Number(contentLength) : undefined;
  if (declaredBytes !== undefined && Number.isFinite(declaredBytes) && declaredBytes > limitBytes) {
    req.on('error', () => { /* discard errors while draining rejected body */ });
    req.resume();
    return Promise.reject(new RpcBodyTooLargeError(limitBytes));
  }

  return new Promise((resolve, reject) => {
    const chunks: Buffer[] = [];
    let receivedBytes = 0;
    let settled = false;

    const cleanup = () => {
      req.off('data', onData);
      req.off('end', onEnd);
      req.off('error', onError);
    };
    const fail = (error: Error) => {
      if (settled) return;
      settled = true;
      chunks.length = 0;
      cleanup();
      req.on('error', () => { /* discard errors while draining rejected body */ });
      req.resume();
      reject(error);
    };
    const onData = (chunk: Buffer | string) => {
      const buffer = Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk);
      receivedBytes += buffer.length;
      if (receivedBytes > limitBytes) {
        fail(new RpcBodyTooLargeError(limitBytes));
        return;
      }
      chunks.push(buffer);
    };
    const onEnd = () => {
      if (settled) return;
      settled = true;
      cleanup();
      resolve(Buffer.concat(chunks, receivedBytes).toString('utf8'));
    };
    const onError = (error: Error) => fail(error);

    req.on('data', onData);
    req.on('end', onEnd);
    req.on('error', onError);
  });
}

export class Leader implements PluginSender {
  private server: http.Server;
  private wss: WebSocketServer;
  readonly bridge = new Bridge();

  constructor() {
    this.wss = new WebSocketServer({ noServer: true, maxPayload: WS_MAX_PAYLOAD_BYTES });
    // An 'error' event with no listener throws and crashes the process; log instead.
    this.wss.on('error', (err) => {
      process.stderr.write(`[figforge-bridge] ws server error: ${err instanceof Error ? err.message : String(err)}\n`);
    });
    this.server = http.createServer((req, res) => this.onRequest(req, res));
    this.server.on('upgrade', (req, socket, head) => {
      if (req.url !== undefined && this.isAllowedWsUpgrade(req)) {
        this.wss.handleUpgrade(req, socket, head, (ws) => this.bridge.attach(ws));
      } else {
        socket.destroy();
      }
    });
  }

  /**
   * Gate /ws upgrades. The server is already loopback-bound, so this is
   * defense in depth and must NOT reject the real plugin:
   *   - the path must be /ws (ignoring any query string);
   *   - the peer must be loopback;
   *   - if FIGFORGE_BRIDGE_TOKEN is set, the client must present a matching
   *     token via ?token= or a Sec-WebSocket-Protocol value. When the env var
   *     is unset, no token is required (preserves the zero-config flow).
   */
  private isAllowedWsUpgrade(req: http.IncomingMessage): boolean {
    // req.url on an upgrade is the request-target (path + optional query),
    // never absolute, but parse defensively against a fixed loopback base.
    let pathname: string;
    let token: string | null;
    try {
      const url = new URL(req.url ?? '', 'http://127.0.0.1');
      pathname = url.pathname;
      token = url.searchParams.get('token');
    } catch {
      return false;
    }
    if (pathname !== '/ws') return false;
    if (!isLoopbackAddress(req.socket.remoteAddress ?? undefined)) return false;

    const expectedToken = process.env.FIGFORGE_BRIDGE_TOKEN;
    if (expectedToken) {
      const protocolToken = req.headers['sec-websocket-protocol'];
      const presented =
        token ??
        (typeof protocolToken === 'string'
          ? protocolToken.split(',').map((s) => s.trim()).find((s) => s.length > 0) ?? null
          : null);
      if (presented !== expectedToken) return false;
    }
    return true;
  }

  /** Resolves once bound, rejects with EADDRINUSE if another leader exists. */
  listen(): Promise<void> {
    return new Promise((resolve, reject) => {
      this.server.once('error', reject);
      this.server.listen(BRIDGE_PORT, '127.0.0.1', () => {
        this.server.off('error', reject);
        // Bind succeeded: replace the one-shot bind guard with a permanent
        // handler so a later runtime error (e.g. EMFILE/ENFILE on accept under
        // fd exhaustion) is logged instead of crashing the bridge as an
        // unhandled 'error' event — which would sever the plugin socket and
        // force every follower to scramble for takeover.
        this.server.on('error', (err) => {
          process.stderr.write(`[figforge-bridge] http server error: ${err instanceof Error ? err.message : String(err)}\n`);
        });
        resolve();
      });
    });
  }

  send(tool: string, nodeIds?: string[], params?: Record<string, unknown>, timeoutMs?: number): Promise<RpcResponse> {
    return this.bridge.send(tool, nodeIds, params, timeoutMs);
  }

  private onRequest(req: http.IncomingMessage, res: http.ServerResponse): void {
    if (req.method === 'GET' && req.url === '/ping') {
      res.writeHead(200, { 'content-type': 'application/json' });
      res.end(JSON.stringify({ status: 'ok', version: VERSION, pluginConnected: this.bridge.connected }));
      return;
    }
    if (req.method === 'POST' && req.url === '/rpc') {
      void this.handleRpc(req, res);
      return;
    }
    res.writeHead(404);
    res.end();
  }

  private async handleRpc(req: http.IncomingMessage, res: http.ServerResponse): Promise<void> {
    let result: RpcResponse;
    try {
      const body = await readRpcBody(req);
      const parsed = rpcRequestSchema.safeParse(JSON.parse(body));
      if (!parsed.success) {
        const issue = parsed.error.issues[0];
        const where = issue?.path.length ? issue.path.join('.') : 'request';
        throw new Error(`Invalid RPC request (${where}): ${issue?.message ?? 'bad shape'}`);
      }
      const rpc = parsed.data;
      // A follower proxying a heavy export carries no timeout over /rpc; give
      // the plugin the same long budget the leader would use for these tools.
      const timeoutMs = EXPORT_TOOLS.has(rpc.tool) ? EXPORT_TIMEOUT_MS : undefined;
      result = await this.bridge.send(rpc.tool, rpc.nodeIds, rpc.params, timeoutMs);
    } catch (e) {
      if (e instanceof RpcBodyTooLargeError) {
        res.writeHead(413, { 'content-type': 'application/json' });
        res.end(JSON.stringify({ error: e.message }));
        return;
      }
      result = { error: e instanceof Error ? e.message : String(e) };
    }
    res.writeHead(200, { 'content-type': 'application/json' });
    res.end(JSON.stringify(result));
  }

  close(): void {
    try { this.wss.close(); } catch { /* ignore */ }
    try { this.server.close(); } catch { /* ignore */ }
  }
}
