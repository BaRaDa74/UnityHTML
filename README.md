# UnityHTML

An editor tool that turns a hand-written HTML page (plus its CSS) into a UI
Toolkit screen in Unity — same structure, same type hierarchy, same palette,
working controls. Not pixel-perfect, but recognisably the same screen.

The repo ships the converter, a three-screen game-menu fixture to exercise it,
and the Node tooling that regenerates and statically checks the output.

- [`SPEC.md`](SPEC.md) — the contract, the measured limitations and the backlog. Start here.
- [`html2uuitk-test/README.md`](html2uuitk-test/README.md) — the narrative tour of the fixture.

---

## What is in here

| Path | What it is |
| --- | --- |
| `Assets/Editor/HtmlToUuitk/` | The converter (editor-only assembly, 7 files) |
| `Assets/Scripts/MenuScreens.cs` | Runtime controller: one screen visible at a time, follows the page's links |
| `Assets/UI/<Screen>/` | Generated: `<page>.uxml` plus one `.uss` per linked stylesheet |
| `Assets/UI/Prefabs/` | Generated: `IndexScreen`, `PauseScreen`, `SettingsScreen`, `MenuScreens` |
| `Assets/UI/MenuPanelSettings.asset` | Shared `PanelSettings` (Scale With Screen Size, 1920×1080) |
| `Assets/Fonts/` | Rubik + Space Mono (both OFL), referenced through `config.json` |
| `html2uuitk-test/` | HTML/CSS source of the fixture, `config.json`, narrative README |
| `tools/` | Node CLI: `convert.mjs`, `validate.mjs`, `unity-test.mjs`, `unity-mcp.mjs` |
| `_renders/` | Play-mode captures (generated, git-ignored) |

Unity project version is 6000.3.17f1.

## The converter

Open **Tools ▸ HTML to UI Toolkit**, point it at a folder (or a single `.html`)
and press **Convert** or **Convert + Create Prefabs**. It defaults to
`html2uuitk-test/`.

| File | Role |
| --- | --- |
| `HtmlParser.cs` | Tolerant HTML tokenizer/tree. No implied tags — the tree is what the markup literally says |
| `Css.cs` | Rule/declaration parser; at-rules (`@media`, `@font-face`) are skipped whole |
| `UssMapper.cs` | CSS → USS: shorthand expansion, units, colours, fonts, `box-shadow`, `:root` variables with chained and fallback `var()` |
| `UxmlWriter.cs` | HTML → UXML mapping, attributes, mixed-content handling |
| `HtmlToUuitkConverter.cs` | Orchestration: stylesheet discovery from `<link>`, config/font loading, file writing |
| `PrefabBuilder.cs` | Screen prefabs and the combined fixture prefab |
| `HtmlToUuitkWindow.cs` | The `Tools ▸ HTML to UI Toolkit` window |

**Output layout.** Styles sit beside the document because `<ui:Style src>`
resolves relative to the `.uxml`:

```
Assets/UI/<TitleCased page name>/
  <page>.uxml        # <ui:Style src> per linked sheet, tab indent
  <sheetname>.uss    # one per linked stylesheet, in document order
```

Files are rewritten in place, so `.meta` GUIDs survive and scene/prefab
references keep working.

**Prefabs.** Unity does not serialize a `VisualElement` tree into a prefab, so
the tree stays in the `.uxml` and the prefab carries the wiring: one prefab per
screen with a `UIDocument` pointing at the generated `.uxml` and the shared
`PanelSettings`, plus `MenuScreens.prefab` with the three screens as nested
instances and the controller fields wired. Drop it in a scene and the menu works.

**Code the converter will not represent:** JavaScript behaviour, form
submission, animations, `@media` queries, responsive breakpoints and image
assets are out of scope. The full mapping tables (which HTML becomes which UXML
element, which CSS property becomes which USS property, and what is dropped with
a warning) are in [`SPEC.md` §4](SPEC.md).

## The fixture

`html2uuitk-test/` is a realistic three-screen menu written as plain HTML and
CSS, with cross-screen `<a href>` links:

```
index.html (main menu)
├── settings.html          "Advanced settings"
└── pause.html (pause overlay)
    └── settings.html      "Controls"
```

| Screen | Source | Converted to |
| --- | --- | --- |
| Main menu | `html2uuitk-test/index.html` | `Assets/UI/Index/index.uxml` |
| Pause overlay | `html2uuitk-test/pause.html` | `Assets/UI/Pause/pause.uxml` |
| Settings | `html2uuitk-test/settings.html` | `Assets/UI/Settings/settings.uxml` |

Shared styles stay in `html2uuitk-test/`: `theme.css` (palette, controls,
states), `base.css` (layout primitives), `config.json` (font assets and converter
options). Each screen folder gets its own copy of `base.uss` + `theme.uss`.

Links survive as `href` on `ui:Label` and as the `data-key:*` classes the
converter carries over from `data-key="…"`; `MenuScreens` reads both to drive the
screens.

## Running the fixture

### In the editor (no install)

1. Open the project in Unity.
2. **Tools ▸ HTML to UI Toolkit**, source `html2uuitk-test`, then **Convert** or
   **Convert + Create Prefabs**.
3. Open `Assets/Scenes/SampleScene.unity` and press Play. The scene has `Screens`
   with the three `UIDocument` children and the `MenuScreens` controller, so menu
   navigation works straight away.

### From the CLI

Needs Node 18+ and the npm converter (the in-editor tool does not):

```bash
npm install
npm run convert        # html -> Assets/UI/{Index,Pause,Settings}
npm run validate       # static checks; no Unity needed
npm run test           # convert + validate
```

`npm run test:unity` runs the end-to-end check **inside a running Unity editor**
through the [unity-mcp](https://github.com/CoplayDev/unity-mcp) bridge, which
must be running at `http://127.0.0.1:8080/mcp` (override with `UNITY_MCP_URL`),
with the project open:

```bash
npm run test:unity
```

It clears the console, forces an asset refresh, reads back import errors and
warnings, then loads every UXML through `AssetDatabase`, instantiates it and
walks the live `VisualElement` tree — reporting per-screen element counts,
dropdown choices and slider ranges.

Both paths write the same `Assets/UI/<Screen>/` files, so they overwrite each
other's output while keeping `.meta` GUIDs intact.

### Tools

| File | Role |
| --- | --- |
| `tools/convert.mjs` | Runs the converter per screen into `Assets/UI/<Screen>/`, then repairs the upstream output defects documented in the fixture README. `--out <dir>`, `--quiet` |
| `tools/validate.mjs` | Dependency-free checks on the generated files: XML well-formedness, element/attribute validity, class coverage, `<ui:Style>` targets, USS `url()` targets. `--strict`, `--json` |
| `tools/unity-test.mjs` | Import + visual-tree test through unity-mcp. `--json`, `--no-refresh` |
| `tools/unity-mcp.mjs` | Small MCP client: `tools`, `schema <tool>`, `resources`, `read <uri>`, `call <tool> <json>` |

### What "good" means for a change

Defined in [`SPEC.md` §7](SPEC.md): `validate.mjs` reports 0 errors with the
warning count explained, `unity-test.mjs` loads 3/3 screens with no failures and
no console errors, and each touched screen is captured in play mode at 1920×1080
and compared by eye with the browser rendering of the same page.

Play-mode capture is the only way to see real pixels — editor-mode rendering
returns blank. Two things to remember: the **Game view must be focused** or Unity
stops drawing frames, and `UIDocument.visualTreeAsset` must be assigned in edit
mode (assigning it at runtime does not rebuild the tree). Captures land in
`_renders/`, outside `Assets/`, so Unity never imports them.

## Known limitations

Measured, not guessed — the table with symptoms, causes and workarounds is
[`SPEC.md` §6](SPEC.md). The short version: long text in a row container does not
wrap, `overflow: auto` becomes `hidden` (no `ScrollView` is emitted yet), `img`
never appears, and `data-key:*` classes cannot be targeted from USS because `:`
is a pseudo-class separator — C# reads them with `GetClasses()` instead.

## Library and other generated files

`Library/`, `Temp/`, `Logs/`, `UserSettings/`, `_renders/`, `node_modules/` and
the usual IDE artefacts are git-ignored. `Assets/UI/` **is** committed, since
the generated assets are the thing the fixture validates.

## Attribution

The CLI path uses [`html2uuitk`](https://github.com/fake-skate/html2uuitk)
0.5.1 (ISC), keeping it as the reference the C# port was checked against. The two
fonts in `Assets/Fonts/` are Rubik and Space Mono, both under the SIL Open Font
License.
