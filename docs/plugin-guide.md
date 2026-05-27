# FigForge Plugin Guide

The plugin runs in **Figma Desktop** (it needs `exportAsync`, unavailable in the
browser). Build with `npm run build` in `plugin/`, then import
`plugin/manifest.json` via **Plugins → Development → Import plugin from manifest…**

## Window

It's a single page — no tabs.

- **Header** — logo, version, window size presets (S / M / L), the **MCP toggle** (a connect/disconnect control with a live status dot: grey = off, amber = connecting, green = connected), and minimize.
- **Body** — toolbar (scale + options), layer tree (left), live preview (right), and the export button.

Click the MCP control to start/stop the bridge connection; while on, it
auto-reconnects, so the dot goes green as soon as the bridge server is up.

## Export options

| Control | Effect |
|--------|--------|
| Scale | `0.5×`–`4×`, or fixed `512w` / `1024w` / `1024h`. Drives PNG resolution + reference resolution. |
| Auto-merge | Locked containers flatten into a single PNG automatically. |
| Gradients | Emit gradient fills into the manifest (off → first solid stop only). |
| Raster strokes | Bake strokes into PNGs instead of emitting stroke data. |

## Per-layer controls (tree)

- 👁 **Exclude** — drop the layer (and subtree) from the export.
- ⊞ **Merge** — flatten this container + its children into one PNG.
- **P** (text only) — rasterize a text layer as a PNG instead of `TextMeshProUGUI`.
- A pill on the row shows a **canonical ref** when the layer name matches `Btn_<instance>_<ref>`.

## How layers are treated

| Figma | Unity |
|------|------|
| Frame / Group / Component with a fill or stroke | `Image` (sprite or procedural fill) |
| Pure container (no fill/stroke) | structural `RectTransform` only |
| Vector / shape / icon group | rasterized PNG → `Image` |
| Text | `TextMeshProUGUI` (or PNG if forced) |
| `Btn_<instance>_<ref>` | canonical prefab instance |

## Naming & sanitization

Layer names are sanitized to `snake_case` for filenames and GameObject names
(`Save Button` → `save_button`). PNG files are `<root>_<layer>@<scale>x.png` and
deduplicated by content hash.

## Manifest field reference

Root: `schema`, `version`, `generator`, `exportedAt`, `screen`, `elements[]`,
`assets[]`, `fonts[]`, `canonicalRefs[]`.

`screen`: `{ id, name, figmaSize{w,h}, referenceResolution{w,h}, exportScale }`.

Each `elements[]` entry:

| Field | Meaning |
|------|---------|
| `id`, `name`, `displayName`, `type`, `parentId` | identity + hierarchy |
| `rect` `{x,y,w,h}` | parent-local Figma rect |
| `rotation` | degrees |
| `transform` | `anchorMin/anchorMax/pivot/offsetMin/offsetMax/rotationZ` (Unity-space, reference px) |
| `components[]` | e.g. `Image`, `TextMeshProUGUI`, `Button` |
| `style` | `opacity`, `cornerRadius`, `corners[4]`, `fill`, `stroke` |
| `fill` | `{kind:"solid",color}` \| `{kind:"gradient",gradient,stops[]}` \| `{kind:"image",asset}` |
| `stroke` | `{color, weight, align, dashed}` |
| `text` | content, font family/style/size, color, alignment, spacing |
| `asset` / `assetBounds` | PNG filename + pixel size |
| `canonical` | `{kind, ref, instanceName, label}` |
| `interactive`, `clipsContent`, `merged` | flags |
| `autoLayout` | mode/padding/spacing (applied as layout group) |
| `children[]` | child element ids |

The Unity `ManifestData` C# model mirrors this 1:1.
