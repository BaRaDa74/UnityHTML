#!/usr/bin/env node
// Converts every HTML fixture into UI Toolkit assets under Assets/UI/<Screen>/ so
// Unity imports them directly. Each screen folder carries its own base.uss and
// theme.uss because the UXML <ui:Style src> paths are relative to the .uxml.
//
// Usage: node tools/convert.mjs [--out <dir>] [--quiet]

import { spawnSync } from 'node:child_process';
import { mkdirSync, readFileSync, readdirSync, statSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const args = process.argv.slice(2);
const quiet = args.includes('--quiet');
const outIdx = args.indexOf('--out');
const outRoot = outIdx >= 0 ? resolve(root, args[outIdx + 1]) : join(root, 'Assets', 'UI');

const fixtures = join(root, 'html2uuitk-test');
const converter = join(root, 'node_modules', 'html2uuitk', 'convert.js');

const SCREENS = [
  { source: 'index.html', folder: 'Index' },
  { source: 'pause.html', folder: 'Pause' },
  { source: 'settings.html', folder: 'Settings' },
];

// Two upstream defects are repaired here so Unity imports the output cleanly:
// 1. html2uuitk emits bare <Style src="…"/>. The UXML importer tolerates it, but
//    UI Builder refuses to open a document whose Style elements are not
//    namespaced, so every generated screen gets the ui: prefix.
// 2. It appends `-unity-font-definition: none;` whenever a font is mapped, to
//    make the legacy `-unity-font` property win. Unity logs
//    "Expected (<resource> | <url>) but found 'none'" for it, so it is dropped.
// 3. <select> becomes DropdownField with <option> children, which Unity's UXML
//    importer drops without a word: the field ends up with zero choices. UXML
//    does support a `choices` attribute, so the options are hoisted into one.
// 4. `min`/`max` on a slider are HTML attributes; UI Toolkit wants
//    `low-value`/`high-value` and otherwise leaves the slider at 0..10.
// 5. CSS custom properties are copied through verbatim (`color: var(--text)`),
//    but UI Toolkit has no var() support and no custom-property declarations:
//    every such declaration is silently dropped. The :root variables are read
//    from the source CSS and substituted here.
const UXML_EDITS = [
  { what: 'ui: prefix on <Style>', apply: (s) => s.replace(/^([ \t]*)<Style /gm, '$1<ui:Style ') },
  { what: 'options hoisted into `choices`', apply: hoistOptions },
  { what: '`min`/`max` mapped to `low-value`/`high-value`', apply: mapSliderRange },
];

const USS_EDITS = [
  { what: 'dropped `-unity-font-definition: none`', apply: (s) => s.replace(/^[ \t]*-unity-font-definition: none;\r?\n/gm, '') },
  { what: 'CSS variables resolved (Unity has no var())', apply: (s) => resolveCssVariables(s) },
];

let unresolvedVars = 0;

// USS only understands comma-separated rgb()/rgba(); the modern space-and-slash
// form is a parse error, and one bad value makes Unity discard the whole sheet.
function normalizeColor(s) {
  return s
    .replace(/rgba?\(\s*([\d.]+)[\s,]+([\d.]+)[\s,]+([\d.]+)\s*\/\s*([\d.]+%?)\s*\)/g, 'rgba($1, $2, $3, $4)')
    .replace(/rgba?\(\s*([\d.]+)[\s,]+([\d.]+)[\s,]+([\d.]+)\s*\)/g, 'rgb($1, $2, $3)');
}

function cssVariables(files) {
  const vars = new Map();
  for (const file of files) {
    for (const block of readFileSync(file, 'utf8').matchAll(/:root\s*\{([^}]*)\}/g)) {
      for (const decl of block[1].matchAll(/(--[\w-]+)\s*:\s*([^;]+);/g)) vars.set(decl[1], normalizeColor(decl[2].trim()));
    }
  }
  return vars;
}

// Replaces var(--x) / var(--x, fallback) with literal values. `lookup` returns a
// string, or null when the name is unknown and there is no fallback.
function substituteVars(text, lookup) {
  let out = '';
  let i = 0;
  let missing = 0;
  for (;;) {
    const start = text.indexOf('var(', i);
    if (start < 0) {
      out += text.slice(i);
      return { text: out, missing };
    }
    out += text.slice(i, start);
    let depth = 0;
    let end = start + 4;
    for (; end < text.length; end++) {
      if (text[end] === '(') depth++;
      else if (text[end] === ')' && depth-- === 0) {
        end++;
        break;
      }
    }
    const inner = text.slice(start + 4, end - 1);
    let depth2 = 0;
    let comma = -1;
    for (let k = 0; k < inner.length; k++) {
      if (inner[k] === '(') depth2++;
      else if (inner[k] === ')') depth2--;
      else if (inner[k] === ',' && depth2 === 0) {
        comma = k;
        break;
      }
    }
    const name = (comma < 0 ? inner : inner.slice(0, comma)).trim();
    const fallback = comma < 0 ? null : inner.slice(comma + 1).trim();
    const value = lookup(name, fallback);
    if (value == null) {
      missing++;
      out += text.slice(start, end);
    } else {
      out += value;
    }
    i = end;
  }
}

function resolveCssVariables(uss) {
  const vars = cssVariables([join(fixtures, 'base.css'), join(fixtures, 'theme.css')]);
  const lookup = (name, fallback, stack = new Set()) => {
    const raw = vars.get(name);
    if (raw != null && !stack.has(name)) {
      const nextStack = new Set(stack).add(name);
      const resolved = substituteVars(raw, (n, fb) => lookup(n, fb, nextStack));
      if (resolved.missing === 0) return resolved.text.trim();
    }
    if (fallback != null) {
      const resolved = substituteVars(fallback, (n, fb) => lookup(n, fb, stack));
      return normalizeColor(resolved.text.trim());
    }
    return null;
  };
  const result = substituteVars(uss, lookup);
  unresolvedVars += result.missing;
  return normalizeColor(result.text);
}

function decodeEntities(s) {
  return s
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&quot;/g, '"')
    .replace(/&#39;/g, "'")
    .replace(/&amp;/g, '&');
}

// Each <option> wraps its text in a <ui:Label text="…">, so the label text is
// the value; anything else in the option body is flattened to plain text.
function hoistOptions(text) {
  return text.replace(
    /<ui:DropdownField([^>]*)>([\s\S]*?)<\/ui:DropdownField>/g,
    (whole, attrs, body) => {
      const options = [...body.matchAll(/<option[^>]*>([\s\S]*?)<\/option>/g)].map((m) => {
        const inner = m[1];
        const labelTexts = [...inner.matchAll(/<ui:Label[^>]*\btext="([^"]*)"/g)].map((x) => decodeEntities(x[1]));
        const raw = labelTexts.length ? labelTexts.join(' ') : decodeEntities(inner.replace(/<[^>]*>/g, ' '));
        return raw.replace(/\s+/g, ' ').trim();
      });
      if (!options.length) return whole;
      // `choices` is a comma-separated attribute value, and it must stay valid XML.
      if (options.some((o) => o.includes(',') || /["<>]/.test(o))) return whole;
      const rest = body.replace(/<option[^>]*>[\s\S]*?<\/option>/g, '').trim();
      if (rest) return whole; // child elements we would lose
      return `<ui:DropdownField${attrs} choices="${options.join(',')}" />`;
    },
  );
}

function mapSliderRange(text) {
  // Matches both <ui:Slider … /> and <ui:Slider …> (the converter emits the latter).
  return text.replace(/<(ui:Slider|ui:SliderInt)((?:\s+[\w:-]+="[^"]*")*)\s*(\/?)>/g, (whole, tag, attrs, selfClose) => {
    let next = attrs;
    if (!/\blow-value=/.test(next)) next = next.replace(/\bmin="([^"]*)"/, 'low-value="$1"');
    if (!/\bhigh-value=/.test(next)) next = next.replace(/\bmax="([^"]*)"/, 'high-value="$1"');
    if (next === attrs) return whole;
    return `<${tag}${next}${selfClose ? ' /' : ''}>`;
  });
}

function postProcess(file, edits) {
  let text = readFileSync(file, 'utf8');
  const applied = [];
  for (const edit of edits) {
    const next = edit.apply(text);
    if (next !== text) {
      text = next;
      applied.push(edit.what);
    }
  }
  if (applied.length) writeFileSync(file, text, 'utf8');
  return applied;
}

const summary = [];
let failed = false;

for (const screen of SCREENS) {
  const outDir = join(outRoot, screen.folder);
  mkdirSync(outDir, { recursive: true });

  const result = spawnSync(
    process.execPath,
    [
      converter,
      '-i', join(fixtures, screen.source),
      '--css', join(fixtures, 'base.css'), join(fixtures, 'theme.css'),
      '--config', join(fixtures, 'config.json'),
      '-o', outDir,
    ],
    { encoding: 'utf8' },
  );

  if (result.status !== 0) {
    failed = true;
    console.error(`\n✗ ${screen.source} (exit ${result.status})`);
    console.error(result.stdout?.trim());
    console.error(result.stderr?.trim());
    continue;
  }

  const warnings = (result.stderr ?? '')
    .split(/\r?\n/)
    .filter((l) => /warn|error|unsupported|unknown/i.test(l));
  const informational = (result.stderr ?? '')
    .split(/\r?\n/)
    .filter((l) => l.trim() && !warnings.includes(l));

  const uxml = readdirSync(outDir).find((f) => f.endsWith('.uxml'));
  const uxmlPath = join(outDir, uxml);
  const edits = [
    ...postProcess(uxmlPath, UXML_EDITS),
    ...readdirSync(outDir)
      .filter((f) => f.endsWith('.uss'))
      .flatMap((f) => postProcess(join(outDir, f), USS_EDITS)),
  ];

  summary.push({
    screen: screen.folder,
    source: screen.source,
    outDir,
    files: readdirSync(outDir).filter((f) => /\.(uxml|uss)$/.test(f)).sort(),
    bytes: statSync(uxmlPath).size,
    edits: [...new Set(edits)],
    warnings,
    informational,
  });

  if (warnings.length) failed = true;
}

for (const s of summary) {
  console.log(`\n${s.screen}  ${s.source} -> ${s.outDir.slice(root.length + 1)}`);
  console.log(`  ${s.files.join('  ')}`);
  console.log(`  ${s.bytes} bytes${s.edits.length ? `  (patched: ${s.edits.join('; ')})` : ''}`);
  if (s.warnings.length) s.warnings.forEach((w) => console.log(`  WARN ${w}`));
  if (!quiet) s.informational.forEach((i) => console.log(`  info ${i}`));
}

if (failed) {
  console.error('\nconversion finished with warnings/errors');
  process.exit(1);
}
console.log(`\n${summary.length} screens converted${unresolvedVars ? ` (${unresolvedVars} unresolved var() left as-is)` : ''}`);
