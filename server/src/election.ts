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

// After a takeover we lost (someone else became leader), the winner's Figma
// plugin WebSocket has usually dropped and is mid-reconnect. Sending immediately
// races that reconnect and surfaces a spurious "plugin not connected" error during
// a normal leader-restart window. Poll the new leader's /ping pluginConnected flag
// for a short bounded period so a healthy system isn't reported as broken. The
// bound is small (well under the per-tool budgets) and only delays the lost-race
// path — the happy path (we are leader, or a follower whose leader's plugin is
// already connected) is untouched.
const PLUGIN_RECONNECT_WAIT_MS = 3_000;
const PLUGIN_RECONNECT_POLL_MS = 250;

const delay = (ms: number): Promise<void> => new Promise((resolve) => setTimeout(resolve, ms));

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
    const status = await this.follower.pingStatus();
    if (!status.reachable) {
      await this.tryBecomeLeader();
      if (this.role === 'leader' && this.leader) {
        return this.leader.send(tool, nodeIds, params, timeoutMs);
      }
      // We lost the port race: a brand-new leader just bound. Its plugin socket
      // is almost certainly mid-reconnect, so wait (bounded) for it to land
      // before sending — otherwise we'd surface a spurious not-connected error
      // during a normal leader restart.
      await this.waitForPluginReconnect();
    } else if (!status.pluginConnected) {
      // Leader is up but its plugin isn't connected yet (e.g. it just took over
      // from a crashed leader). Same bounded wait before issuing the send.
      await this.waitForPluginReconnect();
    }
    return this.follower.send(tool, nodeIds, params, timeoutMs);
  }

  /**
   * Poll the leader's /ping pluginConnected flag until the plugin reconnects or
   * the bounded window elapses. Returns regardless; the subsequent send still
   * surfaces a real not-connected error if the plugin never came back. This only
   * retries the wait/probe — it never re-issues a tool call, so it is
   * side-effect-free.
   */
  private async waitForPluginReconnect(): Promise<void> {
    const deadline = Date.now() + PLUGIN_RECONNECT_WAIT_MS;
    while (Date.now() < deadline) {
      const status = await this.follower.pingStatus();
      if (status.reachable && status.pluginConnected) return;
      const remaining = deadline - Date.now();
      if (remaining <= 0) return;
      await delay(Math.min(PLUGIN_RECONNECT_POLL_MS, remaining));
    }
  }
}
