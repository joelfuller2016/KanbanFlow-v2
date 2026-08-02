#!/usr/bin/env node
// framing-ab.mjs — proves scripts/mcp-server.mjs speaks the MCP stdio wire format.
//
// MCP spec 2025-06-18, Transports/stdio:
//   "Messages are delimited by newlines, and MUST NOT contain embedded newlines."
// There is no Content-Length framing in MCP stdio — that is LSP.
//
// Usage:  node scripts/framing-ab.mjs spec  -> must receive a valid initialize RESULT (exit 0)
//         node scripts/framing-ab.mjs lsp   -> must NOT receive a result; a JSON-RPC
//                                              parse error is correct and expected (exit 0)
//
// NOTE on the `lsp` assertion: it deliberately does NOT assert "zero bytes". A server that
// rejects a malformed line with -32700 is behaving correctly — asserting byte-count would
// test the wrong property and fail a correct implementation.
//
// Both modes exit non-zero when the observed behaviour is wrong, so this is usable
// as a gate. `initialize` is answered locally by the wrapper, so no running app is needed.
import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";
import path from "node:path";

const mode = process.argv[2];
if (mode !== "spec" && mode !== "lsp") {
  console.error("usage: node scripts/framing-ab.mjs <spec|lsp>");
  process.exit(2);
}

const here = path.dirname(fileURLToPath(import.meta.url));
const wrapper = path.join(here, "mcp-server.mjs");
const WAIT_MS = Number(process.env.FRAMING_AB_WAIT_MS ?? 4000);

const child = spawn(process.execPath, [wrapper], {
  stdio: ["pipe", "pipe", "pipe"],
  env: {
    ...process.env,
    KANBANFLOW_API_URL: process.env.KANBANFLOW_API_URL ?? "http://127.0.0.1:5148",
    KANBANFLOW_MCP_TIMEOUT_MS: "5000",
  },
});

let out = "";
child.stdout.on("data", (d) => { out += d.toString(); });
child.stderr.on("data", () => {}); // the wrapper logs to stderr by design

const init = JSON.stringify({
  jsonrpc: "2.0",
  id: 1,
  method: "initialize",
  params: {
    protocolVersion: "2025-06-18",
    capabilities: {},
    clientInfo: { name: "framing-ab", version: "1.0" },
  },
});

if (mode === "spec") {
  child.stdin.write(init + "\n");                                        // what a real MCP client sends
} else {
  child.stdin.write(`Content-Length: ${Buffer.byteLength(init, "utf8")}\r\n\r\n${init}`); // LSP framing
}

setTimeout(() => {
  const bytes = Buffer.byteLength(out);
  const got = out.trim();
  console.log(`MODE=${mode}  BYTES_RECEIVED=${bytes}`);

  let ok;
  if (mode === "spec") {
    ok = bytes > 0;
    if (!ok) {
      console.log("FAIL: a spec-compliant client got NO RESPONSE — the wrapper is unreachable.");
    } else {
      console.log("PASS: responded to a spec-compliant client.");
      console.log("  " + got.split("\n")[0].slice(0, 160));
      // The reply itself must also be newline-delimited, not LSP-framed.
      if (/^Content-Length:/i.test(got)) {
        console.log("FAIL: the RESPONSE is LSP-framed — a compliant client cannot parse it.");
        ok = false;
      }
      try {
        const v = JSON.parse(got.split("\n")[0])?.result?.protocolVersion;
        console.log(`  negotiated protocolVersion=${v}`);
        if (v !== "2025-06-18") {
          console.log("FAIL: did not echo the requested protocolVersion 2025-06-18.");
          ok = false;
        }
      } catch { console.log("FAIL: response line is not valid JSON."); ok = false; }
    }
  } else {
    // The property that matters: LSP framing must NOT produce a successful initialize
    // result. Rejecting the malformed header line with -32700 is correct behaviour.
    let sawResult = false;
    for (const line of got.split("\n").filter(Boolean)) {
      try { if (JSON.parse(line)?.result?.protocolVersion) sawResult = true; } catch { /* not JSON: fine */ }
    }
    ok = !sawResult;
    if (ok) {
      console.log("PASS: LSP framing yields no initialize result (old wire format is gone).");
      if (bytes > 0) console.log("  (a JSON-RPC parse error was returned, which is correct)");
    } else {
      console.log("FAIL: LSP-framed input still produced a valid initialize result.");
    }
  }

  child.kill("SIGKILL");
  process.exit(ok ? 0 : 1);
}, WAIT_MS);
