import test from "node:test";
import assert from "node:assert/strict";
import { createServer } from "node:http";
import { spawn } from "node:child_process";
import { once } from "node:events";
import { fileURLToPath } from "node:url";

// Real stdio JSON-RPC and real HTTP adapter; the fixture replaces only the REST server.
async function withTransport(status, response, check) {
  const requests = [];
  const api = createServer(async (req, res) => {
    let raw = "";
    for await (const chunk of req) raw += chunk;
    requests.push({ method: req.method, url: req.url, body: raw ? JSON.parse(raw) : null, headers: req.headers });
    res.writeHead(status, { "Content-Type": "application/json" });
    res.end(JSON.stringify(response));
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
    const initialized = await rpc("initialize", { protocolVersion: "2024-11-05", capabilities: {}, clientInfo: { name: "atomic-projection-test", version: "1" } });
    assert.equal(initialized.error, undefined);
    child.stdin.write(JSON.stringify({ jsonrpc: "2.0", method: "notifications/initialized" }) + "\n");
    await check(rpc, requests);
  } finally {
    child.stdin.end();
    if (child.exitCode === null) child.kill();
    await new Promise(resolve => api.close(resolve));
  }
}

const description = "\r\n    Complete Ω source\r\n" + "long evidence  \r\n".repeat(450) + "TAIL  \r\n\r\n";
const args = {
  featureId: 10, name: "Imported issue", repositoryExternalId: "R_board",
  repositoryOwner: "octo", repositoryName: "board", externalNodeId: "I_issue",
  externalNumber: 42, url: "https://github.com/octo/board/issues/42", description,
};

for (const [actor, header] of [['Agent \u03a9', 'mcp-agent'], ['agent\r\nlabel', 'mcp-agent'], ['   ', 'mcp-agent'], ['  operator  ', 'operator']]) {
  test(`stdio REST actor header is safe while JSON retains ${JSON.stringify(actor)}`, async () => {
    await withTransport(200, { id: 7 }, async (rpc, requests) => {
      const result = await rpc('tools/call', { name: 'project_github_issue', arguments: { ...args, actor } });
      assert.equal(result.error, undefined);
      assert.equal(requests.length, 1);
      assert.equal(requests[0].headers['x-kanbanflow-actor'], header);
      // Header normalization must not rewrite caller-authored JSON.
      assert.equal(requests[0].body.actor, actor);
    });
  });
}

test('stdio idea edits require and forward the version from the original read', async () => {
  await withTransport(200, { id: 7, workflowVersion: 6 }, async (rpc, requests) => {
    const missing = await rpc('tools/call', { name: 'update_idea', arguments: { ideaId: 7, body: 'stale draft' } });
    assert.ok(missing.error);
    assert.equal(requests.length, 0);
    const result = await rpc('tools/call', { name: 'update_idea', arguments: { ideaId: 7, body: 'new draft', expectedWorkflowVersion: 5 } });
    assert.equal(result.error, undefined);
    assert.equal(requests.length, 1);
    assert.equal(requests[0].method, 'PATCH');
    assert.equal(requests[0].body.expectedWorkflowVersion, 5);
    assert.equal(requests[0].body.body, 'new draft');
    assert.equal(JSON.parse(result.result.content[0].text).workflowVersion, 6);
  });
});

test("stdio project_github_issue exposes one compound call and preserves complete content", async () => {
  const projected = { task: { id: 7, featureId: 10, name: args.name, description }, link: { id: 8, taskId: 7, externalNodeId: "I_issue" }, created: true };
  await withTransport(201, projected, async (rpc, requests) => {
    const listing = await rpc("tools/list", {});
    const tool = listing.result.tools.find(t => t.name === "project_github_issue");
    assert.ok(tool);
    assert.ok(tool.inputSchema.required.includes("externalNodeId"));
    const call = await rpc("tools/call", { name: "project_github_issue", arguments: args });
    assert.equal(call.error, undefined);
    assert.deepEqual(JSON.parse(call.result.content[0].text), projected);
    assert.equal(requests.length, 1);
    assert.equal(requests[0].method, "POST");
    assert.equal(requests[0].url, "/api/features/10/external-work-item-projections/github");
    const { featureId, ...payload } = args;
    assert.equal(featureId, 10);
    assert.deepEqual(requests[0].body, { ...payload, actor: "mcp-agent" });
  });
});

test("stdio atomic identity refusal returns the complete conflict and makes no follow-up call", async () => {
  const conflict = { conflictCode: "external-node-cross-project", message: description };
  await withTransport(409, conflict, async (rpc, requests) => {
    const call = await rpc("tools/call", { name: "project_github_issue", arguments: args });
    assert.equal(call.error.code, -32000);
    assert.deepEqual(call.error.data.payload, conflict);
    assert.ok(call.error.message.includes(JSON.stringify(conflict)));
    assert.equal(requests.length, 1);
  });
});

test("stdio atomic projection preserves an explicitly empty description", async () => {
  await withTransport(201, { task: { id: 7, description: "" }, created: true }, async (rpc, requests) => {
    const call = await rpc("tools/call", { name: "project_github_issue", arguments: { ...args, description: "" } });
    assert.equal(call.error, undefined);
    assert.equal(requests.length, 1);
    assert.equal(requests[0].body.description, "");
    assert.equal(JSON.parse(call.result.content[0].text).task.description, "");
  });
});
