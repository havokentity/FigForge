# FigForge

> Draw it in Figma. Forge it into Unity uGUI — by hand-off or by AI.

Most Figma-to-Unity tools hand you a pile of flat images. FigForge keeps the
*structure*: a button stays anchored like a button, a stretched bar stays
stretched, a rounded panel keeps its corners and its colour. You export a frame,
and on the Unity side it comes back as a real, responsive uGUI hierarchy you'd
have been happy to build by hand — only you didn't.

It comes in three parts that all speak one file format, so any piece is
swappable and nothing is magic.

---

## If you just want to use it

```text
1.  Build/install the Figma plugin  →  open it in Figma Desktop
2.  Select a frame  →  Export for Unity  →  a .zip drops
3.  Unzip under Assets/  →  Window ▸ FigForge ▸ Importer ▸ Build
```

That's the whole loop. The AI bridge (part three) is optional sugar for when you
want an agent to skip the zip and write straight into your project. Detailed
setup is [further down](#setting-it-up-properly).

---

## The three parts, and the one file

FigForge is deliberately not a monolith:

**`plugin/` — the Figma side** *(TypeScript, bundled with esbuild)*
Reads the selected frame, turns Figma constraints into Unity anchors, decides
what has to become a PNG versus what can be rebuilt structurally, and emits a
`manifest.json` plus its assets.

**`server/` — the bridge** *(TypeScript, MCP + WebSocket)*
An MCP server that lets an agent read the Figma file and call `export_unity` to
drop a manifest straight onto disk. Several MCP clients can run at once; they
elect one leader to hold the single plugin socket and the rest proxy through it.

**`unity/` — the importer** *(C#, Editor + Runtime assemblies)*
Reads the manifest and rebuilds the hierarchy: anchored `RectTransform`s,
`Image`s, `TextMeshProUGUI`, the lot.

The glue is **the manifest** — one JSON contract the plugin writes and the
importer reads. Change one side, change the other. Everything else is detail.

---

## What happens to a frame

Follow a single frame through the machine; every feature shows up along the way.

**0 · You curate it.** In the plugin you can exclude layers, merge a container
into one flat PNG, force a text layer to rasterize, search the tree, and preview
any node live before committing.

**1 · It gets walked.** A depth-first pass classifies every node. Placeholder
and fully-transparent paints (the empty image fill someone left in the design)
are recognised as *nothing* — so they never bake out as junk PNGs or stray white
boxes.

**2 · Layout becomes anchors.** Each layer's Figma constraints map to real Unity
anchors — stretch, pin, centre, or proportional, per axis. Nothing is dumped at
a fixed centre point, so the result actually responds to canvas size. Rotation
rides along too.

**3 · Pixels vs. structure gets decided.** Vectors and icon groups rasterize
(hash-deduped, so identical art is stored once). Solid *and gradient* fills are
captured as data — gradients bake to a sprite rather than collapsing to one
colour. Strokes carry their width and colour and come back as 9-sliced outlines,
rounded-corner aware. A rounded panel with only a fill becomes a procedural
rounded sprite — corners and colour intact. Text stays `TextMeshProUGUI`. If an
export ever fails, that element falls back to its fill colour instead of a
missing-sprite white box.

**4 · It's written out.** `manifest.json` + the PNGs, either as a downloadable
zip or — over the bridge — written directly into your Unity project.

**5 · Unity rebuilds it.** The importer stitches the anchored hierarchy under a
`Canvas`, maps each font family/style to a `TMP_FontAsset`, swaps any
canonically-named layer for a real prefab instance, and parents the whole frame
under a `ScreenManager` as one `BaseScreen` — so importing several frames gives
you a single navigable, multi-page scene rather than a heap of disconnected
canvases.

---

## Setting it up properly

> Prefer not to build anything? Every piece ships prebuilt on the
> [latest release](../../releases/latest).

**The plugin**

```bash
cd plugin && npm install && npm run build      # → dist/main.js + dist/ui.html
```

Then in Figma **Desktop** (it needs `exportAsync`, which the browser lacks):
*Plugins → Development → Import plugin from manifest…* → `plugin/manifest.json`.
From a release instead: unzip `figforge-plugin-<ver>.zip` and import its
`manifest.json`.

**The bridge** *(only if you want the AI workflow)*

```bash
cd server && npm install && npm run build      # → dist/index.js
```

Point your MCP client at it. Set `FIGFORGE_WORKSPACE` to your Unity project root
so `export_unity` writes there (and refuses to write anywhere else):

```jsonc
{
  "mcpServers": {
    "figforge": {
      "command": "node",
      "args": ["<abs>/server/dist/index.js"],
      "env": { "FIGFORGE_WORKSPACE": "<abs>/MyUnityProject" }
    }
  }
}
```

From a release instead: unzip `figforge-bridge-<ver>.zip`, then
`npm install --omit=dev`.

**The Unity importer** — pick whichever fits your project:

```text
Package Manager ▸ Add package from git URL…
    https://github.com/havokentity/FigForge.git?path=unity#v1.0.1
Package Manager ▸ Add package from tarball…
    figforge-unity-importer-<ver>.tgz   (from a release)
Package Manager ▸ Add package from disk…
    unity/package.json
```

Pin the git URL to a tag (`#v1.0.1`) so upgrades are deliberate. Deps — uGUI,
TextMeshPro, Newtonsoft JSON, 2D Sprite — resolve automatically.

---

## The canonical trick

Here's the part that makes FigForge more than an importer. Name a Figma layer
like this:

```text
Btn_<instanceName>_<canonicalRef>          Btn_Save_PrimaryButton
└┬┘ └─────┬──────┘ └──────┬──────┘
 │        │               └─ which reusable definition to drop in
 │        └─ this instance's own name
 └─ the kind tag (button, for now)
```

On import, instead of rebuilding that layer from a flattened PNG, FigForge looks
up `PrimaryButton` in a **Canonical Library** asset you provide
(*Create ▸ FigForge ▸ Canonical Library*), instantiates your real button prefab,
and stamps the label onto it. Define a control once; reference it by name from
every screen; restyle the prefab and every page updates. Miss a mapping and you
get a labelled placeholder plus a warning — never a broken build. Full walkthrough:
[`docs/canonical-elements.md`](docs/canonical-elements.md).

---

## Handing the wheel to an agent

With the bridge running, an MCP client can drive the whole thing. Tools on offer:

`get_metadata` · `get_document` · `get_selection` · `get_node` ·
`get_design_context` · `get_screenshot` · `save_screenshots` · `export_unity`

`export_unity` is the headline: it runs the same exporter the UI button uses and
writes `manifest.json` + PNGs to a folder you name (sandboxed to the workspace
root). The plugin's **Bridge** tab connects out to `ws://127.0.0.1:1994/ws`.
Wiring and the leader/follower design live in
[`docs/architecture.md`](docs/architecture.md).

---

## The manifest, in one breath

One JSON file describes the screen, every element (its rect, the computed Unity
transform, components, style, text, any sprite or canonical reference, and its
children), the asset list, and the fonts used. The plugin writes it; the C#
`ManifestData` mirrors it field-for-field. The full schema is in
[`docs/plugin-guide.md`](docs/plugin-guide.md).

---

## When it fights you

- **Plugin says "Select a frame…"** — select a Frame, Component, or Group, not a lone shape.
- **Export button greyed out** — nothing valid is selected.
- **Bridge dot stays red** — the server isn't up, or port `1994` is taken; start it and hit **Connect**.
- **Wrong fonts in Unity** — map each `family|style` to a `TMP_FontAsset` in the importer's Fonts section.
- **A canonical layer came in as a purple placeholder** — assign a Canonical Library and map that ref name to a prefab, then rebuild.
- **A rounded panel rendered as a sharp/white box** — give the layer a real fill or stroke; fill-only rounded panels are drawn procedurally.
- **`using FigForge` won't resolve after a git install** — make sure you're on a tag that ships `.meta` files (`v1.0.1`+); earlier builds didn't and Unity ignores meta-less package assets.

---

## On the bench (not built yet)

A 2D `SpriteRenderer` output mode, dashed-stroke fidelity, and more canonical
kinds beyond buttons (inputs, toggles). PRs welcome.

---

## Where things live

```text
plugin/   Figma plugin          — TypeScript / esbuild
server/   MCP bridge            — TypeScript
unity/    Unity importer (UPM)  — C#, Editor + Runtime
docs/     Guides
```

**Needs:** Unity 2022.3+ (tested through 6000.x) · Node ≥ 20 · TextMeshPro ·
Newtonsoft JSON.  **Licence:** MIT.
