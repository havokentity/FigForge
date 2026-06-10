// =============================================================================
// Path-traversal guards for the on-disk write tools. Runs against the compiled
// dist/ output with the built-in node:test runner (no extra deps):
//   npm test   ->   tsc && node --test test/tools.test.ts
// =============================================================================

import { describe, it, beforeEach, afterEach } from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import os from 'node:os';
import { existsSync } from 'node:fs';
import { mkdtemp, mkdir, rm } from 'node:fs/promises';

import { resolveAndValidateOutputPath, executeExportUnity } from '../dist/tools.js';
import type { PluginSender, RpcResponse } from '../src/types.ts';

// A sender whose export_unity reply carries the given assets verbatim.
function senderWithAssets(assets: Array<{ name: unknown; data: unknown }>): PluginSender {
  return {
    async send(): Promise<RpcResponse> {
      return {
        data: { exports: [{ manifest: { screen: { name: 's' }, elements: [] }, assets }] },
      };
    },
  };
}

describe('resolveAndValidateOutputPath', () => {
  const root = path.resolve(os.tmpdir(), 'figforge-workspace');

  it('allows the root itself and nested subdirs', () => {
    assert.equal(resolveAndValidateOutputPath('.', root), root);
    assert.equal(resolveAndValidateOutputPath('a/b', root), path.join(root, 'a', 'b'));
  });

  it('rejects "../" escapes', () => {
    assert.throws(() => resolveAndValidateOutputPath('../escape', root), /Refusing to write outside/);
  });

  it('rejects absolute paths outside the root', () => {
    assert.throws(() => resolveAndValidateOutputPath('/etc', root), /Refusing to write outside/);
  });

  it('rejects sibling dirs that merely share the root as a name prefix', () => {
    // /tmp/figforge-workspace-evil must NOT pass as inside /tmp/figforge-workspace.
    assert.throws(() => resolveAndValidateOutputPath('../figforge-workspace-evil', root), /Refusing to write outside/);
  });
});

describe('executeExportUnity asset-name sandboxing', () => {
  let base: string; // temp throwaway containing the workspace root
  let root: string; // the validated workspace root (== bridge cwd)

  beforeEach(async () => {
    base = await mkdtemp(path.join(os.tmpdir(), 'figforge-test-'));
    root = path.join(base, 'workspace');
    await mkdir(root);
  });
  afterEach(async () => {
    await rm(base, { recursive: true, force: true });
  });

  it('flattens a "../"-bearing asset name so it cannot escape the output dir', async () => {
    // From root/out, "../../evil.png" would resolve to base/evil.png — an escape.
    const sender = senderWithAssets([{ name: '../../evil.png', data: [1, 2, 3] }]);
    const res = await executeExportUnity(sender, 'node-1', 'out', root);

    assert.equal(res.assetCount, 1);
    assert.ok(existsSync(path.join(root, 'out', 'evil.png')), 'asset must land inside the output dir');
    assert.ok(!existsSync(path.join(base, 'evil.png')), 'asset must NOT escape the workspace root');
    assert.ok(!existsSync(path.join(root, 'evil.png')), 'asset must NOT escape the output dir');
  });

  it('skips "." and ".." asset names instead of writing the parent dir', async () => {
    const sender = senderWithAssets([
      { name: '..', data: [1] },
      { name: '.', data: [2] },
    ]);
    const res = await executeExportUnity(sender, 'node-1', 'out', root);
    assert.equal(res.assetCount, 0);
  });

  it('writes a normal asset name unchanged', async () => {
    const sender = senderWithAssets([{ name: 'icon.png', data: [9] }]);
    const res = await executeExportUnity(sender, 'node-1', 'out', root);
    assert.equal(res.assetCount, 1);
    assert.ok(existsSync(path.join(root, 'out', 'icon.png')));
  });
});
