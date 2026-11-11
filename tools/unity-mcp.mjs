#!/usr/bin/env node
// Minimal dependency-free client for the unity-mcp (CoplayDev) streamable-HTTP server.
//
// Usage:
//   node tools/unity-mcp.mjs tools [filter]        list tools (with filter on name)
//   node tools/unity-mcp.mjs schema <tool>         print a tool's input schema
//   node tools/unity-mcp.mjs resources             list resources
//   node tools/unity-mcp.mjs read <uri>            read a resource
//   node tools/unity-mcp.mjs call <tool> [json]    call a tool
//   node tools/unity-mcp.mjs raw <json>            send an arbitrary JSON-RPC request
//
// Flags: --full (no truncation), --out <file> (write the result payload to a file)
// Env:   UNITY_MCP_URL (default http://127.0.0.1:8080/mcp)

import { writeFileSync } from 'node:fs';

const URL_ = process.env.UNITY_MCP_URL ?? 'http://127.0.0.1:8080/mcp';
const args = process.argv.slice(2);
const full = args.includes('--full');
const outIdx = args.indexOf('--out');
const outFile = outIdx >= 0 ? args[outIdx + 1] : null;
const argv = args.filter((a, i) => a !== '--full' && a !== '--out' && (outIdx < 0 || i !== outIdx + 1));

let sessionId = null;
let nextId = 1;

async function post(body) {
  const headers = {
    'Content-Type': 'application/json',
    Accept: 'application/json, text/event-stream',
  };
  if (sessionId) headers['Mcp-Session-Id'] = sessionId;

  const res = await fetch(URL_, { method: 'POST', headers, body: JSON.stringify(body) });
  const sid = res.headers.get('mcp-session-id');
  if (sid) sessionId = sid;

  const text = await res.text();
  if (!res.ok) {
    throw new Error(`HTTP ${res.status} ${res.statusText}\n${text.slice(0, 2000)}`);
  }
  if (!text.trim()) return null;

  // Responses may be plain JSON or SSE frames ("event: message\ndata: {...}").
  if (text.startsWith('{') || text.startsWith('[')) {
    const parsed = JSON.parse(text);
    return Array.isArray(parsed) ? parsed.at(-1) : parsed;
  }
  const frames = text
    .split(/\r?\n/)
    .filter((l) => l.startsWith('data:'))
    .map((l) => JSON.parse(l.slice(5).trim()));
  return frames.at(-1) ?? null;
}

async function rpc(method, params, { notify = false } = {}) {
  const msg = notify
    ? { jsonrpc: '2.0', method, params }
    : { jsonrpc: '2.0', id: nextId++, method, params };
  const res = await post(msg);
  if (!notify && res?.error) {
    throw new Error(`JSON-RPC error ${res.error.code}: ${res.error.message}`);
  }
  return res?.result ?? res;
}

async function connect() {
  await rpc('initialize', {
    protocolVersion: '2024-11-05',
    capabilities: {},
    clientInfo: { name: 'html2uuitk-test', version: '1.0.0' },
  });
  await rpc('notifications/initialized', {}, { notify: true });
}

function print(result) {
  const payload = result?.content
    ? result.content.map((c) => (c.type === 'text' ? c.text : `[${c.type}]`)).join('\n')
    : result?.contents
      ? result.contents.map((c) => c.text ?? `[${c.mimeType ?? c.type}]`).join('\n')
      : JSON.stringify(result, null, 2);

  if (outFile) {
    writeFileSync(outFile, payload, 'utf8');
    console.log(`wrote ${payload.length} chars to ${outFile}`);
    if (!full) console.log(payload.slice(0, 1500));
    return;
  }
  console.log(full || payload.length <= 8000 ? payload : `${payload.slice(0, 8000)}\n…[${payload.length - 8000} chars truncated; --full or --out to keep]`);
}

async function main() {
  const [cmd, ...rest] = argv;
  await connect();

  switch (cmd) {
    case 'tools': {
      const { tools = [] } = await rpc('tools/list', {});
      const filter = rest[0]?.toLowerCase();
      for (const t of tools) {
        if (filter && !t.name.toLowerCase().includes(filter)) continue;
        console.log(`${t.name} — ${(t.description ?? '').split('\n')[0].slice(0, 150)}`);
      }
      console.log(`\n${tools.length} tools total`);
      break;
    }
    case 'schema': {
      const { tools = [] } = await rpc('tools/list', {});
      const t = tools.find((x) => x.name === rest[0]);
      if (!t) throw new Error(`no tool named ${rest[0]}`);
      console.log(JSON.stringify({ description: t.description, inputSchema: t.inputSchema }, null, 2));
      break;
    }
    case 'resources': {
      const { resources = [] } = await rpc('resources/list', {});
      for (const r of resources) console.log(`${r.uri}\n    ${r.name} — ${(r.description ?? '').slice(0, 120)}`);
      break;
    }
    case 'read':
      print(await rpc('resources/read', { uri: rest[0] }));
      break;
    case 'call': {
      const toolArgs = rest[1] ? JSON.parse(rest[1]) : {};
      print(await rpc('tools/call', { name: rest[0], arguments: toolArgs }));
      break;
    }
    case 'raw':
      print(await rpc('raw', JSON.parse(rest[0])));
      break;
    default:
      console.error('usage: node tools/unity-mcp.mjs tools|resources|read|call|raw …');
      process.exit(2);
  }
}

main().catch((err) => {
  console.error(String(err.message ?? err));
  process.exit(1);
});
