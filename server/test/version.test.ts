// =============================================================================
// Guards VERSION against drifting from server/package.json "version". The bridge
// advertises VERSION in its /ping payload and MCP server info, so a stale value
// silently misreports the running build. version.ts can't import package.json
// (it sits outside tsconfig's rootDir: src/), so this test enforces the pairing
// that the source comment asks contributors to bump together on every release.
// =============================================================================

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

import { VERSION } from '../dist/version.js';

const pkg = JSON.parse(
  readFileSync(new URL('../package.json', import.meta.url), 'utf8'),
) as { version: string };

describe('VERSION', () => {
  it('matches the version in package.json', () => {
    assert.equal(VERSION, pkg.version);
  });
});
