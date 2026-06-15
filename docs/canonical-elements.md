# Canonical UI Elements

Define a control **once** and reference it by name across every Figma page.
FigForge can generate reusable uGUI controls for buttons, toggles/radios,
switches, input fields, steppers, dropdowns, sliders, progress bars, lists,
tables, dialogs/modals, and toast notifications.

## The naming convention

```
<Kind>_<instanceName>_<canonicalRef>
```

| Token | Example | Meaning |
|------|---------|---------|
| `<Kind>` | `Btn` | Kind tag (case-insensitive, e.g. `Btn`, `Tgl`, `Sw`, `Inp`, `Step`, `Drp`). |
| `<instanceName>` | `Save` | This element's design-specific name (may contain `_`). |
| `<canonicalRef>` | `PrimaryButton` | The **last** token — the canonical definition to instantiate. |

Examples:

| Figma layer name | kind | instance | ref |
|------------------|------|----------|-----|
| `Btn_Save_PrimaryButton` | button | Save | PrimaryButton |
| `Btn_Cancel_Secondary` | button | Cancel | Secondary |
| `Button_NextStep_Primary` | button | NextStep | Primary |
| `Inp_Email_InputField` | input | Email | InputField |
| `Sw_AirplaneMode_Switch` | switch | AirplaneMode | Switch |
| `Step_Quantity_Stepper` | stepper | Quantity | Stepper |
| `Dialog_DeleteConfirm_Dialog` | modal | DeleteConfirm | Dialog |
| `Toast_SaveSuccess_Toast` | toast | SaveSuccess | Toast |

## Radio groups

Radio buttons are mutually exclusive inside a group. To make a radio set, select
the related radio instances in Figma and group them with `Cmd+G` / `Ctrl+G`, or
place them under the same frame. FigForge groups radios that share the same
Unity parent.

The plugin records `canonical: { kind, ref, instanceName, label }` on the element
(the `label` is the first text found inside the layer, else the instance name)
and lists distinct refs in `manifest.canonicalRefs`.

## Component variants

When a canonical layer is a Figma component instance, FigForge reads structured
component variant metadata before falling back to layer names and visibility.
Supported normalized axes include:

| Axis | Common aliases / values |
|------|--------------------------|
| `state` | `Default`, `Regular`, `Hover`, `Rollover`, `Pressed`, `Active`, `Selected`, `Disabled`, `Focused` |
| `value` | `On`, `Off`, `true`, `false`, `Checked`, `Unchecked`, selected/current option names |
| `size` | `XS`, `S`, `M`, `L`, `XL`, plus arbitrary designer-defined strings |
| `tone`, `intent`, `severity` | `Primary`, `Secondary`, `Success`, `Warning`, `Error`, `Info`, `Danger` |

The manifest includes `canonical.variantProps` with normalized axes plus the
original Figma names/values for debugging. Toggle, radio, and switch initial
values prefer `value`/`state` variants; dropdown/list/table selected/current
values prefer variant metadata when present; toast severity prefers
`severity`/`intent`/`tone`; and buttons can capture component-set state variants
as normal/hover/pressed state visuals.

Existing designer-friendly heuristics remain active: `Regular`, `Rollover`,
`Pressed`, `Selected`, `Checkmark`, `Fill`, component tag payloads, and visible
layer state still drive capture when structured variant metadata is absent or
incomplete. Variant decisions are summarized under `variantExtraction`
diagnostics and in `canonical.variantProps.diagnostics`.

## Wiring it up in Unity

1. **Create ▸ FigForge ▸ Canonical Library** — a `CanonicalLibrary` asset.
2. Add an entry per ref: `referenceName = "PrimaryButton"`, `prefab = your control prefab`.
   - Input prefabs should carry a `TMP_InputField`; `FigForgeBindings.label`
     is used as the placeholder, and `valueText` is used for the editable text.
   - Dialog prefabs should carry `FigForgeModal` with Backdrop, Panel, Title,
     Body, Actions, Primary/Secondary, and Close parts wired.
   - Toast prefabs should carry `FigForgeToastHost`; runtime calls use
     `Toasts.Success("Saved")`, `Toasts.Error(...)`, or `Toasts.Show(data)`.
3. In **Window ▸ FigForge ▸ Importer**, open **Canonical elements**, assign the
   library. Each ref shows ✓ (resolved) or ✗ (missing).
4. **Build**. For each canonical layer, FigForge instantiates the prefab, applies
   the layer's `RectTransform`, and stamps the label — instead of rebuilding from
   a PNG/text.

If a ref isn't found in the library, FigForge generates a prefab from the Figma
component when it has enough captured structure; otherwise it drops in a labelled
purple placeholder and logs a warning, so the layout still works.

## Why

- One source of truth for shared controls — restyle the prefab, every page updates.
- Real, interactive Unity prefabs (with your scripts) instead of flat images.
- Designs stay declarative: the Figma name *is* the binding.

## Extending to new kinds

`CanonicalLibrary.Resolve(kind, ref)` switches on `kind`. Add a new kind tag in
the plugin's `naming.ts` `KIND_TAGS`, a manifest capture branch, and a Unity
builder branch for new control families.
