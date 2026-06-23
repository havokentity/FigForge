#!/usr/bin/env node
// Asserts the manifest CONTRACT stays in lockstep between the plugin (emitter)
// and the Unity importer (parser). The importer only WARNS (or, for the version,
// hard-fails at import time) when they differ, so without this gate the two
// silently drift. Three checks, each with its counterpart pair:
//   1. Canonical-control capture generation:
//        plugin/src/types.ts                export const CANONICAL_SCHEMA = N
//        unity/Editor/HierarchyBuilder.cs   internal const int CanonicalSchema = N
//   2. Manifest wire-format version — the importer hard-fails on a version it
//      doesn't accept, so whatever the plugin emits MUST be in the supported set:
//        plugin/src/types.ts            export const MANIFEST_VERSION = 'X'
//        unity/Editor/ManifestParser.cs static readonly string[] SupportedManifestVersions = { … }
//   3. Best-effort top-level field-name parity between the two contract types:
//        plugin/src/types.ts            export interface Manifest { … }
//        unity/Editor/Data/ManifestData.cs   public class Manifest { … }
//
// NOTE on (3): this is a REGEX-based, structural check, not a real parser. It only
// looks at the immediate field names of the two Manifest types — it does NOT
// compare nested types, field types, optionality, or ordering. It exists to catch
// the obvious "someone added a top-level field on one side and forgot the other"
// divergence; a deep contract verification still needs a round-trip integration
// test. Treat a parity failure as a loud signal to look, not as proof of (in)correctness.
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const repoRoot = join(dirname(fileURLToPath(import.meta.url)), '..');

let failed = false;
function fail(msg) {
  console.error(msg);
  failed = true;
}

function read(relPath) {
  return readFileSync(join(repoRoot, relPath), 'utf8');
}

function matchOrExit(text, regex, label, relPath) {
  const m = text.match(regex);
  if (!m) {
    console.error(`✗ could not find ${label} in ${relPath}`);
    process.exit(1);
  }
  return m;
}

// ---------------------------------------------------------------------------
// (1) Canonical schema generation — both sides must carry the same integer.
// ---------------------------------------------------------------------------
const typesTs = read('plugin/src/types.ts');
const hierarchyCs = read('unity/Editor/HierarchyBuilder.cs');

const pluginSchema = Number(
  matchOrExit(typesTs, /export const CANONICAL_SCHEMA\s*=\s*(\d+)/, 'CANONICAL_SCHEMA', 'plugin/src/types.ts')[1],
);
const unitySchema = Number(
  matchOrExit(hierarchyCs, /const int CanonicalSchema\s*=\s*(\d+)/, 'CanonicalSchema', 'unity/Editor/HierarchyBuilder.cs')[1],
);

if (pluginSchema !== unitySchema) {
  fail(
    `✗ canonical schema drift: plugin CANONICAL_SCHEMA = ${pluginSchema} ` +
      `(plugin/src/types.ts) != Unity CanonicalSchema = ${unitySchema} (unity/Editor/HierarchyBuilder.cs).\n` +
      `  Bump both in lockstep so exports don't trip the importer degradation warning.`,
  );
} else {
  console.log(`✓ canonical schema in lockstep: ${pluginSchema}`);
}

// ---------------------------------------------------------------------------
// (2) Manifest version — the plugin emits exactly one version; the importer
// hard-fails on anything outside SupportedManifestVersions. So the plugin's
// MANIFEST_VERSION MUST appear in the importer's supported list (the list may
// be a superset — it can still accept older manifests).
// ---------------------------------------------------------------------------
const parserCs = read('unity/Editor/ManifestParser.cs');

const pluginVersion = matchOrExit(
  typesTs,
  /export const MANIFEST_VERSION\s*=\s*'([^']+)'/,
  'MANIFEST_VERSION',
  'plugin/src/types.ts',
)[1];

const supportedDecl = matchOrExit(
  parserCs,
  /SupportedManifestVersions\s*=\s*\{([^}]*)\}/,
  'SupportedManifestVersions',
  'unity/Editor/ManifestParser.cs',
)[1];
const supportedVersions = [...supportedDecl.matchAll(/"([^"]+)"/g)].map((m) => m[1]);

if (!supportedVersions.includes(pluginVersion)) {
  fail(
    `✗ manifest version drift: plugin emits MANIFEST_VERSION = '${pluginVersion}' ` +
      `(plugin/src/types.ts) but the importer's SupportedManifestVersions = ` +
      `[${supportedVersions.map((v) => `'${v}'`).join(', ')}] (unity/Editor/ManifestParser.cs) ` +
      `does NOT include it — every import would hard-fail with an "unsupported version" abort.\n` +
      `  Add '${pluginVersion}' to SupportedManifestVersions (and keep older entries for back-compat).`,
  );
} else {
  console.log(
    `✓ manifest version accepted: plugin emits '${pluginVersion}', ` +
      `importer supports [${supportedVersions.map((v) => `'${v}'`).join(', ')}]`,
  );
}

// ---------------------------------------------------------------------------
// (3) Best-effort top-level field-name parity of the two Manifest contract types.
// Regex-scraped, intentionally shallow — see the NOTE at the top of the file.
// ---------------------------------------------------------------------------

// TS: pull the `export interface Manifest { … }` body, then each `name:` key.
function tsManifestFields(text) {
  const body = matchOrExit(
    text,
    /export interface Manifest\s*\{([\s\S]*?)\n\}/,
    'export interface Manifest',
    'plugin/src/types.ts',
  )[1];
  const fields = new Set();
  for (const line of body.split('\n')) {
    // Strip line comments so a `// foo: bar` note can't masquerade as a field.
    const code = line.replace(/\/\/.*$/, '');
    const m = code.match(/^\s*([A-Za-z_]\w*)\??\s*:/);
    if (m) fields.add(m[1]);
  }
  return fields;
}

// C#: pull the `public class Manifest { … }` body, then each `public <type> name`
// field declaration (drops a leading [JsonProperty(...)] attribute if present).
function csManifestFields(text) {
  const body = matchOrExit(
    text,
    /public class Manifest\s*\{([\s\S]*?)\n\s{4}\}/,
    'public class Manifest',
    'unity/Editor/Data/ManifestData.cs',
  )[1];
  const fields = new Set();
  for (const line of body.split('\n')) {
    const code = line.replace(/\/\/.*$/, '');
    // public <Type[, generics]> <name> [= …];  — capture the last identifier
    // before the terminator (= or ;).
    const m = code.match(/^\s*public\s+.+?\b([A-Za-z_]\w*)\s*(?:=|;)/);
    if (m) fields.add(m[1]);
  }
  return fields;
}

const manifestDataCs = read('unity/Editor/Data/ManifestData.cs');
const tsFields = tsManifestFields(typesTs);
const csFields = csManifestFields(manifestDataCs);

const onlyTs = [...tsFields].filter((f) => !csFields.has(f));
const onlyCs = [...csFields].filter((f) => !tsFields.has(f));

if (onlyTs.length || onlyCs.length) {
  // Loud warning, then fail: a top-level field on one side but not the other is
  // the exact "added a field, forgot the other side" divergence this guards.
  fail(
    `✗ Manifest top-level field parity mismatch between the TS interface ` +
      `(plugin/src/types.ts) and the C# class (unity/Editor/Data/ManifestData.cs):\n` +
      (onlyTs.length ? `  only in TS:  ${onlyTs.join(', ')}\n` : '') +
      (onlyCs.length ? `  only in C#:  ${onlyCs.join(', ')}\n` : '') +
      `  Add the missing field to the other side so the wire contract stays mirrored.\n` +
      `  (This is a shallow regex check — top-level field NAMES only, not types/nesting.)`,
  );
} else {
  console.log(`✓ Manifest top-level fields in parity (${tsFields.size}): ${[...tsFields].join(', ')}`);
}

// ---------------------------------------------------------------------------
// (4) CanonicalKind coverage — every value in the TS `CanonicalKind` union MUST
// be parseable by the C# importer. The importer accepts a kind only if
// CanonicalLibrary.TryParseKind has a matching (lowercased) `case "…":` label
// (this covers both the enum names and aliases like "dialog"/"notification").
// So the set of TryParseKind labels MUST be a SUPERSET of the TS union — adding
// a TS kind without wiring TryParseKind would make those instances silently
// fail to resolve their prefab, and this gate catches it.
// Best-effort & intentionally loose: it only checks that each TS literal has a
// C# case; it does NOT require the two sides to match exactly (C# may accept
// extra aliases the TS union doesn't list).
// ---------------------------------------------------------------------------
const canonicalLibraryCs = read('unity/Runtime/CanonicalLibrary.cs');

// TS: `export type CanonicalKind = 'a' | 'b' | …;` → the quoted literals.
const tsUnionDecl = matchOrExit(
  typesTs,
  /export type CanonicalKind\s*=\s*([^;]+);/,
  'CanonicalKind union',
  'plugin/src/types.ts',
)[1];
const tsKinds = [...tsUnionDecl.matchAll(/'([^']+)'/g)].map((m) => m[1].toLowerCase());

// C#: every `case "label":` in the file (TryParseKind is the only switch with
// string cases here). Lowercased to mirror TryParseKind's ToLowerInvariant().
const csKindCases = new Set(
  [...canonicalLibraryCs.matchAll(/case\s+"([^"]+)"\s*:/g)].map((m) => m[1].toLowerCase()),
);

const missingKinds = tsKinds.filter((k) => !csKindCases.has(k));
if (missingKinds.length) {
  fail(
    `✗ CanonicalKind coverage gap: the TS union (plugin/src/types.ts) lists kind(s) ` +
      `[${missingKinds.map((k) => `'${k}'`).join(', ')}] that CanonicalLibrary.TryParseKind ` +
      `(unity/Runtime/CanonicalLibrary.cs) does NOT accept — those instances would fail to ` +
      `resolve their prefab.\n` +
      `  Add a matching 'case "…":' to TryParseKind (it lowercases input, so use the lowercase form).`,
  );
} else {
  console.log(`✓ CanonicalKind coverage: all ${tsKinds.length} TS kinds parseable by TryParseKind`);
}

process.exit(failed ? 1 : 0);
