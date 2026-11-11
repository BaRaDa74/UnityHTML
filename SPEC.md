# SPEC — HTML → UI Toolkit conversion inside the Unity editor

Status: implemented and working against the three-screen fixture. This document is
the contract, the measured state, and the backlog. Update it when behaviour
changes; it is the place a new contributor should start.

---

## 1. Goal

One editor tool that takes a hand-written HTML page (plus its CSS) and produces a
UI Toolkit screen in Unity that is a **fair to good representation of the
original** — not pixel-perfect, but recognisably the same menu: same structure,
same type hierarchy, same palette, working controls.

"A fair to good representation" is only useful if it can be checked. The bar is:

| ID | Criterion | How it is checked |
| --- | --- | --- |
| G1 | Every page renders; no screen is empty | `VisualTreeAsset.Instantiate().childCount > 0` for each screen |
| G2 | All source text appears, in document order, without overlap | Rendered capture + `unity-test` text dump |
| G3 | Typography from the theme applies: family, size, weight, letter-spacing | Rendering inspection; USS contains `-unity-font`, `font-size`, `-unity-font-style` |
| G4 | Block structure survives flex rules (rows/columns, sidebar split, spacing) | Capture compared to the browser rendering of the same page |
| G5 | Controls work: sliders have `low-value`/`high-value`, dropdowns have choices, toggles start checked | `unity-test` reports slider ranges and dropdown sizes |
| G6 | No console errors; UXML is valid; warnings are only the known-loss list | `read_console`, `tools/validate.mjs` |
| G7 | Regenerating does not break scene/prefab references | `.meta` GUIDs persist across overwrite |

Deliberately **not** in scope: JavaScript behaviour, form submission, animations,
`@media` queries, responsive breakpoints, CSS cascade resolution beyond what USS
does natively, image assets.

---

## 2. Repository inventory

| Path | What it is |
| --- | --- |
| `Assets/Editor/HtmlToUuitk/` | The converter, editor-only assembly (1,789 lines across 7 files) |
| `Assets/Scripts/MenuScreens.cs` | Runtime controller: one screen visible at a time, follows the page's links |
| `Assets/UI/<Screen>/` | Generated: `<page>.uxml` + one `.uss` per linked stylesheet |
| `Assets/UI/Prefabs/` | Generated: `IndexScreen`, `PauseScreen`, `SettingsScreen`, `MenuScreens` |
| `Assets/UI/MenuPanelSettings.asset` | Shared `PanelSettings` (Scale With Screen Size, 1920×1080) |
| `Assets/Fonts/` | Rubik + Space Mono (OFL), referenced through `config.json` |
| `html2uuitk-test/` | HTML/CSS source of the fixture + `README.md` (narrative) and `config.json` |
| `tools/` | Node CLI: `convert.mjs`, `validate.mjs`, `unity-test.mjs`, `unity-mcp.mjs` |
| `_renders/` | Play-mode captures, outside `Assets/` so Unity never imports them |

Editor tool files:

| File | Lines | Role |
| --- | --- | --- |
| `HtmlParser.cs` | 217 | Tolerant HTML tokenizer/tree. No implied tags, no foster parenting |
| `Css.cs` | 171 | Rule/declaration parser; at-rules skipped whole |
| `UssMapper.cs` | 598 | CSS → USS: expansion, units, colours, fonts, shadows, `var()` |
| `UxmlWriter.cs` | 331 | HTML → UXML mapping, mixed-content handling |
| `HtmlToUuitkConverter.cs` | 147 | Orchestration, stylesheet discovery, config/font loading |
| `PrefabBuilder.cs` | 105 | Screen prefabs + fixture prefab |
| `HtmlToUuitkWindow.cs` | 220 | `Tools ▸ HTML to UI Toolkit` |

---

## 3. Data flow

```
fixture folder or single .html
        │
        ├─ HtmlParser ─────────────────► HtmlNode tree
        │                                    │
        ├─ <link> discovery ─► CssParser ─► rules (per file, order kept)
        │                        │
        │                        ├─ AddVariables(:root, all files)   ← must precede mapping
        │                        └─ UssMapper.MapSheet(file) ─► <name>.uss
        │
        └─ UxmlWriter(Build) ◄── style file names ─► <page>.uxml

PrefabBuilder: .uxml + PanelSettings → <Screen>Screen.prefab
               screen prefabs → MenuScreens.prefab (nested instances + controller refs)
```

Two invariants worth preserving:

1. **Variables before declarations.** `:root` is collected from every stylesheet in
   the folder before any declaration is mapped, because `var()` may reference a
   variable defined in another file.
2. **Styles sit beside the document.** `<ui:Style src="base.uss">` resolves
   relative to the `.uxml`, so every screen folder carries its own copy of each
   stylesheet. Copying is cheaper than inventing a path scheme USS cannot express.

---

## 4. Conversion contract

### 4.1 HTML → UXML elements

| HTML | UXML | Notes |
| --- | --- | --- |
| `div section header footer main aside nav ul ol li form article figure fieldset hr svg` | `ui:VisualElement` | Unknown tags also land here, with a warning |
| `h1–h6 p span strong em small code pre label time a b i u blockquote cite legend abbr kbd output mark sub sup` | `ui:Label` | `text` attribute |
| `button` | `ui:Button` | Direct text goes in `text`; child elements become children |
| `input[type=text\|search\|email\|password]` | `ui:TextField` | `value`, `placeholder` |
| `input[type=range]` | `ui:Slider` | `min`→`low-value`, `max`→`high-value`, `value` |
| `input[type=number]` | `ui:IntegerField` | `value`, `min`, `max` passed through |
| `input[type=checkbox]` | `ui:Toggle` | `checked=""` |
| `input[type=radio]` | `ui:RadioButton` | `checked=""`, `name` |
| `input[type=submit\|button\|reset]` | `ui:Button` | |
| `select` | `ui:DropdownField` | Option text joined into `choices="a,b,c"`, children dropped |
| `progress` | `ui:ProgressBar` | `value`, `max` |
| `img` | `ui:VisualElement` | Warns: no sprite to point at |
| `script style head meta link title base template noscript` | dropped | |

Attributes: `class` (plus `data-key:<value>` appended as a class), `id`, then a
passthrough list — `for href name type value min max placeholder datetime
aria-label title alt role`. Everything else is dropped silently, which is the
behaviour the fixture's validator expects.

**Mixed content.** A `Label` whose children include elements emits its text runs
*and* inline children as ordered sibling labels, and gets
`style="flex-direction: row; flex-wrap: wrap;"`. Putting all runs into one `text`
attribute (the Node converter's approach) loses order and draws phrases on top of
each other. Whitespace is collapsed to single spaces; text is XML-escaped.

### 4.2 CSS → USS

| CSS | USS | Rule |
| --- | --- | --- |
| `display` | `display` | `flex`/`none`; anything else becomes `flex` |
| `flex-direction justify-content align-items align-self align-content flex-wrap flex-grow flex-shrink flex-basis row-gap column-gap opacity white-space` | same | copied |
| `gap` | `row-gap` + `column-gap` | shorthand split |
| `flex: 1` | `flex-grow: 1; flex-shrink: 1` | shorthand split |
| `padding margin` | four sides | 1–4 value expansion |
| `border [side]` | `border-width` + `border-color` | style (`solid`) and spread dropped; `none` → width 0 |
| `border-radius` | four corner radii | 1–4 value expansion |
| `background` | `background-color` + `background-repeat` | colour token + `no-repeat` |
| `box-shadow` | `--unity-shadow-offset-x/-y/-blur-radius/color` | 4th length (spread) dropped |
| `font` | `-unity-font` + `font-size` + `-unity-font-style` | family looked up in `config.json` |
| `font-family` | `-unity-font` | unmapped family warns |
| `font-weight` | `-unity-font-style` | ≥600 → `bold` |
| `text-align` | `-unity-text-align` | `left` → `middle-left`, etc. |
| `overflow` | `overflow` | `visible` kept, everything else `hidden` |
| `width height min-* max-* top right bottom left letter-spacing font-size` | same | via length conversion |
| `position` | same | `static` dropped |
| `line-height text-transform cursor transition transform content outline z-index background-image text-decoration list-style visibility box-sizing overflow-x/y` | dropped | no USS equivalent |
| `--*` | dropped | consumed by `var()` resolution only |

Units: `px` kept; `rem`/`em` × 16px; `pt` × 96/72; `%` kept; `vh`/`vw` →
percent (a panel is always the full screen — dropping them silently shrinks a
`min-height: 100vh` root to content height).

Colours: 3-digit hex expanded; 8-digit hex folded into `rgba()`; modern
`rgb(a b c / d)` normalised to `rgba(a, b, c, d)`. **One unparseable value makes
Unity discard the entire stylesheet**, so this normalisation is load-bearing.

`var()`: substituted from `:root` values, including chained definitions and
`var(--a, var(--b))` fallbacks; unresolved references are left verbatim and
counted in the tool's report.

### 4.3 Output layout

```
Assets/UI/<TitleCased page name>/
  <page>.uxml        # no XML declaration, <ui:Style src> per linked sheet, tab indent
  <sheetname>.uss    # one per linked stylesheet, in document order
```

Files are rewritten in place, which preserves `.meta` GUIDs, which preserves
scene and prefab references.

---

## 5. Prefabs and scene

**Why prefabs and not objects.** Unity does not serialize a `VisualElement` tree
into a prefab. The tree lives in the `.uxml`; the prefab carries the wiring. This
is the reason conversion must emit assets rather than building objects directly.

| Prefab | Contents |
| --- | --- |
| `<Screen>Screen.prefab` | Root GameObject + `UIDocument` → generated `.uxml` + shared `PanelSettings` |
| `MenuScreens.prefab` | Root + `MenuScreens` with three nested `<Screen>Screen` instances, controller fields wired |

**Scene** (`Assets/Scenes/SampleScene.unity`): `Screens` (controller) with
`IndexScreen` / `PauseScreen` / `SettingsScreen` children, each a `UIDocument`
using the shared panel.

`MenuScreens` rules:

- One screen visible at a time by toggling `rootVisualElement.style.display`.
  **Not** `SetActive(false)`: disabling a `UIDocument` rebuilds its tree on
  re-enable, which drops the click handlers bound at `Start`.
- Links come from the page itself: `data-key:*` classes on buttons
  (`settings`, `main-menu`) and the `back-link` label that was an `<a>`. Labels
  are made clickable with a `Clickable` manipulator.
- `Continue` / `New Game` open the pause screen because there is no gameplay
  behind the fixture.

---

## 6. Known limitations (measured, not guessed)

| # | Symptom | Cause | Workaround / next |
| --- | --- | --- | --- |
| L1 | Long text runs past its container | A `Label` inside a `flex-direction: row` parent keeps its natural width; USS has no auto-wrap for block children | Set `white-space: normal` + width constraints, or emit rich text; see N2 |
| L2 | `.settings-content` clips its last rows | `overflow: auto` becomes `hidden`; UI Toolkit scrolls only via `ScrollView` | Build the `ScrollView` in the converter when it sees `overflow: auto` (N3) |
| L3 | Pause overlay is translucent over the skybox | The page's 78% backdrop is intentional; `html, body` background selectors never match, so the root needs the colour | Fixture carries `background-color: var(--bg)` on `.screen`; document it as an authoring rule |
| L4 | Slider rows sit slightly tighter than HTML | Control heights differ between engines; `flex-shrink: 0` on `.setting-row` is a fixture-side compensation | Accept; treat as engine difference |
| L5 | `data-key:*` classes cannot be targeted from USS | `:` is a pseudo-class separator in USS selectors | Read them from C# with `GetClasses()`; do not add USS rules for them |
| L6 | Semantic HTML that has no equivalent (`table`, `dl`, `details`) | Not mapped | Currently lands as `VisualElement` division soup; see N5 |
| L7 | Images never appear | `img` has no sprite to bind | Needs an asset-resolution step (N6) |

---

## 7. Verification playbook

```bash
node tools/validate.mjs          # static: XML validity, element/attribute validity,
                                 # class coverage, Style targets, USS url() targets
node tools/unity-test.mjs        # clears console, refreshes, loads every UXML, walks the
                                 # tree: elements, dropdown choices, slider ranges
```

Play-mode capture (the only way to see real pixels):

```
manage_editor stop → sleep → [edit-mode asset assignment] → manage_editor play →
focus GameView → ScreenCapture.CaptureScreenshot("_renders/<name>.png") → poll for file
```

Definition of done for any change to the converter:

1. `validate.mjs`: 0 errors; warning count explained (currently 27, all in the
   known-loss list).
2. `unity-test.mjs`: 3/3 loaded, 0 failures, 0 console errors, slider/dropdown
   assertions unchanged.
3. A capture of each touched screen, at 1920×1080, inspected by eye against the
   browser rendering of the same page.
4. If prefabs were rebuilt, `PrefabUtility.LoadPrefabContents` shows the
   controller refs and each `UIDocument` pointing at the right asset.

---

## 8. Working through unity-mcp (hard-won notes)

Transport: HTTP JSON-RPC at `http://127.0.0.1:8080/mcp`, driven by
`node tools/unity-mcp.mjs call <tool> <json>` (`tools`, `schema <tool>`,
`resources`, `read <uri>` are the other verbs). Unity 6000.3.17f1, unity-mcp
3.4.11.

**Payloads**
- JSON goes as an argv argument, not stdin: `... call execute_code "$(cat .tmp/run.json)"`.
- Build the payload with `node -e '...JSON.stringify({action,code})'`. Hand-escaping
  C# strings into JSON is a reliable source of `Newline in constant` errors.
- Write scratch payloads **inside the project** (`.tmp/`); on this host `/tmp`
  resolves to `E:\tmp` for Node and fails with `ENOENT`.
- `execute_code` needs `{"action":"execute","code":"…"}` and returns
  `data.result`, with `compiler: "codedom"`.

**Inside `execute_code`**
- Roslyn is not used: this is CodeDom, effectively C# 5. No string interpolation,
  no local functions, no expression-bodied members, no `?.`. Lambdas, `var`,
  generics and `System.Linq` are fine.
- Fully qualify everything: `UnityEngine.UIElements.UQueryExtensions.Query<T>(root, name, className)`
  (the `Q` extension needs its static class in scope),
  `UnityEditor.AssetDatabase`, etc.
- Snippets can reference project assemblies once compiled: `MenuScreens`
  (Assembly-CSharp) and `HtmlToUuitk.*` (Assembly-CSharp-Editor) are both visible.
- `root.styleSheets` is not enumerable — index it: `for (i < set.count) set[i]`.
- Some calls are blocked by a pattern filter (`AssetDatabase.DeleteAsset` is
  refused). Delete through the filesystem, then `AssetDatabase.Refresh()`.

**Compilation**
- Adding a `.cs` file does not import or compile it. Force it:
  `AssetDatabase.Refresh(ForceSynchronousImport)` +
  `CompilationPipeline.RequestScriptCompilation()`, then poll
  `EditorApplication.isCompiling` and `EditorUtility.scriptCompilationFailed`.
- `refresh_unity {compile:"request"}` answers `compile_started: false` when the
  editor window is not the active application (`InternalEditorUtility.isApplicationActive`
  is false). Entering play mode also kicks a pending compile.
- `scriptCompilationFailed` goes stale-true until the next compile finishes; poll
  until `compiling == false && failed == false`.
- One bad detail fails the whole document, silently: a duplicate attribute gave
  `'name' is a duplicate attribute name`, and the panel simply rendered empty.
  Always re-check `Instantiate().childCount` after a converter change.

**Console**
- `read_console {"action":"get","page_size":N}`; `types` filtering exists but the
  unfiltered read is more useful. Import errors only appear right after the
  import, so re-import and read immediately.
- `"UI was recreated and no companion MonoBehaviour found"` and TCS-completion
  chatter are noise from the bridge, not project errors.

**Play mode and rendering**
- Editor-mode `manage_ui render_ui` returns a blank white image (it only assigns
  a RenderTexture). Play mode with `ScreenCapture` is the only real capture.
- The **Game view must be focused** or Unity stops drawing: focus with
  `EditorWindow.GetWindow(Type.GetType("UnityEditor.GameView,UnityEditor"), false, "Game", true)`,
  `Focus()`, `Repaint()`, then `ScreenCapture.CaptureScreenshot(path)`; poll for
  the file.
- Frames only render **after** that focus, so a UI change made mid-session is not
  in the captured image even though `resolvedStyle` already reports it. One
  screen per play session is the reliable pattern; `display` flags are the
  cheap way to prove switching logic.
- `UIDocument.visualTreeAsset` assigned at runtime does **not** rebuild the tree.
  Assign in edit mode, then play. Consequence for Scene/prefab work: swapping
  screens is a `display` toggle, never a tree rebuild.
- `manage_scene {"action":"save"}` persists the scene; play-mode changes are lost
  on stop unless saved in edit mode.

---

## 9. Next steps

Ordered by how much they move G2/G4 — the criteria furthest from "good".

**N1 — Text that wraps where HTML wraps.** *(highest value)*
Labels in row containers overflow (L1). Options: emit `white-space: normal` plus
`flex-shrink: 1; min-width: 0` for text-heavy classes; or generate a
`TextElement` with rich-text markup and inline styling for mixed content. Needs a
measurement pass on the pause quote and the footer to keep the fix honest.

**N2 — `overflow: auto` → `ScrollView`.**
The converter knows the CSS; emitting a `ScrollView` wrapper (and its `contentContainer`
class) is a contained change in `UxmlWriter`. Fixes the clipped settings rows (L2).

**N3 — Layout parity harness.**
Render each screen in the editor, and screenshot the same HTML with a headless
browser at 1920×1080; compare element boxes (position/size) rather than pixels.
Gives G4 a number instead of an opinion, and would catch regressions from N1/N2.
This is the piece that turns "fair" into "measurably fair".

**N4 — Report panel in the window instead of `Debug.Log` only.**
The window already lists per-screen warnings; add counts per category
(dropped property, unmapped font, dropped shadow spread) so a user sees what
their HTML lost without reading the README.

**N5 — More element mappings.** `table`/`tr`/`td` (grid), `details`/`summary`
(Foldout), `dl`/`dt`/`dd`, `figure`/`figcaption` semantics. Each is a small
`UxmlWriter` case plus a fixture page.

**N6 — Images.** Resolve `img[src]` to a `Texture2D`/`Sprite` in the project if a
matching asset exists, warn otherwise; `background-image` similarly.

**N7 — Cascade and inheritance sanity.**
Selector order is copied through, so USS does its own cascade. Not yet exercised:
two rules of different specificity setting the same property, `inherit`, and
`@media`. Worth a fixture page rather than code, at first.

**N8 — In-editor validation.** Run the subset of `validate.mjs` that matters
(unknown element, dropped attribute, unresolved var) inside the conversion so the
window can fail loudly instead of writing subtly wrong assets.

**N9 — Batch entry point for CI.** A static method
(`HtmlToUuitkConverter.ConvertFolder`) callable from `-executeMethod`, so the
converter can run headless in a Unity build job. The method already exists; it
needs a CLI wrapper and an exit code.

**N10 — Prefab variants per screen state.** The fixture has hover/focus/active
styling; a prefab variant per state would let designers compare without running.

---

## 10. Open questions

- Should the tool own `Assets/UI/` wholesale (clean stale screen folders), or only
  write what it converts? Today: only writes; deleting is manual.
- Is the Node converter still the reference for CLI/CI, or should the C# port
  become canonical there too (N9)? Both currently write the same files.
- Do we want `MenuScreens` generated per fixture (so the tool owns navigation), or
  is the hand-written controller the right boundary? Today: hand-written, generic
  over `data-key` and `back-link`.
- How should "good" be judged for typography — the theme values are applied, but
  Rubik metrics differ from the browser's fallback stack, so line breaks will not
  match exactly.
