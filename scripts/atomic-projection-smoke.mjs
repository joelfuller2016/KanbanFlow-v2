// Usage: KANBANFLOW_API_URL=http://127.0.0.1:5208 node scripts/atomic-projection-smoke.mjs
// Creates and deletes only its labeled projects. Run against a disposable local application.
import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";

const base = process.env.KANBANFLOW_API_URL;
assert.ok(base, "Set KANBANFLOW_API_URL to the disposable app explicitly.");
const api = async (method, path, body, expected = 200) => {
  const response = await fetch(base + path, {
    method, headers: { "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body), signal: AbortSignal.timeout(15000),
  });
  const text = await response.text();
  assert.equal(response.status, expected, `${method} ${path}: ${text}`);
  return text ? JSON.parse(text) : null;
};
const native = async args => {
  const response = await fetch(base + "/mcp", {
    method: "POST", headers: { "Content-Type": "application/json", Accept: "application/json, text/event-stream", "MCP-Protocol-Version": "2025-06-18" },
    body: JSON.stringify({ jsonrpc: "2.0", id: 1, method: "tools/call", params: { name: "project_github_issue", arguments: args } }),
    signal: AbortSignal.timeout(15000),
  });
  assert.equal(response.status, 200);
  const text = await response.text();
  const data = text.split(/\r?\n/).find(line => line.startsWith("data: "));
  const packet = JSON.parse(data ? data.slice(6) : text);
  assert.equal(packet.error, undefined);
  assert.notEqual(packet.result.isError, true, JSON.stringify(packet.result));
  return packet.result.structuredContent ?? JSON.parse(packet.result.content.find(c => c.type === "text").text);
};
const child = spawn(process.execPath, [fileURLToPath(new URL("./mcp-server.mjs", import.meta.url))], {
  windowsHide: true, stdio: ["pipe", "pipe", "pipe"],
  env: { ...process.env, KANBANFLOW_USAGE_LOG: "off" },
});
let pending;
let buffer = "";
let nextId = 0;
child.stdout.setEncoding("utf8");
child.stdout.on("data", data => {
  buffer += data;
  let boundary;
  while ((boundary = buffer.indexOf("\n")) >= 0) {
    const packet = JSON.parse(buffer.slice(0, boundary));
    buffer = buffer.slice(boundary + 1);
    if (pending && packet.id === pending.id) {
      clearTimeout(pending.timeout); pending.resolve(packet); pending = null;
    }
  }
});
child.stderr.resume();
child.on("exit", code => {
  if (pending) { clearTimeout(pending.timeout); pending.reject(new Error(`MCP exited ${code}`)); pending = null; }
});
const rpc = (method, params) => new Promise((resolve, reject) => {
  const id = ++nextId;
  pending = { id, resolve, reject, timeout: setTimeout(() => { pending = null; reject(new Error(`MCP timed out: ${method}`)); }, 15000) };
  child.stdin.write(JSON.stringify({ jsonrpc: "2.0", id, method, params }) + "\n");
});
const stdio = async args => {
  const packet = await rpc("tools/call", { name: "project_github_issue", arguments: args });
  assert.equal(packet.error, undefined, JSON.stringify(packet.error));
  return JSON.parse(packet.result.content[0].text);
};

const projects = [];
try {
  const initialized = await rpc("initialize", { protocolVersion: "2024-11-05", capabilities: {}, clientInfo: { name: "projection-smoke", version: "1" } });
  assert.equal(initialized.error, undefined);
  child.stdin.write(JSON.stringify({ jsonrpc: "2.0", method: "notifications/initialized" }) + "\n");
  for (const suffix of ["source", "conflict"]) projects.push(await api("POST", "/api/projects", { name: `Issue507 smoke ${Date.now()} ${suffix}` }, 201));
  const features = [];
  for (const project of projects) features.push(await api("POST", `/api/projects/${project.id}/features`, { name: "Atomic projection smoke" }, 201));
  const description = "\r\n    Source Ω\r\n" + "long Markdown evidence  \r\n".repeat(450) + "TAIL  \r\n\r\n";
  const args = { featureId: features[0].id, name: "Atomic fixture", description,
    repositoryExternalId: `R_smoke507_${projects[0].id}`, repositoryOwner: "octo", repositoryName: "board",
    externalNodeId: `I_smoke507_${projects[0].id}_42`, externalNumber: 42, url: "https://github.com/octo/board/issues/42", actor: "projection-smoke" };
  const path = `/api/features/${features[0].id}/external-work-item-projections/github`;
  const first = await api("POST", path, args, 201);
  const repeat = await api("POST", path, args);
  assert.equal(first.created, true);
  assert.equal(repeat.created, false);
  assert.equal(first.task.id, repeat.task.id);
  assert.equal(first.link.id, repeat.link.id);
  const nativeReplay = await native(args);
  assert.equal(nativeReplay.created, false);
  assert.equal(nativeReplay.task.id, first.task.id);
  assert.equal(nativeReplay.task.description, description);
  const nativeArgs = { ...args, externalNodeId: args.externalNodeId + "_43", externalNumber: 43, url: "https://github.com/octo/board/issues/43" };
  const nativeCreate = await native(nativeArgs);
  assert.equal(nativeCreate.created, true);
  const stdioReplay = await stdio(nativeArgs);
  assert.equal(stdioReplay.created, false);
  assert.equal(stdioReplay.task.id, nativeCreate.task.id);
  const stdioCreate = await stdio({ ...args, externalNodeId: args.externalNodeId + "_44", externalNumber: 44, url: "https://github.com/octo/board/issues/44" });
  assert.equal(stdioCreate.created, true);
  for (const result of [first, nativeCreate, stdioCreate]) {
    const task = await api("GET", `/api/tasks/${result.task.id}`);
    assert.equal(task.description, description);
    assert.equal(task.externalLinks.length, 1);
    const events = await api("GET", `/api/tasks/${result.task.id}/activity`);
    assert.equal(events.length, 2);
    assert.deepEqual(events.map(e => e.eventType).sort(), ["ExternalWorkItemLinked", "TaskCreated"]);
    assert.equal(JSON.parse(events.find(e => e.eventType === "TaskCreated").afterJson).description, description);
  }
  const cross = await api("POST", `/api/features/${features[1].id}/external-work-item-projections/github`, args, 409);
  assert.equal(cross.conflictCode, "external-node-cross-project");
  assert.equal(cross.taskId, first.task.id);
  const numberConflict = await api("POST", path, { ...args, externalNodeId: args.externalNodeId + "_conflict" }, 409);
  assert.equal(numberConflict.conflictCode, "external-number-already-linked");
  assert.equal(numberConflict.taskId, first.task.id);
  await api("POST", path, { ...args, repositoryExternalId: "R_wrong" }, 422);
  await api("POST", path, { ...args, url: "https://github.com/foreign/board/issues/42" }, 422);
  await api("POST", path, null, 400);
  assert.equal((await api("GET", `/api/projects/${projects[0].id}/tasks`)).length, 3);
  assert.equal((await api("GET", `/api/projects/${projects[1].id}/tasks`)).length, 0);
  console.log(JSON.stringify({ passed: true, restCreateReplay: true, nativeCreateReplay: true, stdioCreateReplay: true,
    completeDescriptionLength: description.length, oneAuditPairEach: true, crossProject409: true, identity422: true, null400: true }));
} finally {
  child.stdin.end();
  if (child.exitCode === null) child.kill();
  for (const project of projects.reverse()) await api("DELETE", `/api/projects/${project.id}`, undefined, 204);
}
