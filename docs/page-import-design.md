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
| `Sw` `Switch` | switch | `FigForgeSwitch` | `Toggle` |
| `Inp` `Field` | input | `TMP_InputField` | `TextField` |
| `Step` `Stepper` `Num` | stepper | `FigForgeStepper` | `FloatField` |
| `Drp` `Select` | dropdown | `TMP_Dropdown` | `DropdownField` |
| `Sld` | slider | `Slider` | `Slider` |
| `Dialog` `Modal` `Dlg` | modal | `FigForgeModal` | `VisualElement` |
| `Toast` `Notification` | toast | `FigForgeToastHost` | `VisualElement` |

## 2. Manifest additions

- `canonical.kind` ∈ button | toggle | switch | input | stepper | dropdown | slider | progress | list | table | modal | toast
- `canonical.value?` — initial state (toggle/switch on/off, slider/stepper value, input text) when detectable
- `canonical.placeholder?` — placeholder text for input fields
- `canonical.options?` — string[] for dropdowns (from Figma list children; heuristic)
- `canonical.body?`, `primaryLabel?`, `secondaryLabel?` — modal/toast copy and dialog action labels
- `canonical.severity?`, `position?`, `duration?` — toast variant, stack location, and auto-dismiss seconds
- `element.nav?` — `{ target: "<screenName>", trigger: "click" }` from Figma prototype reactions (data only)

## 3. Bundle formats (both supported)

- **Project bundle (default for Export Page):** a `project.json` index + one folder per screen.
  ```jsonc
  {
    "schema": "figforge/project",
    "version": "2.0",
    "generator": "FigForge",
    "name": "<page>",
    "exportedAt": "<ISO timestamp>",
    "initial": "Home",
    "screens": [
      {
        "name": "Home",
        "manifest": "Home/manifest.json",
        "section": "App Shell",
        "role": "screen"
      },
      {
        "name": "App Shell",
        "manifest": "App_Shell/manifest.json",
        "section": "App Shell",
        "role": "shell"
      }
    ]
  }
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

## 6. Shell scaffolds

The plugin's **+ Shell** tool creates the recommended shell shape automatically:

```text
Section "App Shell"
├─ Frame "App Shell"
│  ├─ Header
│  ├─ Nav
│  └─ Frame "Content"
├─ Frame "Home"
├─ Frame "Inventory"
├─ Frame "Settings"
└─ Frame "Profile"
```

The Section names the shell scope. The frame inside it is the actual showable
shell frame and should usually share the Section's name. `Content` is the mount
slot. The sample screens are sibling frames in the same Section so the Unity
importer mounts them into the shell's `Content`; frames outside the Section
import as normal full-screen pages.
Each Section may have its own `Shell`, so one exported page can mix standalone
screens with multiple independent shell groups.
Shell frames are detected by the `role=shell` plugin tag, by the exact name
`Shell`, or by names that start/end with `Shell` as a separate word/token
(`Shell Main`, `App Shell`, `Inventory_Shell`).

## 7. Registry

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
