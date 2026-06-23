// Shared protocol types for the FigForge bridge.

/** A request the server sends down to the Figma plugin over WebSocket. */
export interface BridgeRequest {
  type: string;
  requestId: string;
  nodeIds?: string[];
  params?: Record<string, unknown>;
}

/** The plugin's reply, correlated by requestId. */
export interface BridgeResponse {
  type: string;
  requestId: string;
  data?: unknown;
  error?: string;
}

/** Follower → Leader HTTP RPC envelope (no requestId; HTTP correlates). */
export interface RpcRequest {
  tool: string;
  nodeIds?: string[];
  params?: Record<string, unknown>;
}
export interface RpcResponse {
  data?: unknown;
  error?: string;
}

/** Minimal MCP tool result shape (index signature satisfies the SDK's type). */
export type ToolResult = {
  [x: string]: unknown;
  content: { type: 'text'; text: string }[];
  isError?: boolean;
};

/** Anything that can send a tool request to the plugin and await a response. */
export interface PluginSender {
  send(tool: string, nodeIds?: string[], params?: Record<string, unknown>, timeoutMs?: number): Promise<RpcResponse>;
}
