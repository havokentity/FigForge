# Releasing

How a FigForge release is cut, what CI does, and how the three packages are versioned.

## TL;DR — releases are cut from tags

Push a `vX.Y.Z` tag and the **Release** workflow builds everything, packages the
three install artifacts, and publishes a GitHub Release with auto-generated notes.
Nothing is uploaded by hand.

```bash
# on a clean, green main — after bumping versions (see below)
git tag -a vX.Y.Z -m "FigForge vX.Y.Z — <summary>"
git push origin vX.Y.Z          # this is what triggers the release
```

> Don't run `gh release create` yourself — the tag push already triggers the
> workflow, and a manual release collides with it.

## The two workflows

| Workflow | File | Trigger | What it does |
|---|---|---|---|
| **CI** | [`.github/workflows/ci.yml`](../.github/workflows/ci.yml) | push to `main`, any PR, manual | typecheck + build the plugin and server |
| **Release** | [`.github/workflows/release.yml`](../.github/workflows/release.yml) | push tag `v*`, or manual `workflow_dispatch` with a `tag` input | build → package 3 artifacts → publish the GitHub Release |

Both run on `ubuntu-latest`, Node 22 (≥ 22.6 — the server's `npm test` runs the
`.ts` test files directly via Node's native type stripping), installing with
`npm ci`. The Release job
rebuilds from source, so `dist/` staying gitignored is fine — nothing committed is
trusted into the artifacts.

## What a release publishes

| Artifact | Built from | How a user installs it |
|---|---|---|
| `figforge-plugin-<tag>.zip` | `plugin/manifest.json` + `plugin/dist` | Figma Desktop → *Plugins → Development → Import plugin from manifest…* |
| `figforge-bridge-<tag>.zip` | `server/dist` + `package.json` + lockfile | unzip → `npm install --omit=dev` → point the MCP client at `dist/index.js` |
| `figforge-unity-importer-<tag>.tgz` | `npm pack` in `unity/` | Unity → Package Manager → *Add package from tarball…* |

Notes are auto-generated (`generate_release_notes: true`) and prefixed with the
Install block defined in `release.yml`. The Release is published immediately (not a
draft) by `github-actions[bot]`.

## Versioning — three packages, one product tag

- **`plugin/package.json`** and **`unity/package.json`** track the *product* version
  and match the tag (tag `v1.0.41` ⇒ both at `1.0.41`). The plugin version is
  injected into the UI bundle at build time via `__FIGFORGE_VERSION__`.
- **`server/package.json`** (`figforge-bridge`) moves on its **own** cadence — bump
  it only when the bridge changes, so it normally lags the tag. That drift is
  expected, not a mistake.

The workflow takes the version straight from the tag name (`GITHUB_REF_NAME`), so
the tag is the source of truth; the `package.json` bumps are for the artifacts'
internal metadata.

## Cutting a release, step by step

1. Land everything on `main`; confirm **CI** is green.
2. Bump versions:
   - `plugin/package.json` + `unity/package.json` → the new `X.Y.Z`.
   - `server/package.json` → its next patch **only if** the bridge changed.
3. Commit: `release: <summary> (vX.Y.Z)`.
4. Annotated tag: `git tag -a vX.Y.Z -m "FigForge vX.Y.Z — <summary>"`.
5. Push branch then tag: `git push origin main && git push origin vX.Y.Z`.
6. Watch it land: `gh run watch` then `gh release view vX.Y.Z`.

### Re-running a release for an existing tag

Use the Release workflow's **Run workflow** button (`workflow_dispatch`) and pass the
tag, or delete and re-push the tag:

```bash
git push --delete origin vX.Y.Z && git push origin vX.Y.Z
```

## Conventions

- Tags are **annotated**, subject line `FigForge vX.Y.Z — <summary>`.
- Release commit subject: `release: <summary> (vX.Y.Z)`.
