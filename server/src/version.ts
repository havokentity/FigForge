// Runtime-reported bridge version (/ping payload, MCP server info, startup
// log). Keep in LOCKSTEP with server/package.json "version" — JSON can't carry
// this comment, and importing package.json from here would break the dist/
// layout (tsconfig rootDir is src/), so the pairing is by convention: bump
// both together on every release.
export const VERSION = '1.0.40';
export const BRIDGE_PORT = 1994;
