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
const nativePacket = async args => {
  const response = await fetch(base + "/mcp", {
    method: "POST", headers: { "Content-Type": "application/json", Accept: "application/json, text/event-stream", "MCP-Protocol-Version": "2025-06-18" },
    body: JSON.stringify({ jsonrpc: "2.0", id: 1, method: "tools/call", params: { name: "project_github_issue", arguments: args } }),
    signal: AbortSignal.timeout(15000),
  });
  assert.equal(response.status, 200);
  const text = await response.text();
  const data = text.split(/\r?\n/).find(line => line.startsWith("data: "));
  const packet = JSON.parse(data ? data.slice(6) : text);
  return packet;
};
const native = async args => {
  const packet = await nativePacket(args);
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
const projectSnapshot = async project => {
  const tasks = await api("GET", `/api/projects/${project.id}/tasks`);
  const activity = await api("GET", `/api/projects/${project.id}/activity?limit=500`);
  assert.ok(activity.length < 500, "The owned fixture's complete audit trail must fit the explicit page.");
  return {
    tasks: await Promise.all(tasks.sort((a, b) => a.id - b.id).map(task => api("GET", `/api/tasks/${task.id}`))),
    comments: await Promise.all(tasks.map(task => api("GET", `/api/tasks/${task.id}/comments`))),
    links: await api("GET", `/api/projects/${project.id}/external-work-item-links`),
    activity,
  };
};
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

  // A sync may already have read its identity cache when a human saves this decision.
  // Every entry point must re-read the quarantine in the transaction before creating a card.
  const staleLinks = await api("GET", `/api/projects/${projects[0].id}/external-work-item-links`);
  assert.equal(staleLinks.length, 3);
  const candidate = await api("POST", `/api/features/${features[0].id}/tasks`, {
    name: "Unnumbered candidate awaiting human review", description: "Keep this human decision  \r\nΩ", actor: "human-review",
  }, 201);
  const quarantine = await api("POST", `/api/tasks/${candidate.id}/external-work-item-quarantines/github`, {
    repositoryExternalId: args.repositoryExternalId, repositoryOwner: args.repositoryOwner, repositoryName: args.repositoryName,
    candidateNumber: null, candidateUrl: null, reason: "Resolve ambiguous provenance before another import  \r\nΩ", actor: "human-review",
  });
  assert.equal(quarantine.externalNumber, null);
  assert.equal(quarantine.externalNodeId, null);
  const blockedArgs = { ...args, externalNodeId: args.externalNodeId + "_blocked", externalNumber: 45, url: "https://github.com/octo/board/issues/45" };
  const expectedQuarantineMessage = `Task ${candidate.id} has an unresolved quarantine without an issue number in this project and repository; resolve it before creating another projection.`;
  const beforeQuarantineCalls = await Promise.all(projects.map(projectSnapshot));
  const assertUnchanged = async () => assert.deepEqual(await Promise.all(projects.map(projectSnapshot)), beforeQuarantineCalls,
    "Refusal/replay must preserve every task field, link, comment and audit event in both projects.");

  const restQuarantine = await api("POST", path, blockedArgs, 409);
  assert.equal(restQuarantine.conflictCode, "quarantined-link-exists");
  assert.equal(restQuarantine.taskId, candidate.id);
  assert.equal(restQuarantine.message, expectedQuarantineMessage);
  await assertUnchanged();

  const nativeQuarantine = await nativePacket(blockedArgs);
  assert.equal(nativeQuarantine.error, undefined);
  assert.equal(nativeQuarantine.result.isError, true);
  assert.ok(nativeQuarantine.result.content.filter(c => c.type === "text").map(c => c.text).join("\n")
    .includes(`quarantined-link-exists: ${expectedQuarantineMessage}`), "Native MCP must expose the complete human-review refusal.");
  await assertUnchanged();

  const stdioQuarantine = await rpc("tools/call", { name: "project_github_issue", arguments: blockedArgs });
  assert.equal(stdioQuarantine.error.code, -32000);
  assert.equal(stdioQuarantine.error.data.status, 409);
  assert.equal(stdioQuarantine.error.data.payload.conflictCode, "quarantined-link-exists");
  assert.equal(stdioQuarantine.error.data.payload.taskId, candidate.id);
  assert.equal(stdioQuarantine.error.data.payload.message, expectedQuarantineMessage);
  await assertUnchanged();

  // An unrelated human-review item cannot turn an exact active replay into a write or refusal.
  for (const replay of [await api("POST", path, args), await native(args), await stdio(args)]) {
    assert.equal(replay.created, false);
    assert.equal(replay.task.id, first.task.id);
    assert.equal(replay.link.id, first.link.id);
    assert.equal(replay.task.description, description);
  }
  await assertUnchanged();

  const unrelatedRepository = await api("POST", path, {
    ...blockedArgs, repositoryExternalId: args.repositoryExternalId + "_other", externalNodeId: blockedArgs.externalNodeId + "_other_repo",
  }, 201);
  assert.equal(unrelatedRepository.created, true);
  const unrelatedProject = await native({ ...blockedArgs, featureId: features[1].id });
  assert.equal(unrelatedProject.created, true);
  assert.equal(unrelatedProject.task.featureId, features[1].id);
  const stdioUnrelated = await stdio({ ...blockedArgs, featureId: features[1].id,
    externalNodeId: blockedArgs.externalNodeId + "_46", externalNumber: 46, url: "https://github.com/octo/board/issues/46" });
  assert.equal(stdioUnrelated.created, true);
  const preservedQuarantine = (await api("GET", `/api/projects/${projects[0].id}/external-work-item-links`)).find(link => link.id === quarantine.id);
  assert.deepEqual(preservedQuarantine, quarantine);
  console.log(JSON.stringify({ passed: true, restCreateReplay: true, nativeCreateReplay: true, stdioCreateReplay: true,
    completeDescriptionLength: description.length, oneAuditPairEach: true, crossProject409: true, identity422: true, null400: true,
    nullNumberQuarantine: { rest409: true, nativeRefusal: true, stdio409: true, completeStateUnchanged: true,
      activeReplayAllTransports: true, unrelatedProjectAndRepositoryAllowed: true } }));
} finally {
  child.stdin.end();
  if (child.exitCode === null) child.kill();
  for (const project of projects.reverse()) await api("DELETE", `/api/projects/${project.id}`, undefined, 204);
}
