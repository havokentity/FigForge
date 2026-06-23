// Runtime-reported bridge version (/ping payload, MCP server info, startup
// log). Must stay in LOCKSTEP with server/package.json "version" — importing
// package.json here would break the dist/ layout (it sits outside tsconfig's
// rootDir: src/), so the pairing is enforced by test/version.test.ts instead.
// Bump both together on every release.
export const VERSION = '1.0.41';
export const BRIDGE_PORT = 1994;

// Long round-trip budget for tools that ship large base64 payloads over the
// wire (export_unity / export_project_unity, and screenshot saves). Cheap query
// tools stay on Bridge's 30s default; only these heavy exports need the bump.
export const EXPORT_TIMEOUT_MS = 10 * 60_000;
