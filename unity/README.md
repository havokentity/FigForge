# FigForge Unity Importer

Imports a FigForge export (`manifest.json` + PNGs) into a Unity uGUI hierarchy.

## Install

- **Package Manager → + → Add package from disk…** and pick this folder's `package.json`, or
- copy the folder into your project's `Packages/`.

Dependencies (auto-resolved): uGUI, TextMeshPro, Newtonsoft JSON, 2D Sprite.

## Use

1. **Window ▸ FigForge ▸ Importer** → **Import a .zip…** and pick a FigForge export
   (it extracts into `Assets/FigForge/Imports/<name>/` for you).
   *Or* drop an already-extracted folder under `Assets/`, or write one with the
   MCP `export_unity` tool, then **Rescan**.
2. Pick the manifest, configure, **Build**.

## What it builds

- Constraint-driven `RectTransform` anchors (stretch / pin / proportional), not everything centered.
- Sprite images, **solid + gradient fills**, **real stroke borders**, **rotation**, and procedural **rounded panels** for fill-only rounded containers.
- `TextMeshProUGUI` text with per-family/style font mapping.
- **Canonical UI elements**: a layer named `Btn_<instance>_<ref>` becomes an instance of the matching prefab in your assigned **Canonical Library** (Create ▸ FigForge ▸ Canonical Library). Buttons are supported today.
- **Connected scenes**: each imported frame becomes a `BaseScreen` under a shared `Canvas` + `ScreenManager`, so several pages live in one navigable scene.
