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
  // Single-flight takeover guard — see tryBecomeLeader().
  private takeover: Promise<void> | null = null;

  async start(): Promise<Role> {
    await this.tryBecomeLeader();
    return this.role;
  }

  /**
   * Attempt to bind the leader port, with at most one attempt in flight.
   *
   * Why the mutex: two concurrent send() calls can both observe a dead leader
   * and both race in here. Each used to construct its own listening Leader;
   * the loser's EADDRINUSE catch then stomped role/leader back to follower
   * AFTER the winner had installed itself — orphaning a bound Leader nobody
   * references. The orphan holds the port forever, so the node can never
   * become leader again (permanently degraded). Now the first caller performs
   * the takeover and concurrent callers await the same promise.
   */
  private tryBecomeLeader(): Promise<void> {
    if (!this.takeover) {
      this.takeover = this.performTakeover().finally(() => {
        // Clear on settle (success or failure alike) so a future dead-leader
        // detection starts a fresh attempt instead of awaiting a stale,
        // already-settled promise.
        this.takeover = null;
      });
    }
    return this.takeover;
  }

  private async performTakeover(): Promise<void> {
    // Late-joiner guard: a caller queued behind a finished takeover (mutex
    // already cleared) must not create a SECOND Leader next to a live one —
    // binding would fail against our own port and the catch below would
    // wrongly demote us, orphaning the existing listening Leader.
    if (this.leader) return;
    const leader = new Leader();
    try {
      await leader.listen();
      this.leader = leader;
      this.role = 'leader';
    } catch {
      // Port taken → someone else is leader; act as follower. Close the
      // never-bound Leader so its WebSocketServer can't linger.
      leader.close();
      this.leader = null;
      this.role = 'follower';
    }
  }

  async send(tool: string, nodeIds?: string[], params?: Record<string, unknown>, timeoutMs?: number): Promise<RpcResponse> {
    if (this.role === 'leader' && this.leader) {
      return this.leader.send(tool, nodeIds, params, timeoutMs);
    }
    // Follower path. If the leader has vanished, try to take over once.
    const alive = await this.follower.ping();
    if (!alive) {
      await this.tryBecomeLeader();
      if (this.role === 'leader' && this.leader) {
        return this.leader.send(tool, nodeIds, params, timeoutMs);
      }
    }
    return this.follower.send(tool, nodeIds, params, timeoutMs);
  }
}
