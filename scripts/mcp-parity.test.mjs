import test from "node:test";
import assert from "node:assert/strict";
import { createServer } from "node:http";
import { spawn } from "node:child_process";
import { once } from "node:events";
import { fileURLToPath } from "node:url";

// Regression coverage for the wrapper/native parity review of master @ 1589e43 (findings
// F2-F4 = #537, F5-F7 = #538): actor-attribution parity (create_task, delete_task, the idea tools)
// and wrapper-vs-native contract drift (limit clamping, two stale tool/doc descriptions).
//
// Real stdio JSON-RPC and a real HTTP adapter; the fixture replaces only the REST server. This is
// a LOCAL COPY of the `withTransport` helper in atomic-projection-mcp.test.mjs (not exported
// there), extended to tolerate bodyless requests (GET/DELETE) — the original throws on those
// because `JSON.parse("")` is invalid for a request with no body.
async function withTransport(status, response, check) {
  const requests = [];
  const api = createServer(async (req, res) => {
    let raw = "";
    for await (const chunk of req) raw += chunk;
    requests.push({ method: req.method, url: req.url, body: raw.length ? JSON.parse(raw) : undefined });
    res.writeHead(status, { "Content-Type": "application/json" });
    res.end(response === undefined ? undefined : JSON.stringify(response));
  });
  api.listen(0, "127.0.0.1");
  await once(api, "listening");
  const child = spawn(process.execPath, [fileURLToPath(new URL("./mcp-server.mjs", import.meta.url))], {
    windowsHide: true,
    env: { ...process.env, KANBANFLOW_API_URL: `http://127.0.0.1:${api.address().port}`, KANBANFLOW_USAGE_LOG: "off" },
    stdio: ["pipe", "pipe", "pipe"],
  });
  const pending = new Map();
  let buffer = "";
  let nextId = 0;
  child.stdout.setEncoding("utf8");
  child.stdout.on("data", data => {
    buffer += data;
    let boundary;
    while ((boundary = buffer.indexOf("\n")) >= 0) {
      const message = JSON.parse(buffer.slice(0, boundary));
      buffer = buffer.slice(boundary + 1);
      const request = pending.get(message.id);
      if (request) { clearTimeout(request.timeout); pending.delete(message.id); request.resolve(message); }
    }
  });
  child.stderr.resume();
  child.on("exit", code => {
    for (const request of pending.values()) {
      clearTimeout(request.timeout);
      request.reject(new Error(`MCP fixture exited ${code}`));
    }
    pending.clear();
  });
  const rpc = (method, params) => new Promise((resolve, reject) => {
    const id = ++nextId;
    const timeout = setTimeout(() => { pending.delete(id); reject(new Error(`Timed out: ${method}`)); }, 10000);
    pending.set(id, { resolve, reject, timeout });
    child.stdin.write(JSON.stringify({ jsonrpc: "2.0", id, method, params }) + "\n");
  });
  try {
    const initialized = await rpc("initialize", { protocolVersion: "2024-11-05", capabilities: {}, clientInfo: { name: "mcp-parity-test", version: "1" } });
    assert.equal(initialized.error, undefined);
    child.stdin.write(JSON.stringify({ jsonrpc: "2.0", method: "notifications/initialized" }) + "\n");
    await check(rpc, requests);
  } finally {
    child.stdin.end();
    if (child.exitCode === null) child.kill();
    await new Promise(resolve => api.close(resolve));
  }
}

async function callTool(rpc, name, args) {
  const call = await rpc("tools/call", { name, arguments: args });
  assert.equal(call.error, undefined, `tools/call ${name} unexpectedly errored: ${JSON.stringify(call.error)}`);
  return call;
}

async function toolDescription(rpc, name) {
  const listing = await rpc("tools/list", {});
  const tool = listing.result.tools.find(t => t.name === name);
  assert.ok(tool, `tool ${name} not found in tools/list`);
  return tool;
}

function queryOf(url) {
  return new URL(url, "http://x").searchParams;
}

// ── F2: create_task actor parity ────────────────────────────────────────────

test("stdio create_task forwards an explicit actor (F2)", async () => {
  await withTransport(201, { id: 9, featureId: 3, name: "actor-passthrough-test" }, async (rpc, requests) => {
    await callTool(rpc, "create_task", { featureId: 3, name: "actor-passthrough-test", actor: "explicit-actor" });
    assert.equal(requests.length, 1);
    assert.equal(requests[0].method, "POST");
    assert.equal(requests[0].body.actor, "explicit-actor");
  });
});

test("stdio create_task defaults actor to mcp-agent when omitted (F2)", async () => {
  await withTransport(201, { id: 8 }, async (rpc, requests) => {
    await callTool(rpc, "create_task", { featureId: 3, name: "no-actor-test" });
    assert.equal(requests[0].body.actor, "mcp-agent");
  });
});

test("stdio create_task treats a whitespace-only actor as absent (F2)", async () => {
  await withTransport(201, { id: 10 }, async (rpc, requests) => {
    await callTool(rpc, "create_task", { featureId: 3, name: "whitespace-actor-test", actor: "   " });
    assert.equal(requests[0].body.actor, "mcp-agent");
  });
});

// ── F3: delete_task actor parity (wrapper half; the REST half is covered by
// tests/KanbanFlow.Tests/TaskDeleteActorEndpointTests.cs against the real route) ─────────────────

test("stdio delete_task forwards an explicit actor as a query parameter (F3)", async () => {
  await withTransport(204, undefined, async (rpc, requests) => {
    await callTool(rpc, "delete_task", { taskId: 8, actor: "explicit-actor" });
    assert.equal(requests.length, 1);
    assert.equal(requests[0].method, "DELETE");
    const url = new URL(requests[0].url, "http://x");
    assert.equal(url.pathname, "/api/tasks/8");
    assert.equal(url.searchParams.get("actor"), "explicit-actor");
  });
});

test("stdio delete_task defaults actor to mcp-agent when omitted (F3)", async () => {
  await withTransport(204, undefined, async (rpc, requests) => {
    await callTool(rpc, "delete_task", { taskId: 8 });
    assert.equal(queryOf(requests[0].url).get("actor"), "mcp-agent");
  });
});

// ── F4: idea-tool actor defaults ────────────────────────────────────────────

test("stdio create_idea defaults actor to mcp-agent when omitted (F4)", async () => {
  await withTransport(201, { id: 1 }, async (rpc, requests) => {
    await callTool(rpc, "create_idea", { title: "idea-no-actor" });
    assert.equal(requests[0].body.actor, "mcp-agent");
  });
});

test("stdio create_idea treats a whitespace-only actor as absent (F4)", async () => {
  await withTransport(201, { id: 1 }, async (rpc, requests) => {
    await callTool(rpc, "create_idea", { title: "idea-ws-actor", actor: "  " });
    assert.equal(requests[0].body.actor, "mcp-agent");
  });
});

test("stdio create_idea forwards an explicit actor unchanged (F4 control)", async () => {
  await withTransport(201, { id: 1 }, async (rpc, requests) => {
    await callTool(rpc, "create_idea", { title: "idea-explicit-actor", actor: "explicit-actor" });
    assert.equal(requests[0].body.actor, "explicit-actor");
  });
});

test("stdio update_idea defaults actor to mcp-agent when omitted (F4)", async () => {
  await withTransport(200, { id: 1 }, async (rpc, requests) => {
    // expectedWorkflowVersion is ignored by this tool on master; included so this test keeps
    // passing unmodified if a future merge (e.g. #511) makes it required.
    await callTool(rpc, "update_idea", { ideaId: 1, priority: "Low", expectedWorkflowVersion: 0 });
    assert.equal(requests[0].body.actor, "mcp-agent");
  });
});

// promote_idea was left "PLAUSIBLE — NOT SHOWN" by the review (same code shape, not independently
// driven live). This is the first live exercise of it.
test("stdio promote_idea defaults actor to mcp-agent when omitted (F4)", async () => {
  await withTransport(200, { ideaId: 1, projectId: 2 }, async (rpc, requests) => {
    await callTool(rpc, "promote_idea", { ideaId: 1 });
    assert.equal(requests[0].body.actor, "mcp-agent");
  });
});

// ── F5: get_next_tasks / list_ideas clamp `limit` instead of rejecting it ───────────────────────

test("stdio get_next_tasks clamps an out-of-range limit instead of rejecting it (F5)", async () => {
  await withTransport(200, { projectId: 1, recommendations: [] }, async (rpc, requests) => {
    await callTool(rpc, "get_next_tasks", { projectId: 1, limit: 999 });
    assert.equal(queryOf(requests[0].url).get("limit"), "50");
    await callTool(rpc, "get_next_tasks", { projectId: 1, limit: -5 });
    assert.equal(queryOf(requests[1].url).get("limit"), "1");
  });
});

// Out-of-range-only values (999, -5 above) can't distinguish a real clamp from a mutant that
// always returns `min` regardless of input — every one of those cases wants `min` anyway. These
// in-range and exact-boundary pairs close that gap (a mutant turning `return value` into
// `return min` in optionalClampedInt survived the tests above unnoticed).
test("stdio get_next_tasks passes an in-range limit through unchanged (F5 boundary)", async () => {
  await withTransport(200, { projectId: 1, recommendations: [] }, async (rpc, requests) => {
    await callTool(rpc, "get_next_tasks", { projectId: 1, limit: 7 });
    assert.equal(queryOf(requests[0].url).get("limit"), "7", "an in-range value must pass through unchanged, not collapse to min");
    await callTool(rpc, "get_next_tasks", { projectId: 1, limit: 50 });
    assert.equal(queryOf(requests[1].url).get("limit"), "50", "the max itself is in-range and must not be altered");
    await callTool(rpc, "get_next_tasks", { projectId: 1, limit: 0 });
    assert.equal(queryOf(requests[2].url).get("limit"), "1", "one below min must clamp to min exactly");
    await callTool(rpc, "get_next_tasks", { projectId: 1, limit: 51 });
    assert.equal(queryOf(requests[3].url).get("limit"), "50", "one above max must clamp to max exactly");
  });
});

test("stdio get_next_tasks still rejects a non-integer limit (F5 control)", async () => {
  await withTransport(200, {}, async (rpc, requests) => {
    const call = await rpc("tools/call", { name: "get_next_tasks", arguments: { projectId: 1, limit: "abc" } });
    assert.equal(call.error?.code, -32602);
    assert.equal(requests.length, 0);
  });
});

// A safe-integer-but-not-Int32 limit must be REJECTED, not silently clamped: native's `int`
// parameter fails to bind it and REST's `int?` query binding answers 400 — clamping it here would
// hide a malformed request behind three different outcomes for the same input.
test("stdio get_next_tasks rejects a limit outside the Int32 range instead of clamping it (F5)", async () => {
  await withTransport(200, {}, async (rpc, requests) => {
    const call = await rpc("tools/call", { name: "get_next_tasks", arguments: { projectId: 1, limit: 3000000000 } });
    assert.equal(call.error?.code, -32602, `expected a -32602 rejection, got: ${JSON.stringify(call)}`);
    assert.equal(requests.length, 0, "an out-of-Int32-range limit must never reach REST, clamped or otherwise");
  });
});

// Both halves of the Int32 guard, at their exact edges. The case above only exercises the upper
// half, so a guard reduced to `value > INT32_MAX` (or an off-by-one `>=`) still passed every
// assertion in this file. One past each bound is rejected; each bound itself is
// accepted and then clamped like any other out-of-range limit.
test("stdio get_next_tasks accepts both Int32 extremes and rejects one past each (F5 boundary)", async () => {
  await withTransport(200, { projectId: 1, recommendations: [] }, async (rpc, requests) => {
    for (const limit of [2147483648, -2147483649]) {
      const call = await rpc("tools/call", { name: "get_next_tasks", arguments: { projectId: 1, limit } });
      assert.equal(call.error?.code, -32602, `limit ${limit}: expected a -32602 rejection, got: ${JSON.stringify(call)}`);
    }
    assert.equal(requests.length, 0, "a limit one past either Int32 bound must never reach REST");
    await callTool(rpc, "get_next_tasks", { projectId: 1, limit: 2147483647 });
    assert.equal(queryOf(requests[0].url).get("limit"), "50", "INT32_MAX itself is accepted, then clamped to max");
    await callTool(rpc, "get_next_tasks", { projectId: 1, limit: -2147483648 });
    assert.equal(queryOf(requests[1].url).get("limit"), "1", "INT32_MIN itself is accepted, then clamped to min");
  });
});

test("stdio list_ideas clamps an out-of-range limit instead of rejecting it (F5)", async () => {
  await withTransport(200, [], async (rpc, requests) => {
    await callTool(rpc, "list_ideas", { limit: 99999 });
    assert.equal(queryOf(requests[0].url).get("limit"), "500");
    await callTool(rpc, "list_ideas", { limit: -1 });
    assert.equal(queryOf(requests[1].url).get("limit"), "1");
  });
});

test("stdio list_ideas passes an in-range limit through unchanged (F5 boundary)", async () => {
  await withTransport(200, [], async (rpc, requests) => {
    await callTool(rpc, "list_ideas", { limit: 7 });
    assert.equal(queryOf(requests[0].url).get("limit"), "7", "an in-range value must pass through unchanged, not collapse to min");
    await callTool(rpc, "list_ideas", { limit: 500 });
    assert.equal(queryOf(requests[1].url).get("limit"), "500", "the max itself is in-range and must not be altered");
    await callTool(rpc, "list_ideas", { limit: 0 });
    assert.equal(queryOf(requests[2].url).get("limit"), "1", "one below min must clamp to min exactly");
    await callTool(rpc, "list_ideas", { limit: 501 });
    assert.equal(queryOf(requests[3].url).get("limit"), "500", "one above max must clamp to max exactly");
  });
});

// ── F6: reassign_task description no longer claims the (fixed) #83 gap ─────────────────────────

test("stdio reassign_task description no longer claims cross-project moves are permitted (F6)", async () => {
  await withTransport(200, {}, async (rpc) => {
    const tool = await toolDescription(rpc, "reassign_task");
    assert.ok(
      !tool.description.includes("the API today permits cross-project moves"),
      `reassign_task description still claims the fixed #83 gap: ${tool.description}`);
  });
});

// ── F7: read_graph description states the real 900-entity cap ──────────────────────────────────

test("stdio read_graph description states the real 900 cap, not 1000 (F7)", async () => {
  await withTransport(200, {}, async (rpc) => {
    const tool = await toolDescription(rpc, "read_graph");
    assert.ok(tool.description.includes("900"), `read_graph description missing the real cap: ${tool.description}`);
    assert.ok(!tool.description.includes("1000"), `read_graph description still claims the wrong cap: ${tool.description}`);
  });
});
