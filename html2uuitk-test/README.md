# html2uuitk test fixture — main menu, pause and settings

The contract, measured limitations, unity-mcp workflow notes and the backlog
live in [`../SPEC.md`](../SPEC.md); this file is the narrative tour.

Source files for exercising [html2uuitk](https://github.com/fake-skate/html2uuitk)
against a realistic game menu, converted into UI Toolkit assets that Unity
imports, and verified inside the running editor through
[unity-mcp](https://github.com/CoplayDev/unity-mcp). Conversion now runs **inside
the editor** (`Tools ▸ HTML to UI Toolkit`): HTML and CSS in, `.uxml`/`.uss` and
prefabs out, no Node required. The original npm converter is kept as the CLI
path for CI and as the reference the port was checked against.

| Screen | Source | Converted to |
| --- | --- | --- |
| Main menu | `html2uuitk-test/index.html` | `Assets/UI/Index/index.uxml` |
| Pause overlay | `html2uuitk-test/pause.html` | `Assets/UI/Pause/pause.uxml` |
| Settings | `html2uuitk-test/settings.html` | `Assets/UI/Settings/settings.uxml` |

Shared styles stay in `html2uuitk-test/`: `theme.css` (palette, controls, states,
unchanged by every screen), `base.css` (layout primitives), `config.json`
(converter options). Each screen folder in `Assets/UI/` gets its own copy of
`base.uss` + `theme.uss`, because `<ui:Style src>` is resolved relative to the
`.uxml`.

## Page tree

The screens link to each other with plain `<a href>`, so the tree can be walked
in the editor without C#:

```
index.html (main menu)
├── settings.html          "Advanced settings"
└── pause.html (pause overlay)
    └── settings.html      "Controls"

settings.html
├── ← Main menu            back arrow, header
├── Back to main menu      footer link
└── Resume run → pause.html
```

The links survive as `href` on `ui:Label`, which UI Toolkit ignores, and as the
`data-key:*` classes the converter carries over from `data-key="…"`. `MenuScreens`
(see below) reads both to drive the screens.

## Install and run

Inside Unity: **Tools ▸ HTML to UI Toolkit**, point it at
`html2uuitk-test/` (or a single `.html`), press **Convert** or **Convert + Create
Prefabs**. Nothing to install.

The CLI path is still available and is what the npm scripts use:

```bash
npm install            # html2uuitk 0.5.1, no other runtime dependencies
npm run convert        # html -> Assets/UI/{Index,Pause,Settings}
npm run validate       # static checks, no Unity needed
npm run test:unity     # import + visual-tree test inside the running editor
```

`npm run test` is `convert` + `validate`. `test:unity` needs the Unity project
open with the unity-mcp bridge running (`http://127.0.0.1:8080/mcp`; override
with `UNITY_MCP_URL`).

Both paths write to the same `Assets/UI/<Screen>/` files, so `npm run convert`
overwrites whatever the editor tool produced (and vice versa). Assets keep their
`.meta` GUIDs either way, so scene and prefab references survive; only the
generated markup changes.

Two fonts are checked in at `Assets/Fonts/` (Rubik, Space Mono, both OFL) so the
`font-family` mapping in `config.json` resolves to real font assets.

### What each tool does

| File | Role |
| --- | --- |
| `tools/convert.mjs` | Runs the converter per screen into `Assets/UI/<Screen>/`, fails on converter warnings, then applies the normalisation pass described below |
| `tools/validate.mjs` | Dependency-free checks on the generated files: XML well-formedness, element/attribute validity, class coverage, `<ui:Style>` targets, USS `url()` targets. `--strict` promotes warnings to failures, `--json` for machine output |
| `tools/unity-test.mjs` | Clears the console, forces an asset refresh, reads back errors/warnings, then loads every UXML through `AssetDatabase`, instantiates it and walks the live VisualElement tree |
| `tools/unity-mcp.mjs` | Small CLI for the MCP endpoint: `tools`, `schema <tool>`, `resources`, `read <uri>`, `call <tool> <json>` |

`_renders/` holds play-mode captures of all three screens (`index.png`,
`pause.png`, `settings.png`).

## In-editor converter

`Assets/Editor/HtmlToUuitk/` holds the port. It is editor-only code, ~1.4k lines:

| File | Role |
| --- | --- |
| `HtmlParser.cs` | Tolerant HTML tokenizer/tree builder. No implied tags — the tree is what the markup literally says, which is what a converter wants |
| `Css.cs` | Rule/declaration parser. At-rules (`@media`, `@font-face`) are skipped whole |
| `UssMapper.cs` | CSS → USS: shorthand expansion, units, colours, fonts, `box-shadow`, `:root` variables with chained and fallback `var()` |
| `UxmlWriter.cs` | HTML → UXML element mapping and attribute handling |
| `HtmlToUuitkConverter.cs` | Orchestration: stylesheet discovery from `<link>`, config/font loading, file writing |
| `PrefabBuilder.cs` | Screen prefabs and the fixture prefab |
| `HtmlToUuitkWindow.cs` | The window itself |

Output lands beside itself, because `<ui:Style src>` resolves relative to the
`.uxml`: `Assets/UI/<Screen>/<page>.uxml` plus one `.uss` per linked stylesheet.
Overwriting an existing screen keeps its `.meta` GUID, so scene and prefab
references survive a re-convert.

**Prefabs.** *Convert + Create Prefabs* writes `Assets/UI/Prefabs/`:

- `IndexScreen.prefab`, `PauseScreen.prefab`, `SettingsScreen.prefab` — a root
  GameObject with a `UIDocument` pointing at the generated `.uxml` and the shared
  `PanelSettings`.
- `MenuScreens.prefab` — the three screens as nested prefab instances plus the
  `MenuScreens` controller wired to them; drop it in a scene and the menu works.

A prefab cannot contain the elements themselves: Unity does not serialize a
`VisualElement` tree into a prefab, so the tree stays in the `.uxml` and the
prefab carries the wiring. That is the whole reason the conversion has to emit
assets rather than build objects directly.

## Seeing it in the editor

The scene contains what a UI Toolkit menu needs:

| Object | Value |
| --- | --- |
| `Assets/UI/MenuPanelSettings.asset` | `PanelSettings`, Scale With Screen Size, 1920×1080 |
| `Screens` (GameObject, `SampleScene`) | `MenuScreens` controller |
| `Screens/IndexScreen` | `UIDocument` → `Index/index.uxml` |
| `Screens/PauseScreen` | `UIDocument` → `Pause/pause.uxml` |
| `Screens/SettingsScreen` | `UIDocument` → `Settings/settings.uxml` |

`MenuScreens` shows one screen at a time by toggling the root's `display` (not the
GameObject — disabling a `UIDocument` rebuilds its tree and drops the click
handlers). It follows the page's own links: `data-key:*` classes on buttons
(`settings`, `main-menu`) and the `back-link` label that was an `<a>`. With no
gameplay behind the fixture, `Continue`/`New Game` open the in-run screen, i.e.
the pause menu.

Captures live in `_renders/` (outside `Assets/`, so Unity never imports them).

Editor-mode capture through unity-mcp renders blank — it only assigns a
RenderTexture to the PanelSettings. Play mode works, with two caveats: the
**Game view must be focused**, otherwise Unity stops drawing frames and the
screen capture never completes (the MCP capture then reports `capture_in_progress`
until a new play session); and the UXML source has to be set **before** entering
play mode — assigning `UIDocument.visualTreeAsset` at runtime does not rebuild
the tree. The reliable loop per screen is:

```
stop play  ->  attach_ui_document (edit mode)  ->  play  ->  focus Game view
           ->  ScreenCapture.CaptureScreenshot("_renders/<screen>.png")
```

## What Unity actually did

Measured with `npm run test:unity` against Unity 6000.3.17f1 and unity-mcp
3.4.11. All three screens load (126, 80 and 166 VisualElements) with a clean
console.

Found and repaired in `tools/convert.mjs` — the converter output is wrong here,
not the fixture:

- `<select>` becomes `DropdownField` with `<option>` **child elements**. Unity's
  UXML importer drops unknown children without a word, so every dropdown
  arrived with zero choices. UXML *does* support a `choices` attribute, so the
  option text is hoisted into `choices="Night,Standard,Cinema"`.
- `min`/`max` on `<input type="range">` stay `min`/`max`; UI Toolkit wants
  `low-value`/`high-value`, otherwise sliders sit at 0–10 and the converted
  value (70) clamps. Mapped.
- `<Style>` is emitted without the `ui:` namespace. The importer accepts it but
  **UI Builder refuses to open such a document**. Prefixed to `<ui:Style>`.
- `-unity-font-definition: none;` is appended next to every `-unity-font`, and
  Unity logs `Expected (<resource> | <url>) but found 'none'`. Line dropped.
- **CSS custom properties are copied through unresolved** — `.screen` came out
  as `background-color: var(--bg);` and the `:root` block that defines `--bg`
  was dropped entirely. Drive-by: UI Toolkit has no `var()` and no
  custom-property declarations, so *every* such declaration was silently
  ignored (111 of them across the three screens). The `:root` values are now
  read from `base.css`/`theme.css` and substituted, including chained
  (`--accent-glow: var(--accent)`) and fallback (`var(--a, var(--b))`) forms.
  Substituted colours are normalised to `rgba()` — a single `rgb(30 41 66 / 0.55)`
  makes Unity discard the *whole* stylesheet, which is why the resolving pass
  briefly produced a completely unstyled screen.
- Font paths are written verbatim from `config.json` and resolved relative to
  the `.uss`, so `Assets/Fonts/…` became `Assets/UI/Index/Assets/Fonts/…`.
  `config.json` now uses the project-relative `project://database/Assets/…`
  form, which is what Unity resolves.

Still visible in the output, for the fixture's sake (found via `validate` /
`unity-test`):

- **Only text and classes survive.** HTML attributes with no UXML counterpart are
  silently ignored: `id`, `type`, `for`, `href`, `aria-label`, `checked`,
  `placeholder`, and `min`/`max`/`step` on `IntegerField`. Nothing errors — the
  elements just come back plain.
- `data-*` attributes become class names such as `data-key:continue`. Unity
  keeps the class, but a colon is a pseudo-class separator in USS, so that class
  can never be targeted. 7/5/6 elements per screen carry one.
- Rich `<button>` content becomes nested `ui:Label` children inside `ui:Button`
  (2 per nav item). `Button` is not a container, so the label and its hint are
  separate children laid out by flex rather than one text run.
- `overflow: auto` is "supported" but written as `overflow: hidden`, so
  `.settings-content` clips: the last rows are cut off at the 440px cap. UI
  Toolkit needs a `ScrollView` built in C#.
- Flex children shrink by default in UI Toolkit, so anything inside a
  height-capped container collapses on top of itself. `.setting-row` now sets
  `flex-shrink: 0` — valid CSS that browsers ignore because the box is not
  actually constrained there.
- `html, body` selectors never match, so the page background has to live on the
  root container; `.screen` now carries `background-color: var(--bg)`. Without
  it the panel is transparent and the 3D skybox shows through the pause
  overlay's 78% backdrop.
- Long text inside a row does not wrap: the pause quote and the footer legal
  line run past their containers because a `Label` in a `flex-direction: row`
  parent keeps its natural width.
- `html, body` becomes a USS selector UI Toolkit does not know, and the
  converter adds `class="label"` / `class="settings"` to elements that no CSS
  rule targets.
- Semantic tags with no UI Toolkit equivalent — `dl`/`dt`/`dd`, `figure`,
  `figcaption`, `blockquote`, `fieldset`, `legend`, `hr` — are copied through
  verbatim (`<hr class="rule"></hr>`), which is invalid UXML. The fixtures use
  `div`/`span`/`p` equivalents with the same classes instead.
- `box-shadow` spread (`0 0 0 2px`) is dropped; only offset/blur/color survive.
- Text nodes spanning indented source lines keep their newlines in `text=`.
- An `<a>` with element children emits both a concatenated `text=` and the
  nested labels, duplicating the text; the back arrow is a single text node.

### What the in-editor port does differently

Same contract, different edges — these are defects the Node output has that the
C# port does not produce:

- **Inline text keeps its order.** A `<p>` mixing text and `<strong>`/`<em>` used
to emit a concatenated `text=` *and* nested labels, so phrases drew on top of
each other. The port emits the runs in document order as sibling labels and
puts them in a `flex-direction: row; flex-wrap: wrap` flow, so the sentence
reads as a sentence.
- **Whitespace is collapsed and escaped.** Text keeps a single space per run and
  cannot break the XML with a stray `&` or `<`.
- **Semantic tags are mapped** instead of copied verbatim: `hr`, `fieldset`,
  `figure`, `blockquote` and friends become `VisualElement`/`Label`, so no
  invalid `<hr …></hr>` reaches the importer.
- **Duplicate attributes are impossible.** The first port put `name` on radio
  inputs twice; Unity rejected the whole document with
  `'name' is a duplicate attribute name` and the screen rendered empty. Worth
  remembering: one bad UXML detail does not degrade gracefully, it removes the
  entire tree.
- **`vh`/`vw` map to a percentage**, because a UI Toolkit panel is always the
  full screen. Dropping them silently shrank `.settings` to content height: the
  Node output only looked right because it kept `height: 100%` elsewhere.
- **`margin`/`padding` with one value expand to four sides**, which USS requires;
  the Node path emitted `margin: 0;` and relied on USS defaults.

`data-key:*` classes are still produced, and still cannot be targeted from USS
(the colon is a pseudo-class separator), but C# reads them with
`element.GetClasses()` — that is how `MenuScreens` follows the links.

## Deliberately unsupported CSS

Avoided in the fixtures so the conversion stays readable: `gap`, `line-height`,
`border-bottom`/`border-top`, `border: 1px solid …` shorthand, `list-style`,
`::before`/`::after`. They are replaced with per-side margins, explicit
`border-width`/`border-color` and uniform borders.

## What the fixture covers

| Area | Where |
| --- | --- |
| Multi-file CSS (`--css` order) | `base.css` then `theme.css` |
| `<link rel="stylesheet">` → `<ui:Style>` | `index.html` head |
| Element mapping | `div`, `header`, `main`, `footer`, `nav`, `section`, `aside`, `ul`/`li`, `h1`–`h3`, `p`, `span`, `a`, `button`, `small`, `strong`, `em`, `code`, `time` |
| Controls | `TextField`, `IntegerField`, `Slider`, `RadioButton`, `Toggle`, `DropdownField`, `ProgressBar`, `Button` |
| `data-*` → class | `data-key` on menu items and action buttons |
| Font asset mapping | `Rubik`, `Space Mono` in `config.json` |
| CSS variables | `:root` block, chained `--accent-glow`, fallback `var(--legal-color, var(--muted))` |
| Shorthand expansion | `margin`, `padding`, `border-radius`, `font`, `background` |
| Unit conversion | `rem`, `em`, `vh`, `px` |
| Value mapping | `font-weight: 600/700` → `-unity-font-style`, `text-align`, modern `rgb(r g b / a)` colors |
| Pseudo-classes | `:hover`, `:active`, `:focus` on menu items, buttons and links |
| Page tree | cross-screen `<a href>` links (main → settings, main → pause → settings) |
| Back navigation | `← Main menu` arrow link plus a footer link |
| Centered overlay layout | `.overlay-screen`, `.pause-panel`, `.pause-body` |
| Two-column split inside a card | `.pause-nav` (46%) + flex-grow `.pause-details` |
| Zero-width border | `.rule` — `border-width: 0` plus `height: 1px` |
| Dense settings list | `.settings-content` with `max-height` + `overflow: auto` |
| Label + control rows | `.setting-row` with a two-line `.setting-text` block |
| Sidebar tabs | `.menu-item.is-primary` reused as the active section tab |

## Then what

The converted files are ordinary assets: add a `UIDocument` to a GameObject, a
`PanelSettings` asset, and point the document at one of the `.uxml` files. All
three screens are drawn by the same `base.uss`/`theme.uss` pair, so switching
`UIDocument.sourceAsset` is enough to move between them from C#.
