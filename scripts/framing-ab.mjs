#!/usr/bin/env node
// framing-ab.mjs — proves scripts/mcp-server.mjs speaks the MCP stdio wire format.
//
// MCP spec 2025-06-18, Transports/stdio:
//   "Messages are delimited by newlines, and MUST NOT contain embedded newlines."
// There is no Content-Length framing in MCP stdio — that is LSP.
//
// Usage:
//   node scripts/framing-ab.mjs spec       -> must receive a valid initialize RESULT
//   node scripts/framing-ab.mjs lsp        -> must NOT receive a result; -32700 is correct
//   node scripts/framing-ab.mjs negotiate  -> every supported version echoes; unsupported falls back
//   node scripts/framing-ab.mjs all        -> all three, non-zero if any fails
//
// This is an INDEPENDENT implementation of the wire format. scripts/smoke-mcp.mjs mirrors the
// server's framing (so it cannot prove conformance by itself — a matched pair of wrong
// implementations is exactly how the Content-Length framing survived). This file is the gate
// that can actually catch a framing regression.
//
// NOTE on the `lsp` assertion: it deliberately does NOT assert "zero bytes". A server that
// rejects a malformed line with -32700 is behaving correctly — asserting the byte count would
// test the wrong property and fail a correct implementation.
import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";
import path from "node:path";

const MODES = ["spec", "lsp", "negotiate", "all"];
const mode = process.argv[2];
if (!MODES.includes(mode)) {
  console.error(`usage: node scripts/framing-ab.mjs <${MODES.join("|")}>`);
  process.exit(2);
}

const here = path.dirname(fileURLToPath(import.meta.url));
const WRAPPER = path.join(here, "mcp-server.mjs");

// Guard the override: Number("") is 0 and Number("abc") is NaN, either of which would make
// setTimeout fire immediately and produce a false failure.
const rawWait = process.env.FRAMING_AB_WAIT_MS;
const parsedWait = Number.parseInt(rawWait ?? "", 10);
const WAIT_MS = Number.isFinite(parsedWait) && parsedWait > 0 ? parsedWait : 4000;
if (rawWait !== undefined && WAIT_MS !== Number(rawWait)) {
  console.error(`warn: ignoring invalid FRAMING_AB_WAIT_MS=${JSON.stringify(rawWait)}; using ${WAIT_MS}ms`);
}

const SUPPORTED = ["2025-06-18", "2025-03-26", "2024-11-05"];
const NEWEST = SUPPORTED[0];

function initRequest(id, protocolVersion) {
  return JSON.stringify({
    jsonrpc: "2.0", id, method: "initialize",
    params: { protocolVersion, capabilities: {}, clientInfo: { name: "framing-ab", version: "1.0" } },
  });
}

// Spawn the wrapper, write `payload`, collect stdout for WAIT_MS, always kill the child.
function probe(payload) {
  return new Promise((resolve) => {
    const child = spawn(process.execPath, [WRAPPER], {
      stdio: ["pipe", "pipe", "pipe"],
      env: {
        ...process.env,
        KANBANFLOW_API_URL: process.env.KANBANFLOW_API_URL ?? "http://127.0.0.1:5148",
        KANBANFLOW_MCP_TIMEOUT_MS: "5000",
      },
    });
    let out = "";
    let settled = false;
    const finish = () => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      child.kill("SIGKILL");            // no path leaves the child alive
      resolve({ out, bytes: Buffer.byteLength(out) });
    };
    const timer = setTimeout(finish, WAIT_MS);
    child.stdout.on("data", (d) => { out += d.toString(); });
    child.stderr.on("data", () => {});   // the wrapper logs to stderr by design
    child.on("error", finish);           // spawn failure must not hang the run
    child.stdin.on("error", () => {});   // EPIPE if the child died early
    child.stdin.write(payload);
  });
}

const results = (out) =>
  out.trim().split("\n").filter(Boolean).map((l) => { try { return JSON.parse(l); } catch { return null; } });

async function runSpec() {
  const { out, bytes } = await probe(initRequest(1, NEWEST) + "\n");
  console.log(`[spec] BYTES_RECEIVED=${bytes}`);
  if (bytes === 0) { console.log("[spec] FAIL: no response to a spec-compliant client."); return false; }
  if (/^Content-Length:/i.test(out.trim())) {
    console.log("[spec] FAIL: the RESPONSE is LSP-framed — a compliant client cannot parse it."); return false;
  }
  const v = results(out)[0]?.result?.protocolVersion;
  if (v !== NEWEST) { console.log(`[spec] FAIL: expected protocolVersion ${NEWEST}, got ${v}`); return false; }
  console.log(`[spec] PASS: NDJSON response, protocolVersion=${v}`);
  return true;
}

async function runLsp() {
  const body = initRequest(1, NEWEST);
  const { out, bytes } = await probe(`Content-Length: ${Buffer.byteLength(body, "utf8")}\r\n\r\n${body}`);
  console.log(`[lsp] BYTES_RECEIVED=${bytes}`);
  const sawResult = results(out).some((m) => m?.result?.protocolVersion);
  if (sawResult) { console.log("[lsp] FAIL: LSP framing still produced a valid initialize result."); return false; }
  console.log("[lsp] PASS: no initialize result" + (bytes > 0 ? " (a -32700 parse error was returned, which is correct)" : ""));
  return true;
}

// Every branch of negotiateProtocolVersion, not just the newest. A server that hardcodes
// 2025-06-18 passes `spec` but fails here — which is the point.
async function runNegotiate() {
  const cases = [
    ...SUPPORTED.map((v) => ({ requested: v, expect: v, why: "supported -> echoed" })),
    { requested: "1999-01-01", expect: NEWEST, why: "unsupported -> newest" },
    { requested: 20250618,     expect: NEWEST, why: "non-string -> newest" },
  ];
  let ok = true;
  for (const [i, c] of cases.entries()) {
    const { out } = await probe(initRequest(i + 1, c.requested) + "\n");
    const got = results(out)[0]?.result?.protocolVersion;
    const pass = got === c.expect;
    if (!pass) ok = false;
    console.log(`[negotiate] ${pass ? "PASS" : "FAIL"} requested=${JSON.stringify(c.requested)} expected=${c.expect} got=${got}  (${c.why})`);
  }
  return ok;
}

const run = { spec: runSpec, lsp: runLsp, negotiate: runNegotiate };
const toRun = mode === "all" ? ["spec", "lsp", "negotiate"] : [mode];
let allOk = true;
for (const m of toRun) allOk = (await run[m]()) && allOk;
console.log(allOk ? "OK" : "FAILED");
process.exit(allOk ? 0 : 1);
