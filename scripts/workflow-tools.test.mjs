// Exercise the actual stdio wrapper and its HTTP forwarding boundary. The local
// responder isolates dispatch/serialization; live app/SQLite parity is checked separately.
import { test } from "node:test";
import assert from "node:assert/strict";
import { createServer } from "node:http";
import { spawn } from "node:child_process";
import { createInterface } from "node:readline";
import { once } from "node:events";
import { fileURLToPath } from "node:url";

const wrapper = fileURLToPath(new URL("./mcp-server.mjs", import.meta.url));
const requestId = "9b54859d-39d6-4730-9e9b-e05502c3f8c6";

async function harness(t, respond) {
  const received = [];
  const server = createServer(async (request, response) => {
    try {
      const chunks = [];
      for await (const chunk of request) chunks.push(chunk);
      const rpc = JSON.parse(Buffer.concat(chunks).toString("utf8"));
      received.push({ method: request.method, url: request.url, headers: request.headers, rpc });
      const reply = respond(rpc);
      response.writeHead(200, { "Content-Type": reply.sse ? "text/event-stream" : "application/json" });
      if (Object.hasOwn(reply, "raw")) {
        response.end(reply.raw);
        return;
      }
      const json = JSON.stringify({ jsonrpc: "2.0", id: rpc.id, ...reply.message });
      response.end(reply.sse ? `event: message\ndata: ${json}\n\n` : json);
    } catch (error) {
      response.writeHead(500, { "Content-Type": "text/plain" });
      response.end(error.message);
    }
  });
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  const child = spawn(process.execPath, [wrapper], {
    stdio: ["pipe", "pipe", "pipe"], windowsHide: true,
    env: { ...process.env, KANBANFLOW_API_URL: `http://127.0.0.1:${server.address().port}`, KANBANFLOW_USAGE_LOG: "off", KANBANFLOW_MCP_TIMEOUT_MS: "3000" }
  });
  let stderr = "";
  child.stderr.on("data", chunk => { stderr += chunk; });
  const pending = new Map();
  const lines = createInterface({ input: child.stdout });
  function failPending(error) {
    for (const { reject, timer } of pending.values()) { clearTimeout(timer); reject(error); }
    pending.clear();
  }
  lines.on("line", line => {
    let message;
    try { message = JSON.parse(line); }
    catch { failPending(new Error(`Wrapper emitted non-JSON stdout: ${line}`)); return; }
    const waiter = pending.get(message.id);
    if (waiter) { clearTimeout(waiter.timer); pending.delete(message.id); waiter.resolve(message); }
  });
  child.on("error", failPending);
  child.on("exit", code => failPending(new Error(`Wrapper exited ${code}: ${stderr}`)));
  child.stdin.on("error", failPending);
  t.after(async () => {
    if (child.exitCode === null && child.signalCode === null) {
      const ended = once(child, "exit");
      child.kill();
      await ended;
    }
    lines.close();
    server.closeAllConnections();
    await new Promise((resolve, reject) => server.close(error => error ? reject(error) : resolve()));
  });
  let nextId = 1;
  function send(method, params = {}) {
    const id = nextId++;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => { pending.delete(id); reject(new Error(`No wrapper response for ${method}: ${stderr}`)); }, 5000);
      pending.set(id, { resolve, reject, timer });
      child.stdin.write(JSON.stringify({ jsonrpc: "2.0", id, method, params }) + "\n");
    });
  }
  const initialized = await send("initialize", { protocolVersion: "2025-06-18", capabilities: {}, clientInfo: { name: "workflow-regression", version: "1" } });
  assert.equal(initialized.result.protocolVersion, "2025-06-18");
  return { send, received };
}

test("wrapper advertises usable workflow input schemas once alongside existing board tools", async t => {
  const h = await harness(t, () => { throw new Error("Tool discovery must remain local"); });
  const reply = await h.send("tools/list");
  const tools = reply.result.tools;
  const names = tools.map(tool => tool.name);
  assert.equal(new Set(names).size, names.length);
  for (const name of ["list_requests", "get_request_context", "get_task_workflow", "workflow_command", "get_step_context", "update_task_status"])
    assert.equal(names.filter(candidate => candidate === name).length, 1, name);
  const schema = tools.find(tool => tool.name === "workflow_command").inputSchema;
  assert.deepEqual(schema.required, ["input"]);
  const input = schema.properties.input.properties;
  assert.equal(input.requestId.format, "uuid");
  assert.ok(input.expectedVersion.type.includes("integer"));
  assert.equal(input.actor.type, "string");
  assert.equal(input.key.type, "string");
  assert.equal(input.capture.properties.original.type, "string");
  assert.ok(input.taskBaselines.type.includes("object"));
  assert.equal(input.taskBaselines.additionalProperties.type, "string");
  assert.equal(input.tasks.items.properties.priority.type, "string");
  assert.ok(input.tasks.items.properties.priority.enum.includes("High"));
  assert.equal(input.checkedCriterionIds.items.type, "integer");
  assert.equal(tools.find(tool => tool.name === "get_task_workflow").inputSchema.properties.taskId.type, "integer");
  assert.equal(h.received.length, 0);
});

for (const [name, args, expected] of [
  ["list_requests", { projectId: 27 }, [{ id: requestId, ideaId: 12, projectId: 27, version: 4, archived: false }]],
  ["get_request_context", { requestId }, { id: requestId, ideaId: 12, projectId: 27, version: 4, messages: [{ kind: "original", text: " \r\n Original café 👋\t ", author: "human", source: "board" }], tasks: [{ taskId: 42, status: "Review", evidence: [{ current: false }] }] }],
  ["get_task_workflow", { taskId: 42 }, { id: requestId, ideaId: 12, projectId: 27, tasks: [{ taskId: 42, status: "Done", comments: [{ type: "Verification", text: "Actual checks" }] }], results: [{ current: true, conclusion: "Checked" }] }],
  ["workflow_command", { input: { operation: "capture", actor: "agent", key: "stable-operation-key", capture: { title: "Shared idea", original: " \r\n Exact café 👋\t ", projectId: null, source: "wrapper" } } }, { requestId, version: 1 }]
]) {
  test(`wrapper forwards ${name} to native MCP preserving arguments and structured result`, async t => {
    const h = await harness(t, () => ({ message: { result: { structuredContent: expected, content: [{ type: "text", text: "decoy text must not replace structured content" }] } } }));
    const response = await h.send("tools/call", { name, arguments: args });
    assert.equal(response.error, undefined);
    assert.deepEqual(JSON.parse(response.result.content[0].text), expected);
    assert.deepEqual(response.result.structuredContent, Array.isArray(expected) ? undefined : expected);
    assert.equal(h.received.length, 1);
    const forwarded = h.received[0];
    assert.equal(forwarded.method, "POST");
    assert.equal(forwarded.url, "/mcp");
    assert.equal(forwarded.headers["mcp-protocol-version"], "2025-06-18");
    assert.equal(forwarded.rpc.method, "tools/call");
    assert.deepEqual(forwarded.rpc.params, { name, arguments: args });
  });
}

for (const expected of [null, [1, { full: "text" }], "value", 42, false, { full: "object" }]) {
  test(`wrapper returns valid MCP content for ${JSON.stringify(expected)}`, async t => {
    const h = await harness(t, () => ({ message: { result: { content: [{ type: "text", text: JSON.stringify(expected) }] } } }));
    const response = await h.send("tools/call", { name: "get_request_context", arguments: { requestId } });
    assert.equal(response.error, undefined);
    assert.deepEqual(JSON.parse(response.result.content[0].text), expected);
    if (expected !== null && typeof expected === "object" && !Array.isArray(expected))
      assert.deepEqual(response.result.structuredContent, expected);
    else assert.equal(Object.hasOwn(response.result, "structuredContent"), false);
  });
}

test("wrapper preserves nested acceptance command and SSE text result without changing task IDs", async t => {
  const result = { requestId, version: 10 };
  const h = await harness(t, () => ({ sse: true, message: { result: { content: [{ type: "text", text: JSON.stringify(result) }] } } }));
  const args = { input: { operation: "accept_result", actor: "human-reviewer", key: "accept-key", requestId, expectedVersion: 9, itemId: "02b77978-f7d5-450c-bcf8-64868fc71f23", evidenceId: "5d81278f-85a1-4b0a-9e62-3acb23127977", checkedCriterionIds: [71, 73] } };
  const response = await h.send("tools/call", { name: "workflow_command", arguments: args });
  assert.deepEqual(JSON.parse(response.result.content[0].text), result);
  assert.deepEqual(h.received[0].rpc.params.arguments, args);
});

test("wrapper forwards the planner's original task tokens with a revised plan unchanged", async t => {
  const result = { requestId, version: 12 };
  const h = await harness(t, () => ({ message: { result: { structuredContent: result } } }));
  const args = { input: { operation: "save_plan", actor: "planner", key: "replan-from-read", requestId, expectedVersion: 11,
    taskBaselines: { "42": "token-read-before-edit", "47": "another-task-token" },
    tasks: [{ id: "02b77978-f7d5-450c-bcf8-64868fc71f23", title: "Reviewed task", description: "Reviewed scope", acceptance: "Current checks", repositoryId: null, fileReferences: "", priority: "High", assignee: "human", dependencies: [] }] } };
  const response = await h.send("tools/call", { name: "workflow_command", arguments: args });
  assert.deepEqual(JSON.parse(response.result.content[0].text), result);
  assert.deepEqual(h.received[0].rpc.params, { name: "workflow_command", arguments: args });
});

test("wrapper returns native stale-version refusal as an error and a refreshed retry can succeed", async t => {
  let calls = 0;
  const h = await harness(t, () => ({ message: { result: ++calls === 1
    ? { isError: true, content: [{ type: "text", text: "conflict: This request changed. Reload before saving." }] }
    : { structuredContent: { requestId, version: 11 } } } }));
  const input = { operation: "save_brief", actor: "agent", key: "save-brief", requestId, expectedVersion: 8, brief: { outcome: "Outcome", scope: "Scope", constraints: "Limits", acceptance: "Checks" } };
  const failed = await h.send("tools/call", { name: "workflow_command", arguments: { input } });
  assert.equal(failed.result, undefined);
  assert.equal(failed.error.code, -32000);
  assert.equal(failed.error.message, "conflict: This request changed. Reload before saving.");
  assert.equal(failed.error.data.tool, "workflow_command");
  const refreshed = { ...input, expectedVersion: 10 };
  const retry = await h.send("tools/call", { name: "workflow_command", arguments: { input: refreshed } });
  assert.deepEqual(JSON.parse(retry.result.content[0].text), { requestId, version: 11 });
  assert.deepEqual(h.received.map(call => call.rpc.params.arguments.input), [input, refreshed]);
});

function sseFrame(message) {
  return `event: message\r\ndata: ${JSON.stringify(message)}\r\n\r\n`;
}

test("wrapper selects the matching terminal SSE response after unrelated traffic", async t => {
  const original = "  # Complete source café 👋 Ω\r\n\r\n"
    + "  - Preserve this Markdown\r\n\r\n    exact indentation 👋 Ω  \r\n".repeat(4096)
    + "\r\n  final authored line  ";
  const input = { operation: "capture", actor: "reviewer Ω", key: "mixed-native-frames",
    capture: { title: "Complete source", original, projectId: null, source: "wrapper-regression" } };
  const expected = { requestId, version: 8, messages: [{ kind: "original", text: original }] };
  const h = await harness(t, rpc => {
    const terminal = { jsonrpc: "2.0", id: rpc.id,
      result: { structuredContent: expected, content: [{ type: "text", text: "decoy text" }] } };
    // Multi-line SSE data is legal. JSON string escapes keep the authored CRLF/space values exact.
    const terminalFrame = "event: message\r\n"
      + JSON.stringify(terminal, null, 2).split("\n").map(line => `data: ${line}`).join("\r\n") + "\r\n\r\n";
    return { sse: true, raw: ": heartbeat\r\n\r\ndata: not-json\r\n\r\n"
      + sseFrame({ jsonrpc: "2.0", method: "notifications/progress", params: { progressToken: "work", progress: 1 } })
      + sseFrame({ jsonrpc: "2.0", id: rpc.id + 100, result: { structuredContent: { wrong: "foreign response" } } })
      + sseFrame({ jsonrpc: "2.0", id: rpc.id, method: "roots/list", params: {} })
      + terminalFrame };
  });
  const reply = await h.send("tools/call", { name: "workflow_command", arguments: { input } });
  assert.equal(reply.error, undefined);
  assert.deepEqual(reply.result.structuredContent, expected);
  assert.deepEqual(JSON.parse(reply.result.content[0].text), expected);
  assert.equal(h.received.length, 1, "Traffic frames must not cause another HTTP request");
  assert.deepEqual(h.received[0].rpc.params, { name: "workflow_command", arguments: { input } });
  assert.equal(h.received[0].rpc.params.arguments.input.capture.original, original);
});

const invalidNativeResponses = [
  ["wrong numeric ID", id => ({ jsonrpc: "2.0", id: id + 100, result: {} })],
  ["numeric-equivalent string ID", id => ({ jsonrpc: "2.0", id: String(id), result: {} })],
  ["missing ID", () => ({ jsonrpc: "2.0", result: {} })],
  ["server request only", id => ({ jsonrpc: "2.0", id, method: "roots/list", params: {} })],
  ["server request with a result property", id => ({ jsonrpc: "2.0", id, method: "roots/list", params: {}, result: {} })],
  ["missing terminal result or error", id => ({ jsonrpc: "2.0", id })],
  ["wrong JSON-RPC version", id => ({ jsonrpc: "1.0", id, result: {} })],
  ["missing JSON-RPC version", id => ({ id, result: {} })],
  ["ambiguous result and error", id => ({ jsonrpc: "2.0", id, result: {},
    error: { code: -32602, message: "Both branches must not qualify" } })],
  ["null error", id => ({ jsonrpc: "2.0", id, error: null })],
  ["non-object error", id => ({ jsonrpc: "2.0", id, error: [] })],
  ["non-integer error code", id => ({ jsonrpc: "2.0", id, error: { code: "-32602", message: "Invalid code" } })],
  ["fractional error code", id => ({ jsonrpc: "2.0", id, error: { code: -32602.5, message: "Fractional code" } })],
  ["non-string error message", id => ({ jsonrpc: "2.0", id, error: { code: -32602, message: { text: "Invalid message" } } })],
  ["missing error code", id => ({ jsonrpc: "2.0", id, error: { message: "Missing code" } })],
  ["missing error message", id => ({ jsonrpc: "2.0", id, error: { code: -32602 } })]
];

for (const transport of ["JSON", "SSE"]) {
  for (const [label, envelope] of invalidNativeResponses) {
    test(`wrapper rejects native responses without a matching terminal ID (${transport}: ${label})`, async t => {
      const input = { operation: "capture", actor: "reviewer", key: `explicit-retry-${label}`,
        capture: { title: "Keep the request", original: "  café 👋\r\n\r\n    source stays exact  ", source: "regression" } };
      const expected = { requestId, version: 17 };
      let calls = 0;
      const h = await harness(t, rpc => {
        const message = ++calls === 1 ? envelope(rpc.id)
          : { jsonrpc: "2.0", id: rpc.id, result: { structuredContent: expected } };
        return { sse: transport === "SSE", raw: transport === "SSE" ? sseFrame(message) : JSON.stringify(message) };
      });
      const failed = await h.send("tools/call", { name: "workflow_command", arguments: { input } });
      assert.equal(failed.result, undefined, "Invalid native envelopes must never produce a successful receipt");
      assert.equal(failed.error.code, -32000);
      assert.equal(failed.error.message, "Native MCP returned no matching terminal response for workflow_command.");
      assert.equal(h.received.length, 1, "The wrapper must not retry the command implicitly");

      const recovered = await h.send("tools/call", { name: "workflow_command", arguments: { input } });
      assert.equal(recovered.error, undefined);
      assert.deepEqual(recovered.result.structuredContent, expected);
      assert.deepEqual(JSON.parse(recovered.result.content[0].text), expected);
      assert.equal(h.received.length, 2, "Only the explicit second client call may make a second HTTP request");
      assert.notEqual(h.received[0].rpc.id, h.received[1].rpc.id);
      assert.deepEqual(h.received.map(call => call.rpc.params), [
        { name: "workflow_command", arguments: { input } },
        { name: "workflow_command", arguments: { input } }
      ]);
    });
  }
}

const correlatedNativeErrors = [
  ["", { code: -32602, message: "  Rejected café 👋\r\n\r\n    keep the complete reason  ",
    data: { requestId, field: "input", expectedVersion: 7, details: ["Ω", { keep: false, empty: null }] } }],
  [" (zero code and empty message)", { code: 0, message: "", data: 0 }],
  [" (omitted optional data)", { code: 42, message: "Custom error" }]
];

for (const transport of ["JSON", "SSE"]) {
  for (const [boundary, nativeError] of correlatedNativeErrors) {
    test(`wrapper preserves correlated native errors${transport === "SSE" ? " after unrelated SSE traffic" : " in JSON"}${boundary}`, async t => {
      const input = { operation: "archive", actor: "reviewer", key: "correlated-error-key", requestId,
        expectedVersion: 8, archived: false };
      const h = await harness(t, rpc => {
        const terminal = { jsonrpc: "2.0", id: rpc.id, error: nativeError };
        return { sse: transport === "SSE", raw: transport === "SSE"
          ? sseFrame({ jsonrpc: "2.0", id: rpc.id + 1, error: { code: -32001, message: "Another call" } })
            + sseFrame({ jsonrpc: "2.0", id: rpc.id, method: "roots/list", params: {} }) + sseFrame(terminal)
          : JSON.stringify(terminal) };
      });
      const reply = await h.send("tools/call", { name: "workflow_command", arguments: { input } });
      assert.equal(reply.result, undefined);
      assert.deepEqual(reply.error, nativeError);
      assert.equal(h.received.length, 1);
      assert.deepEqual(h.received[0].rpc.params, { name: "workflow_command", arguments: { input } });
    });
  }
}
