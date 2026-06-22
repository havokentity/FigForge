#!/usr/bin/env node
// Asserts the canonical-control capture generation stays in lockstep between the
// plugin (emitter) and the Unity importer (parser). The importer only WARNS at
// import time when they differ, so without this gate the two silently drift and
// every export trips the degradation warning. Counterparts:
//   plugin/src/types.ts            export const CANONICAL_SCHEMA = N
//   unity/Editor/HierarchyBuilder.cs   internal const int CanonicalSchema = N
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const repoRoot = join(dirname(fileURLToPath(import.meta.url)), '..');

function extract(relPath, regex, label) {
  const text = readFileSync(join(repoRoot, relPath), 'utf8');
  const m = text.match(regex);
  if (!m) {
    console.error(`✗ could not find ${label} in ${relPath}`);
    process.exit(1);
  }
  return { value: Number(m[1]), relPath };
}

const plugin = extract(
  'plugin/src/types.ts',
  /export const CANONICAL_SCHEMA\s*=\s*(\d+)/,
  'CANONICAL_SCHEMA',
);
const unity = extract(
  'unity/Editor/HierarchyBuilder.cs',
  /const int CanonicalSchema\s*=\s*(\d+)/,
  'CanonicalSchema',
);

if (plugin.value !== unity.value) {
  console.error(
    `✗ canonical schema drift: plugin CANONICAL_SCHEMA = ${plugin.value} ` +
      `(${plugin.relPath}) != Unity CanonicalSchema = ${unity.value} (${unity.relPath}).\n` +
      `  Bump both in lockstep so exports don't trip the importer degradation warning.`,
  );
  process.exit(1);
}

console.log(`✓ canonical schema in lockstep: ${plugin.value}`);
