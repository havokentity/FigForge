// =============================================================================
// Node + election — decides whether this process is the Leader (owns the plugin
// WebSocket + HTTP server) or a Follower (proxies to the leader). If the leader
// dies, a follower's next call triggers a takeover attempt.
//
// Why this exists: every MCP client (Claude, Cursor, …) spawns its own stdio
// bridge process, but only ONE WebSocket connection to the single Figma plugin
// can exist. Leader election arbitrates that shared resource.
// =============================================================================

import { Leader } from './leader.js';
import { Follower } from './follower.js';
import type { PluginSender, RpcResponse } from './types.js';

type Role = 'leader' | 'follower';

export class FigForgeNode implements PluginSender {
  private role: Role = 'follower';
  private leader: Leader | null = null;
  private follower = new Follower();

  async start(): Promise<Role> {
    await this.tryBecomeLeader();
    return this.role;
  }

  private async tryBecomeLeader(): Promise<void> {
    const leader = new Leader();
    try {
      await leader.listen();
      this.leader = leader;
      this.role = 'leader';
    } catch {
      // Port taken → someone else is leader; act as follower.
      leader.close();
      this.leader = null;
      this.role = 'follower';
    }
  }

  async send(tool: string, nodeIds?: string[], params?: Record<string, unknown>): Promise<RpcResponse> {
    if (this.role === 'leader' && this.leader) {
      return this.leader.send(tool, nodeIds, params);
    }
    // Follower path. If the leader has vanished, try to take over once.
    const alive = await this.follower.ping();
    if (!alive) {
      await this.tryBecomeLeader();
      if (this.role === 'leader' && this.leader) {
        return this.leader.send(tool, nodeIds, params);
      }
    }
    return this.follower.send(tool, nodeIds, params);
  }
}
