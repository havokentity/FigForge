# Page Import & Canonical Binding — design

Import a whole Figma page into a connected, navigable Unity scene (uGUI or UI
Toolkit) where every recognised control is a real, configured component with
labels/options/values bound and screens linked. Behaviour (`onClick`) is **not**
wired — only structure, references, and navigation *data*.

## 1. Naming convention

`<Kind>_<instance>_<ref>` — first token = kind tag, last = canonical ref, middle = instance name.

| Tag(s) | kind | uGUI | UITK |
|---|---|---|---|
| `Btn` `Button` | button | `Button` | `Button` |
| `Tgl` `Chk` | toggle | `Toggle` | `Toggle` |
| `Inp` `Field` | input | `TMP_InputField` | `TextField` |
| `Drp` `Select` | dropdown | `TMP_Dropdown` | `DropdownField` |
| `Sld` | slider | `Slider` | `Slider` |

## 2. Manifest additions

- `canonical.kind` ∈ button | toggle | input | dropdown | slider
- `canonical.value?` — initial state (toggle on/off, slider value, input text) when detectable
- `canonical.options?` — string[] for dropdowns (from Figma list children; heuristic)
- `element.nav?` — `{ target: "<screenName>", trigger: "click" }` from Figma prototype reactions (data only)

## 3. Bundle formats (both supported)

- **Project bundle (default for Export Page):** a `project.json` index + one folder per screen.
  ```jsonc
  { "schema": "figforge/project", "name": "<page>", "initial": "Home",
    "screens": [ { "name": "Home", "manifest": "Home/manifest.json" }, … ] }
  ```
- **Single frame (existing):** a lone `manifest.json` + PNGs.

Importer auto-detects: `project.json` → **Build Page** (all screens under one
`ScreenManager` / `UIDocument`); lone `manifest.json` → **Build Frame**.

## 4. Binding — `FigForgeBindings` (Runtime component on canonical prefabs)

Named slots the importer fills so prefab internals stay free to change:
`label` (TMP_Text) · `icon` (Image) · `background` (Graphic) · `control`
(Selectable) · `optionsTarget` (TMP_Dropdown) · `valueText` (TMP_Text).
Importer sets label/icon/options/initial value — **no callbacks**. Falls back to
child-name matching if the component is absent.

## 5. Navigation — captured, not executed

A passive `FigForgeNavLink { targetScreen }` is attached wherever Figma had a
"Navigate to" reaction. Nothing listens yet; a later `FigForgeNavBinder` turns
every link into `ScreenManager.Show(target)` in one place.

## 6. Registry

Each screen root carries a `FigForgeScreen` with `Get<T>("instanceName")` so code
fetches controls by Figma name instead of walking the hierarchy.

## Known heuristics / caveats

- Initial toggle/slider state read from component variant state names where
  possible, else default. Dropdown options from text children (heuristic).
- Prototype capture covers "Navigate To" reactions; needs a page-wide
  frameId→name map (resolved at page-export time / via the destination node name).
- Canonical prefabs per kind must exist in the `CanonicalLibrary`, else
  placeholders + warnings.

## Build order

1. Canonical kinds + `FigForgeBindings` (uGUI)
2. Project bundle: plugin **Export Page** + importer **Build Page** (both backends)
3. Navigation capture → `FigForgeNavLink`
4. UITK parity for kinds + nav
5. `FigForgeScreen` registry
