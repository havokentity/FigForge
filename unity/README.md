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

## Backends

Pick one with the **UI backend** dropdown:

- **uGUI** — GameObjects with `RectTransform` + `Image`/`TextMeshProUGUI` under a `Canvas`.
- **UI Toolkit** — a generated `.uxml` + `.uss` (absolute/stretch layout, native border/rounded corners, baked gradients, image backgrounds, `<Button>` for canonical layers). Optionally drops a `UIDocument` + `PanelSettings` into the scene; multi-page pages live under a `UIScreenManager`.

## What it builds (uGUI)

- Constraint-driven `RectTransform` anchors (stretch / pin / proportional), not everything centered.
- Sprite images, **solid + gradient fills**, **real stroke borders**, **rotation**, and procedural **rounded panels** for fill-only rounded containers.
- `TextMeshProUGUI` text with per-family/style font mapping.
- Bundled Inter static faces auto-generate project-local TMP font assets when an import requests Inter and the project does not already provide that weight.
- **Canonical UI elements**: layers like `Btn_<instance>_<ref>` and `Inp_<instance>_<ref>` become generated or library-backed uGUI controls through your **Canonical Library** (Create ▸ FigForge ▸ Canonical Library).
- **Connected scenes**: each imported frame becomes a `BaseScreen` under a shared `Canvas` + `ScreenManager`, so several pages live in one navigable scene.
