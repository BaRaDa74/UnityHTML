#!/usr/bin/env node
// Static checks on the converted UI Toolkit assets, runnable without Unity.
// Catches the classes of defect that only show up as an empty/odd panel after
// import: unknown elements, attributes Unity silently ignores, classes with no
// USS rule, missing <ui:Style> files, and USS asset paths that do not exist.
//
// Usage: node tools/validate.mjs [--strict] [--json]
//   --strict  treat warnings as failures
//   --json    machine-readable report

import { existsSync, readFileSync, readdirSync, statSync } from 'node:fs';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const args = process.argv.slice(2);
const strict = args.includes('--strict');
const asJson = args.includes('--json');
const uiRoot = join(root, 'Assets', 'UI');

// UI Toolkit element inventory. Anything else is a hard import failure.
const ELEMENTS = {
  UXML: ['xmlns:ui', 'xmlns:uie', 'editor-extension-mode'],
  Style: ['src'],
  VisualElement: [],
  Label: ['text', 'enable-rich-text', 'display-tooltip-when-elided', 'emoji-fallback-support', 'parse-escape-sequences'],
  Button: ['text'],
  TextField: ['value', 'label', 'max-length', 'multiline', 'password', 'placeholder-text', 'read-only', 'is-delayed', 'vertical-scroller-visibility', 'keyboard-type'],
  IntegerField: ['value', 'label'],
  FloatField: ['value', 'label'],
  Slider: ['value', 'low-value', 'high-value', 'direction', 'page-size', 'show-input-field', 'inverted'],
  SliderInt: ['value', 'low-value', 'high-value', 'direction', 'page-size', 'show-input-field', 'inverted'],
  MinMaxSlider: ['value', 'low-limit', 'high-limit'],
  Toggle: ['value', 'label', 'text'],
  RadioButton: ['value', 'label', 'text'],
  DropdownField: ['value', 'label', 'index', 'choices'],
  ProgressBar: ['value', 'low-value', 'high-value', 'title'],
  ScrollView: ['horizontal-scroller-visibility', 'vertical-scroller-visibility', 'mode', 'elasticity'],
  Foldout: ['text', 'value', 'toggle-on-label-click'],
};

const COMMON = ['name', 'class', 'style', 'tooltip', 'picking-mode', 'focusable', 'tabindex', 'enabled'];

const findings = [];
const add = (level, file, line, message) => findings.push({ level, file, line, message });

function parseAttributes(raw) {
  const attrs = {};
  for (const m of raw.matchAll(/([\w:-]+)\s*=\s*"([^"]*)"/g)) attrs[m[1]] = m[2];
  return attrs;
}

function ussClasses(files) {
  const classes = new Set();
  for (const file of files) {
    const text = readFileSync(file, 'utf8').replace(/\/\*[\s\S]*?\*\//g, '');
    for (const m of text.matchAll(/\.(-?[A-Za-z_][\w-]*)/g)) classes.add(m[1]);
  }
  return classes;
}

function checkUss(file) {
  const lines = readFileSync(file, 'utf8').split(/\r?\n/);
  lines.forEach((line, i) => {
    // Asset references must resolve; `project://database/` is project-relative,
    // a bare path is relative to the stylesheet.
    for (const m of line.matchAll(/url\(\s*["']?([^"')]+)["']?\s*\)/g)) {
      const ref = m[1];
      if (/^(data:|https?:)/.test(ref)) continue;
      const target = ref.startsWith('project://database/')
        ? join(root, ref.slice('project://database/'.length))
        : join(dirname(file), ref);
      if (!existsSync(target)) {
        add('error', file, i + 1, `url() points at a missing asset: ${ref}`);
      }
    }
    // A legacy -unity-font without a font definition also needs the font to exist.
    if (/-unity-font-definition:\s*none/.test(line)) {
      add('warning', file, i + 1, '-unity-font-definition: none is not a valid value; Unity warns and ignores it');
    }
  });
}

// Structure checks that catch a broken post-processing pass: attribute values may
// not contain '<', and open/close tags must balance.
function checkXmlStructure(file, text) {
  const lines = text.split(/\r?\n/);
  lines.forEach((line, i) => {
    for (const m of line.matchAll(/=\s*"([^"]*)"/g)) {
      if (m[1].includes('<')) add('error', file, i + 1, `attribute value contains an unescaped '<': ${m[1].slice(0, 60)}`);
    }
  });

  const stack = [];
  for (const m of text.matchAll(/<(\/?)([\w:.-]+)((?:"[^"]*"|[^"<>])*?)(\/?)>/g)) {
    const [whole, closing, name, , selfClosing] = m;
    if (whole.startsWith('<!--') || whole.startsWith('<?')) continue;
    if (closing) {
      const open = stack.pop();
      if (open !== name) {
        add('error', file, 1, `closing </${name}> does not match <${open ?? 'nothing'}>`);
        return;
      }
    } else if (!selfClosing) {
      stack.push(name);
    }
  }
  if (stack.length) add('error', file, 1, `unclosed element(s): ${stack.join(', ')}`);
}

function checkUxml(file, screensOnDisk) {
  const lines = readFileSync(file, 'utf8').split(/\r?\n/);
  checkXmlStructure(file, lines.join('\n'));
  const dir = dirname(file);
  const classes = ussClasses(readdirSync(dir).filter((f) => f.endsWith('.uss')).map((f) => join(dir, f)));
  const ignored = new Map(); // tag -> Map(attribute -> count)
  const orphanClasses = new Map(); // class -> first line

  lines.forEach((line, i) => {
    for (const m of line.matchAll(/<\/?((?:ui:)?[A-Za-z_][\w.]*)((?:\s+[\w:-]+\s*=\s*"[^"]*")*)\s*(\/?)>/g)) {
      const tag = m[1].replace(/^ui:/, '');
      const attrs = parseAttributes(m[2]);
      const lineNo = i + 1;

      if (!(tag in ELEMENTS)) {
        add('error', file, lineNo, `unknown element <${m[1]}> (not a UI Toolkit element)`);
        continue;
      }
      if (/^</.test(m[0]) && !m[0].startsWith('</') && !/^(ui:|UXML)/.test(m[1]) && !m[1].startsWith('ui:')) {
        add('error', file, lineNo, `<${m[1]}> is not namespaced with ui:`);
      }

      for (const [name, value] of Object.entries(attrs)) {
        if (name.startsWith('xmlns') || COMMON.includes(name)) continue;
        if (ELEMENTS[tag].includes(name)) continue;
        if (name === 'text' && tag !== 'UXML') continue; // text exists on every TextElement
        if (name === 'label' && !ELEMENTS[tag].length) continue;
        if (!ignored.has(tag)) ignored.set(tag, new Map());
        const perAttr = ignored.get(tag);
        perAttr.set(name, (perAttr.get(name) ?? 0) + 1);
      }

      if (attrs.class) {
        for (const cls of attrs.class.split(/\s+/).filter(Boolean)) {
          if (!classes.has(cls) && !orphanClasses.has(cls)) orphanClasses.set(cls, lineNo);
        }
      }

      if (tag === 'Style') {
        const src = attrs.src ?? '';
        if (!existsSync(join(dir, src))) add('error', file, lineNo, `<ui:Style src="${src}"> does not exist next to the .uxml`);
      }
    }
  });

  // One aggregated line per tag: HTML attributes Unity has no UXML counterpart for.
  for (const [tag, perAttr] of ignored) {
    const attrs = [...perAttr.entries()].map(([name, n]) => (n > 1 ? `${name}×${n}` : name)).join(', ');
    add('warning', file, 1, `<${tag}> ignores HTML-only attribute(s): ${attrs}`);
  }
  if (orphanClasses.size) {
    const list = [...orphanClasses.entries()].map(([cls, line]) => `${cls} (line ${line})`).join(', ');
    add('warning', file, 1, `classes with no rule in the sibling .uss files: ${list}`);
  }

  const text = readFileSync(file, 'utf8');
  if (text.includes('<Style') && !text.includes('<ui:Style')) {
    add('error', file, 1, 'bare <Style> element; UI Builder refuses to open documents without the ui: prefix');
  }
  if (!screensOnDisk.includes(file)) add('warning', file, 1, 'screen is not part of the conversion list');
}

if (!existsSync(uiRoot)) {
  console.error(`no ${relative(root, uiRoot)} — run \`npm run convert\` first`);
  process.exit(1);
}

const screens = [];
for (const dirName of readdirSync(uiRoot)) {
  const dir = join(uiRoot, dirName);
  if (!existsSync(dir) || !statSync(dir).isDirectory()) continue;
  if (!readdirSync(dir).some((f) => f.endsWith('.uxml'))) continue;
  for (const f of readdirSync(dir)) {
    const full = join(dir, f);
    if (f.endsWith('.uxml')) screens.push(full);
    else if (f.endsWith('.uss')) checkUss(full);
  }
}
for (const file of screens) checkUxml(file, screens);

const errors = findings.filter((f) => f.level === 'error');
const warnings = findings.filter((f) => f.level === 'warning');

if (asJson) {
  console.log(JSON.stringify({ screens: screens.map((s) => relative(root, s)), errors, warnings }, null, 2));
} else {
  // Identical findings repeat per occurrence; one line each with a count is
  // far easier to read than a hundred near-identical warnings.
  const groups = new Map();
  for (const f of findings) {
    const key = `${f.level}|${f.file}|${f.message}`;
    if (!groups.has(key)) groups.set(key, { ...f, lines: [f.line] });
    else groups.get(key).lines.push(f.line);
  }
  for (const f of [...groups.values()].sort(
    (a, b) => relative(root, a.file).localeCompare(relative(root, b.file)) || a.lines[0] - b.lines[0] || a.level.localeCompare(b.level),
  )) {
    const level = f.level === 'error' ? 'ERROR' : 'warn ';
    const where = `${relative(root, f.file)}:${f.lines[0]}${f.lines.length > 1 ? ` (+${f.lines.length - 1})` : ''}`;
    console.log(`${level} ${where}  ${f.message}`);
  }
  console.log(
    `\n${screens.length} screens, ${errors.length} error(s), ${warnings.length} warning(s)`,
  );
  if (!findings.length) console.log('all screens valid');
}

if (errors.length || (strict && warnings.length)) process.exit(1);
