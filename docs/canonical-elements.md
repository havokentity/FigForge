# Canonical UI Elements

Define a control **once** and reference it by name across every Figma page.
FigForge can generate reusable uGUI controls for buttons, toggles/radios,
switches, input fields, steppers, dropdowns, sliders, progress bars, lists, and
tables.

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

The plugin records `canonical: { kind, ref, instanceName, label }` on the element
(the `label` is the first text found inside the layer, else the instance name)
and lists distinct refs in `manifest.canonicalRefs`.

## Wiring it up in Unity

1. **Create ▸ FigForge ▸ Canonical Library** — a `CanonicalLibrary` asset.
2. Add an entry per ref: `referenceName = "PrimaryButton"`, `prefab = your control prefab`.
   - Input prefabs should carry a `TMP_InputField`; `FigForgeBindings.label`
     is used as the placeholder, and `valueText` is used for the editable text.
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
