# Canonical UI Elements

Define a control **once** in Unity and reference it by name across every Figma
page. Today FigForge supports the **button** kind; the system is built to extend.

## The naming convention

```
Btn_<instanceName>_<canonicalRef>
```

| Token | Example | Meaning |
|------|---------|---------|
| `Btn` | `Btn` | Kind tag (case-insensitive: `Btn` or `Button`). |
| `<instanceName>` | `Save` | This element's design-specific name (may contain `_`). |
| `<canonicalRef>` | `PrimaryButton` | The **last** token — the canonical definition to instantiate. |

Examples:

| Figma layer name | kind | instance | ref |
|------------------|------|----------|-----|
| `Btn_Save_PrimaryButton` | button | Save | PrimaryButton |
| `Btn_Cancel_Secondary` | button | Cancel | Secondary |
| `Button_NextStep_Primary` | button | NextStep | Primary |

The plugin records `canonical: { kind, ref, instanceName, label }` on the element
(the `label` is the first text found inside the layer, else the instance name)
and lists distinct refs in `manifest.canonicalRefs`.

## Wiring it up in Unity

1. **Create ▸ FigForge ▸ Canonical Library** — a `CanonicalLibrary` asset.
2. Add an entry per ref: `referenceName = "PrimaryButton"`, `prefab = your button prefab`.
   - The prefab's root should carry a `Button` and a `TMP_Text`/`Text` label somewhere inside.
3. In **Window ▸ FigForge ▸ Importer**, open **Canonical elements**, assign the
   library. Each ref shows ✓ (resolved) or ✗ (missing).
4. **Build**. For each canonical layer, FigForge instantiates the prefab, applies
   the layer's `RectTransform`, and stamps the label — instead of rebuilding from
   a PNG/text.

If a ref isn't found in the library, FigForge drops in a labelled purple
placeholder button and logs a warning, so the layout still works.

## Why

- One source of truth for shared controls — restyle the prefab, every page updates.
- Real, interactive Unity prefabs (with your scripts) instead of flat images.
- Designs stay declarative: the Figma name *is* the binding.

## Extending to new kinds

`CanonicalLibrary.Resolve(kind, ref)` switches on `kind`. Add a new list (e.g.
`inputs`), a new kind tag in the plugin's `naming.ts` `KIND_TAGS`, and a branch
in `Resolve` + the importer's canonical handling.
