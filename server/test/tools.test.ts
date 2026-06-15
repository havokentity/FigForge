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

import { resolveAndValidateOutputPath, executeExportUnity, validateManifest } from '../dist/tools.js';
// Imported from src (not dist) deliberately: the regex is the thing under
// test, and node's native type stripping runs the erasable-only schema.ts
// as-is — so the test can't silently pass against a stale compiled copy.
import { figmaNodeId } from '../src/schema.ts';
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

describe('figmaNodeId schema', () => {
  it('accepts plain "num:num" ids', () => {
    assert.equal(figmaNodeId.safeParse('123:456').success, true);
    assert.equal(figmaNodeId.safeParse('0:1').success, true);
  });

  it('accepts instance-descendant path ids ("I" + ";"-separated segments)', () => {
    assert.equal(figmaNodeId.safeParse('I123:4;567:8').success, true);
    assert.equal(figmaNodeId.safeParse('I1:2;3:4;5:6').success, true);
    assert.equal(figmaNodeId.safeParse('I123:4').success, true); // single segment
  });

  it('rejects malformed ids', () => {
    const bad = [
      '123-456',      // hyphen, not colon
      '123:4;567:8',  // path segments without the "I" prefix
      'I123:4;',      // trailing separator
      'I;123:4',      // empty first segment
      'I123',         // no colon pair
      '',
    ];
    for (const id of bad) {
      assert.equal(figmaNodeId.safeParse(id).success, false, `should reject "${id}"`);
    }
  });
});

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

  it('forwards export scale, options, and element configs to the plugin', async () => {
    const sent: { tool?: string; nodeIds?: string[]; params?: Record<string, unknown> } = {};
    const sender: PluginSender = {
      async send(tool, nodeIds, params): Promise<RpcResponse> {
        sent.tool = tool;
        sent.nodeIds = nodeIds;
        sent.params = params;
        return {
          data: { exports: [{ manifest: { screen: { name: 's' }, elements: [] }, assets: [] }] },
        };
      },
    };
    const params = {
      scale: { type: 'width', value: 1024 },
      options: { autoMerge: false, rasterizeStrokes: true, fontFaceDilate: 0.25 },
      elementConfigs: [
        { id: '123:456', excluded: true },
        { id: '123:789', merged: true, rasterize: true },
      ],
    };

    await executeExportUnity(sender, '123:111', 'out', root, params);

    assert.equal(sent.tool, 'export_unity');
    assert.deepEqual(sent.nodeIds, ['123:111']);
    assert.deepEqual(sent.params, params);
  });
});

describe('validateManifest canonical variant contract', () => {
  it('accepts canonical.variantProps and variantExtraction diagnostics', () => {
    const manifest = {
      schema: 'figforge/manifest',
      version: '2.0',
      generator: 'FigForge',
      exportedAt: '2026-06-15T00:00:00.000Z',
      screen: {
        id: '1:1',
        name: 'screen',
        displayName: 'Screen',
        figmaSize: { w: 100, h: 100 },
        referenceResolution: { w: 200, h: 200 },
        exportScale: 2,
      },
      assets: [],
      elements: [{
        id: '1:2',
        name: 'toggle',
        displayName: 'Tgl_Alerts_Toggle',
        type: 'INSTANCE',
        parentId: null,
        rect: { x: 0, y: 0, w: 100, h: 40 },
        transform: {
          anchorMin: [0, 0],
          anchorMax: [0, 0],
          pivot: [0.5, 0.5],
          offsetMin: [0, 0],
          offsetMax: [100, 40],
          rotationZ: 0,
        },
        components: [],
        children: [],
        canonical: {
          kind: 'toggle',
          ref: 'Toggle',
          instanceName: 'Alerts',
          variantProps: {
            axes: { value: 'on', state: 'selected' },
            original: { value: 'Checked', state: 'Selected' },
            raw: [
              {
                axis: 'value',
                value: 'on',
                originalName: 'Checked',
                originalValue: 'true',
                source: 'componentProperties',
                type: 'BOOLEAN',
              },
            ],
          },
        },
        interactive: true,
        clipsContent: false,
        merged: false,
      }],
      fonts: [],
      diagnostics: {
        summary: { variantExtraction: 1 },
        issues: [{
          category: 'variantExtraction',
          severity: 'info',
          message: 'Variant axes found: value=on.',
        }],
      },
      settings: { fontFaceDilate: 0.15 },
      canonicalRefs: ['Toggle'],
    };

    const result = validateManifest(manifest);
    assert.deepEqual(result.errors, []);
  });
});
