#!/usr/bin/env node
// End-to-end conversion test against a running Unity Editor through unity-mcp.
//
//   1. clears the console and forces an asset refresh, so import diagnostics are
//      attributable to these files;
//   2. reads the console back (any error/warning fails the run);
//   3. loads every generated UXML with AssetDatabase, instantiates it and walks
//      the live VisualElement tree, reporting per-screen element counts and the
//      specific things this fixture is meant to exercise.
//
// Usage: node tools/unity-test.mjs [--json] [--no-refresh]

import { readdirSync, existsSync, statSync } from 'node:fs';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const args = process.argv.slice(2);
const asJson = args.includes('--json');
const doRefresh = !args.includes('--no-refresh');
const UI_ROOT = 'Assets/UI';

// --- minimal MCP client ------------------------------------------------------
const MCP_URL = process.env.UNITY_MCP_URL ?? 'http://127.0.0.1:8080/mcp';
let sessionId = null;
let rpcId = 1;

async function post(body) {
  const headers = { 'Content-Type': 'application/json', Accept: 'application/json, text/event-stream' };
  if (sessionId) headers['Mcp-Session-Id'] = sessionId;
  const res = await fetch(MCP_URL, { method: 'POST', headers, body: JSON.stringify(body) });
  const sid = res.headers.get('mcp-session-id');
  if (sid) sessionId = sid;
  const text = await res.text();
  if (!res.ok) throw new Error(`HTTP ${res.status}: ${text.slice(0, 800)}`);
  if (!text.trim()) return null;
  if (text.startsWith('{') || text.startsWith('[')) {
    const parsed = JSON.parse(text);
    return Array.isArray(parsed) ? parsed.at(-1) : parsed;
  }
  const frames = text.split(/\r?\n/).filter((l) => l.startsWith('data:')).map((l) => JSON.parse(l.slice(5).trim()));
  return frames.at(-1) ?? null;
}

async function rpc(method, params, notify = false) {
  const res = await post(notify ? { jsonrpc: '2.0', method, params } : { jsonrpc: '2.0', id: rpcId++, method, params });
  if (res?.error) throw new Error(`JSON-RPC ${res.error.code}: ${res.error.message}`);
  return res?.result;
}

async function connect() {
  await rpc('initialize', {
    protocolVersion: '2024-11-05',
    capabilities: {},
    clientInfo: { name: 'html2uuitk-unity-test', version: '1.0.0' },
  });
  await rpc('notifications/initialized', {}, true);
}

async function callTool(name, toolArgs) {
  const result = await rpc('tools/call', { name, arguments: toolArgs });
  const text = result?.content?.find((c) => c.type === 'text')?.text ?? '';
  let parsed;
  try {
    parsed = JSON.parse(text);
  } catch {
    parsed = null;
  }
  return { text, parsed, isError: Boolean(result?.isError) };
}

// --- the C# probe -----------------------------------------------------------
// CodeDom (C# 5) compatible: no local functions, no LINQ, no string interpolation.
const PROBE = (paths) => `
var paths = new string[] { ${paths.map((p) => `"${p}"`).join(', ')} };
var sb = new System.Text.StringBuilder();
foreach (var path in paths) {
    var vta = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.VisualTreeAsset>(path);
    if (vta == null) { sb.AppendLine("FAIL|" + path + "|VisualTreeAsset did not load"); continue; }
    var root = vta.Instantiate();
    var all = UnityEngine.UIElements.UQueryExtensions.Query<UnityEngine.UIElements.VisualElement>(root).ToList();
    var counts = new System.Collections.Generic.Dictionary<string, int>();
    int buttons = 0, dropdowns = 0, emptyDropdowns = 0, optionsLeft = 0, colonClasses = 0;
    var dropdownSizes = new System.Collections.Generic.List<int>();
    var sliderRanges = new System.Collections.Generic.List<string>();
    var richButtons = new System.Collections.Generic.List<string>();
    foreach (var e in all) {
        var t = e.GetType().Name;
        if (!counts.ContainsKey(t)) counts[t] = 0;
        counts[t] = counts[t] + 1;
        if (t == "Button") {
            buttons = buttons + 1;
            var inner = UnityEngine.UIElements.UQueryExtensions.Query<UnityEngine.UIElements.Label>(e).ToList();
            if (inner.Count > 0) richButtons.Add(inner[0].text + ":" + inner.Count);
        }
        if (t == "Slider") {
            var sl = (UnityEngine.UIElements.Slider)e;
            sliderRanges.Add(sl.lowValue + "-" + sl.highValue);
        }
        if (t == "DropdownField") {
            dropdowns = dropdowns + 1;
            var df = (UnityEngine.UIElements.DropdownField)e;
            int n = df.choices == null ? 0 : df.choices.Count;
            dropdownSizes.Add(n);
            if (n == 0) emptyDropdowns = emptyDropdowns + 1;
        }
        if (t == "Option") optionsLeft = optionsLeft + 1;
        foreach (var c in e.GetClasses()) {
            if (c.IndexOf(':') >= 0) { colonClasses = colonClasses + 1; break; }
        }
    }
    var keys = new System.Collections.Generic.List<string>(counts.Keys);
    keys.Sort();
    var parts = new System.Collections.Generic.List<string>();
    foreach (var k in keys) parts.Add(k + "=" + counts[k]);
    var line = "OK|" + path + "|elements=" + all.Count + "|buttons=" + buttons + "|dropdowns=" + dropdowns
        + "|emptyDropdowns=" + emptyDropdowns + "|options=" + optionsLeft
        + "|colonClasses=" + colonClasses
        + "|richButtons=[" + string.Join("; ", richButtons.ToArray()) + "]"
        + "|sliderRanges=[" + string.Join(",", sliderRanges.ToArray()) + "]"
        + "|dropdownSizes=[" + string.Join(",", dropdownSizes.ConvertAll(delegate(int v) { return v.ToString(); }).ToArray()) + "]"
        + "|types=" + string.Join(" ", parts.ToArray());
    sb.AppendLine(line);
    var labels = UnityEngine.UIElements.UQueryExtensions.Query<UnityEngine.UIElements.Label>(root).ToList();
    var firstTexts = new System.Collections.Generic.List<string>();
    for (int i = 0; i < labels.Count && i < 6; i++) firstTexts.Add("[" + labels[i].text.Replace("\\n", " ") + "]");
    sb.AppendLine("TEXT|" + path + "|" + string.Join(" ", firstTexts.ToArray()));
}
return sb.ToString();
`;

// --- run --------------------------------------------------------------------
if (!existsSync(join(root, UI_ROOT))) {
  console.error(`no ${UI_ROOT} — run \`npm run convert\` first`);
  process.exit(1);
}

const screens = [];
for (const dir of readdirSync(join(root, UI_ROOT))) {
  const abs = join(root, UI_ROOT, dir);
  if (!existsSync(abs) || !statSync(abs).isDirectory()) continue;
  if (!readdirSync(abs).some((f) => f.endsWith('.uxml'))) continue;
  const uxml = readdirSync(abs).find((f) => f.endsWith('.uxml'));
  screens.push(`${UI_ROOT}/${dir}/${uxml}`);
}

await connect();

const report = { screens, console: { errors: [], warnings: [] }, probe: [], failures: [] };

if (doRefresh) {
  await callTool('read_console', { action: 'clear' });
  const r = await callTool('refresh_unity', { mode: 'force', scope: 'assets', wait_for_ready: true });
  if (r.isError || r.parsed?.success === false) report.failures.push(`refresh_unity: ${r.text.slice(0, 300)}`);

  const logs = await callTool('read_console', { action: 'get', types: ['error', 'warning'], count: '100' });
  const entries = logs.parsed?.data ?? [];
  for (const e of entries) {
    if (/error|warning/i.test(e) && /error/i.test(e)) report.console.errors.push(e);
    else report.console.warnings.push(e);
  }
  if (!entries.length) report.console.clean = true;
}

const probe = await callTool('execute_code', { action: 'execute', code: PROBE(screens), safety_checks: false });
if (probe.isError || probe.parsed?.success === false) {
  const errors = probe.parsed?.data?.errors ?? probe.parsed?.message ?? probe.text;
  report.failures.push(`execute_code: ${JSON.stringify(errors).slice(0, 800)}`);
}
const output = String(probe.parsed?.data?.result ?? probe.parsed?.data ?? '');
for (const line of output.split(/\r?\n/)) {
  if (line.startsWith('OK|') || line.startsWith('TEXT|')) report.probe.push(line);
  else if (line.startsWith('FAIL|')) report.failures.push(line);
}

if (asJson) {
  console.log(JSON.stringify(report, null, 2));
} else {
  console.log('Unity: ' + (doRefresh ? 'refreshed assets' : 'no refresh'));
  if (report.console.clean) console.log('Console: clean (no errors, no warnings)');
  for (const e of report.console.errors) console.log('ERROR ' + e.replace(/\n/g, ' | '));
  for (const w of report.console.warnings) console.log('warn  ' + w.replace(/\n/g, ' | '));

  for (const line of report.probe) {
    const [kind, path, ...rest] = line.split('|');
    console.log(`\n${relative(UI_ROOT, path)}   [${kind}]`);
    for (const r of rest) console.log('  ' + r);
  }
  for (const f of report.failures) console.log('\nFAIL ' + f);
  const imported = report.probe.filter((l) => l.startsWith('OK|')).length;
  console.log(`\n${imported}/${screens.length} screens loaded, ${report.failures.length} failure(s), ${report.console.errors.length} console error(s)`);
}

if (report.failures.length || report.console.errors.length) process.exit(1);
