// Run prepare and verify as separate processes, with an actual app stop/start by the caller between them.
// KANBANFLOW_API_URL must name disposable data. The required third argument is a JSON path under this repo's ignored _sandbox/.
// node scripts/workspace-delivery-restart-smoke.mjs prepare _sandbox/579/delivery-restart.json
// node scripts/workspace-delivery-restart-smoke.mjs verify  _sandbox/579/delivery-restart.json
import assert from "node:assert/strict";
import { existsSync, mkdirSync, readFileSync, realpathSync, writeFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { openWorkspaceStdio, workspaceNativePacket, workspaceToolValue } from "./workspace-transport.mjs";

const phase = process.argv[2];
assert.ok(phase === "prepare" || phase === "verify", "Choose prepare or verify; the caller restarts the app between phases.");
const base = process.env.KANBANFLOW_API_URL?.replace(/\/$/, "");
assert.ok(base, "Set KANBANFLOW_API_URL to an explicitly disposable local app.");
const address = new URL(base);
assert.ok(["127.0.0.1", "localhost", "[::1]"].includes(address.hostname), "Use a disposable loopback app.");
assert.ok(!address.username && !address.password && address.pathname === "/" && !address.search && !address.hash,
  "Provide a local API origin without credentials, path, query or fragment.");
const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const sandbox = path.resolve(repo, "_sandbox");
assert.ok(readFileSync(path.join(repo, ".gitignore"), "utf8").split(/\r?\n/).includes("_sandbox/"), "The repository must ignore _sandbox/ artifacts.");
assert.ok(process.argv[3], "Provide the required JSON artifact path under the repository's ignored _sandbox/.");
const artifact = path.resolve(process.argv[3]);
const inside = (parent, target) => {
  const relative = path.relative(parent, target);
  return relative.length > 0 && relative !== ".." && !relative.startsWith(".." + path.sep) && !path.isAbsolute(relative);
};
assert.ok(inside(sandbox, artifact) && path.extname(artifact).toLowerCase() === ".json", "Artifact must be a JSON file inside this repository's _sandbox/.");
if (phase === "prepare") {
  assert.ok(!existsSync(artifact), "Choose a new artifact path so an earlier restart receipt is preserved.");
  mkdirSync(sandbox, { recursive: true });
  let ancestor = path.dirname(artifact);
  while (!existsSync(ancestor)) ancestor = path.dirname(ancestor);
  assert.ok(path.relative(realpathSync(sandbox), realpathSync(ancestor)) === "" || inside(realpathSync(sandbox), realpathSync(ancestor)),
    "Artifact parent must resolve inside the repository sandbox.");
  mkdirSync(path.dirname(artifact), { recursive: true });
} else {
  assert.ok(existsSync(artifact), "Run prepare first and retain its artifact across the app restart.");
  assert.ok(inside(realpathSync(sandbox), realpathSync(artifact)), "Artifact must resolve inside this repository's sandbox.");
}

// Native SDK can omit optional object null properties. No string, array, timestamp or number is normalized.
const normalized = value => Array.isArray(value) ? value.map(normalized) : value && typeof value === "object"
  ? Object.fromEntries(Object.entries(value).filter(([, item]) => item !== null).map(([key, item]) => [key, normalized(item)])) : value;
const equal = (actual, expected, message) => assert.deepEqual(normalized(actual), normalized(expected), message);
async function api(method, suffix, body, expected = 200) {
  const response = await fetch(base + suffix, { method, headers: { "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body), signal: AbortSignal.timeout(20000) });
  const text = await response.text();
  assert.equal(response.status, expected, `${method} ${suffix}: ${text}`);
  return text ? JSON.parse(text) : null;
}
const native = async (name, args = {}) => workspaceToolValue(await workspaceNativePacket(base, "tools/call", { name, arguments: args }));
const client = await openWorkspaceStdio(base);
const stdio = async (name, args = {}) => workspaceToolValue(await client.call(name, args));
const context = workspaceId => api("GET", `/api/workspaces/${workspaceId}/context`);
let sequence = 0;
async function send(transport, envelope) {
  if (transport === "REST") {
    const { workspaceId, ...body } = envelope;
    return api("POST", `/api/workspaces/${workspaceId}/commands`, body);
  }
  return (transport === "native" ? native : stdio)("workspace_command", envelope);
}
async function command(transport, fixture, workspaceId, action, payload) {
  const current = await context(workspaceId);
  const envelope = { workspaceId, operationId: `${fixture.prefix}-operation-${++sequence}`, expectedRevision: current.revision,
    action, payload, actor: fixture.actor };
  return { envelope, result: await send(transport, envelope) };
}
async function changes(workspaceId) {
  const page = await api("GET", `/api/workspaces/${workspaceId}/changes?limit=500`);
  assert.equal(page.nextCursor, null, "The bounded restart fixture must have its complete activity in the response.");
  return page;
}
async function snapshot(fixture) {
  const [source, canonical, task, projectTasks, taskActivity, sourceChanges, canonicalChanges, reference, catalog] = await Promise.all([
    context(fixture.workspaceId), context(fixture.projectWorkspaceId), api("GET", `/api/tasks/${fixture.taskId}`),
    api("GET", `/api/projects/${fixture.projectId}/tasks`), api("GET", `/api/tasks/${fixture.taskId}/activity`),
    changes(fixture.workspaceId), changes(fixture.projectWorkspaceId),
    api("GET", `/api/workspaces/${fixture.workspaceId}/component?componentId=${encodeURIComponent(fixture.referenceId)}`),
    api("GET", `/api/workspaces?q=${encodeURIComponent(fixture.prefix)}&limit=500`),
  ]);
  assert.equal(catalog.nextCursor, null);
  return { source, canonical, task, projectTasks, taskActivity, sourceChanges, canonicalChanges, reference,
    catalog: { total: catalog.total, keys: catalog.items.map(item => item.workspaceId).sort() } };
}
async function checkTransports(fixture, expected) {
  for (const call of [native, stdio]) {
    equal(await call("get_workspace_context", { workspaceId: fixture.workspaceId }), expected.source, "Resumed idea context must agree across transports.");
    equal(await call("get_workspace_context", { workspaceId: fixture.projectWorkspaceId }), expected.canonical, "Resumed canonical context must agree across transports.");
    equal(await call("get_workspace_component", { workspaceId: fixture.workspaceId, componentId: fixture.referenceId }), expected.reference,
      "Current task reference must agree across transports after human changes.");
    const preview = await call("preview_workspace_publication", { workspaceId: fixture.workspaceId,
      expectedRevision: expected.source.revision, expectedProjectPlanRevision: expected.source.projectPlanRevision });
    assert.equal(preview.newTaskCount, 0);
    assert.equal(preview.existingTaskCount, 1);
    assert.equal(preview.items[0].canonicalItemId, fixture.canonicalItemId);
    assert.equal(preview.items[0].existingTask.id, fixture.taskId);
    assert.equal(preview.items[0].existingTask.status, "Done");
    assert.equal(preview.items[0].existingTask.description, fixture.humanDescription);
    assert.equal(preview.items[0].description, fixture.specification);
  }
}
function assertSavedFixture(fixture, state) {
  assert.equal(state.source.originalSource.title, fixture.capture.title);
  assert.equal(state.source.originalSource.body, fixture.capture.body);
  assert.equal(state.source.originalSource.plan, fixture.capture.plan);
  assert.equal(state.source.projectId, fixture.projectId);
  assert.equal(state.source.projectMissing, false);
  assert.equal(state.source.originalProjectSource.readme, fixture.initialProject.readme);
  const captured = state.source.projectSourceSnapshots.find(item => item.source.readme === fixture.laterProject.readme);
  assert.ok(captured, "The source history observed by a module write must survive restart.");
  assert.equal(captured.source.body, fixture.laterProject.description);
  assert.equal(captured.source.plan, fixture.laterProject.goals);
  assert.equal(captured.actor, fixture.actor);
  const canonicalItem = state.canonical.acceptedPlan.items.find(item => item.id === fixture.canonicalItemId);
  assert.ok(canonicalItem);
  equal(canonicalItem.provenance, fixture.provenance);
  assert.equal(canonicalItem.description, fixture.specification);
  assert.equal(canonicalItem.publishedTaskId, fixture.taskId);
  assert.equal(state.source.acceptedPlan.id, state.canonical.acceptedPlan.id);
  assert.equal(state.canonical.document.publications.length, 1);
  assert.equal(state.canonical.document.publications[0].taskId, fixture.taskId);
  assert.equal(state.task.id, fixture.taskId);
  assert.equal(state.task.status, "Done");
  assert.equal(state.task.name, fixture.humanName);
  assert.equal(state.task.description, fixture.humanDescription);
  assert.equal(state.task.sortOrder, 4321.25);
  assert.ok(state.task.completedAt);
  assert.ok(state.task.acceptanceCriteria.every(item => item.completed));
  assert.equal(state.projectTasks.length, 1);
  assert.equal(state.projectTasks[0].id, fixture.taskId);
  assert.equal(state.reference.task.id, fixture.taskId);
  assert.equal(state.reference.task.status, "Done");
  assert.equal(state.reference.task.description, fixture.humanDescription);
  assert.equal(state.source.document.checkpoints.length, 2);
  for (const checkpoint of fixture.checkpoints) {
    const current = state.source.document.checkpoints.find(item => item.clientId === checkpoint.clientId && item.sessionId === checkpoint.sessionId);
    assert.ok(current);
    for (const field of ["objective", "focusedComponentIds", "completedChanges", "openQuestions", "nextActions", "lastAcknowledgedActivityId"])
      equal(current[field], checkpoint[field], `Checkpoint ${checkpoint.clientId} ${field} must remain complete.`);
  }
  equal(state.catalog, { total: 2, keys: [fixture.workspaceId, fixture.projectWorkspaceId].sort() }, "Restart and retries must not create another source workspace.");
}

try {
  if (phase === "prepare") {
    const fixture = { schemaVersion: 1, prefix: `workspace-delivery-restart-${Date.now()}`, actor: "delivery-restart-smoke", checkpoints: [] };
    fixture.capture = { operationId: `${fixture.prefix}-capture`, actor: fixture.actor, title: `${fixture.prefix} idea`,
      body: "\r\n  # Complete restart source Ω\r\n\r\n    preserve indentation and spaces  \r\n".repeat(65) + "\r\nFINAL ORIGINAL SOURCE Ω  \r\n",
      plan: "  ## Original restart plan Ω\r\n\r\n- Preserve the final requirement  \r\n" };
    fixture.captureResult = await api("POST", "/api/workspaces", fixture.capture);
    fixture.workspaceId = fixture.captureResult.workspaceId;
    fixture.initialProject = await api("POST", "/api/projects", { name: `${fixture.prefix} project`, description: "Original project description Ω\n    full internal spacing  \nEND",
      goals: "Original project goals Ω", readme: "# Original restart readme Ω\n\n" + "Complete original project evidence. ".repeat(60) + "\nFINAL ORIGINAL README" }, 201);
    fixture.projectId = fixture.initialProject.id;
    fixture.projectWorkspaceId = `project:${fixture.projectId}`;
    await command("native", fixture, fixture.workspaceId, "link_project", { projectId: fixture.projectId });
    fixture.specification = "\r\n  ## Accepted restart specification Ω\r\n\r\n    preserve all task context  \r\n".repeat(80) + "\r\nFINAL ACCEPTED SPECIFICATION Ω  \r\n";
    await command("stdio", fixture, fixture.workspaceId, "propose_plan", { title: `${fixture.prefix} accepted plan`,
      items: [{ id: "restart-step-1", title: "Published restart task", description: fixture.specification,
        acceptanceCriteria: ["  Preserve the full requirement Ω  ", "Second complete criterion Ω"], dependsOn: [] }],
      expectedProjectPlanRevision: (await context(fixture.workspaceId)).projectPlanRevision });
    const draft = (await context(fixture.workspaceId)).document.planRevisions.at(-1);
    await command("native", fixture, fixture.workspaceId, "accept_plan", { planRevisionId: draft.id,
      expectedProjectPlanRevision: (await context(fixture.workspaceId)).projectPlanRevision });
    const beforePublish = await context(fixture.workspaceId);
    fixture.publicationEnvelope = { workspaceId: fixture.workspaceId, operationId: `${fixture.prefix}-immutable-publication`,
      expectedRevision: beforePublish.revision, action: "publish_plan", actor: fixture.actor,
      payload: { expectedProjectPlanRevision: beforePublish.projectPlanRevision } };
    fixture.publicationResult = await send("REST", fixture.publicationEnvelope);
    assert.equal(fixture.publicationResult.publication.newTaskCount, 1);
    const mapping = fixture.publicationResult.publication.items[0];
    fixture.taskId = mapping.taskId;
    fixture.canonicalItemId = mapping.canonicalItemId;
    fixture.provenance = (await context(fixture.projectWorkspaceId)).acceptedPlan.items[0].provenance;

    const publishedTask = await api("GET", `/api/tasks/${fixture.taskId}`);
    for (const criterion of publishedTask.acceptanceCriteria)
      await api("PUT", `/api/acceptance-criteria/${criterion.id}`, { completed: true });
    fixture.humanName = `${fixture.prefix} human completed task`;
    // Existing TaskService trims outer whitespace, so the human edit starts/ends with text and retains internal Markdown spacing exactly.
    fixture.humanDescription = "# Human completed task Ω\n\n" + "Complete human-expanded delivery context 漢字 🧭. ".repeat(90)
      + "\n\n```text\n    meaningful internal indentation and spaces  \n```\n\nFINAL HUMAN TASK CONTENT";
    await api("PUT", `/api/tasks/${fixture.taskId}`, { name: fixture.humanName, description: fixture.humanDescription,
      status: "Done", priority: "High", assignee: "human-restart-owner", sortOrder: 4321.25, actor: "human-restart-owner" });
    fixture.reference = await command("native", fixture, fixture.workspaceId, "create_component", { component: {
      id: "restart-live-task", type: "task-reference", title: "Current human-completed delivery", content: "\r\n  Read the real task before continuing Ω  \r\n", taskId: fixture.taskId } });
    fixture.referenceId = fixture.reference.result.componentId;
    for (const [transport, clientId] of [["native", "restart-native-client"], ["stdio", "restart-stdio-client"]]) {
      const current = await context(fixture.workspaceId);
      const checkpoint = { clientId, sessionId: "restart-session", objective: `  Continue ${clientId} Ω  `,
        focusedComponentIds: [fixture.referenceId], completedChanges: ["Published the accepted plan", "  Human completed the task Ω  "],
        openQuestions: ["What should the next plan increment contain?"], nextActions: ["  Inspect current task before any changes Ω  "],
        lastAcknowledgedActivityId: current.lastActivityId };
      await command(transport, fixture, fixture.workspaceId, "save_checkpoint", checkpoint);
      fixture.checkpoints.push(checkpoint);
    }
    fixture.laterProject = await api("PUT", `/api/projects/${fixture.projectId}`, { name: fixture.initialProject.name,
      description: "# Later project description Ω\n\n    exact later internal spaces  \nFINAL LATER DESCRIPTION",
      goals: "## Later goals Ω\n\n- Preserve this later goal  \nFINAL LATER GOAL",
      readme: "# Later observed restart readme Ω\n\n" + "Complete later project history 漢字. ".repeat(70) + "\n\n    retain readme spaces  \nFINAL OBSERVED README" });
    await command("stdio", fixture, fixture.projectWorkspaceId, "add_comment", { text: "Observe complete later project documents before restart" });
    fixture.expected = await snapshot(fixture);
    assertSavedFixture(fixture, fixture.expected);
    await checkTransports(fixture, fixture.expected);
    equal(await snapshot(fixture), fixture.expected, "Preparation's cross-transport reads and previews must not mutate saved data.");
    fixture.preparedAt = new Date().toISOString();
    writeFileSync(artifact, JSON.stringify(fixture, null, 2) + "\n", { encoding: "utf8", flag: "wx" });
    console.log(JSON.stringify({ result: "PASS", phase, artifact, workspaceId: fixture.workspaceId, projectId: fixture.projectId,
      taskId: fixture.taskId, sourceCharacters: fixture.capture.body.length, taskCharacters: fixture.specification.length,
      checks: ["actual-three-transport-fixture", "accepted-provenance-and-task-mapping", "human-done-content-and-completed-criteria", "two-client-checkpoints", "observed-source-history", "immutable-publication-envelope-saved"] }));
  } else {
    const fixture = JSON.parse(readFileSync(artifact, "utf8"));
    assert.equal(fixture.schemaVersion, 1);
    assert.ok(fixture.prefix?.startsWith("workspace-delivery-restart-"), "Only this script's labeled disposable fixture can be verified.");
    const resumed = await snapshot(fixture);
    assertSavedFixture(fixture, resumed);
    equal(resumed, fixture.expected, "An actual restarted app must recover the exact prepared data and activity.");
    await checkTransports(fixture, resumed);
    equal(await snapshot(fixture), resumed, "Fresh native and stdio reads/previews must remain read-only.");
    for (const transport of ["REST", "native", "stdio"]) {
      const replay = await send(transport, fixture.publicationEnvelope);
      equal(replay, { ...fixture.publicationResult, replayed: true }, `${transport} must return the original immutable publication receipt.`);
      equal(await snapshot(fixture), resumed, `${transport} publication replay must preserve current Done task, full content, criteria, history, checkpoints and task count.`);
    }
    equal(await native("create_workspace", fixture.capture), { ...fixture.captureResult, replayed: true }, "Capture receipt must recover the original source identity.");
    equal(await send("stdio", fixture.reference.envelope), { ...fixture.reference.result, replayed: true }, "Component command receipt must recover the original live task reference.");
    equal(await snapshot(fixture), resumed, "Capture and component receipt replays must not add duplicate sources, tasks, components or audit.");
    console.log(JSON.stringify({ result: "PASS", phase, artifact, workspaceId: fixture.workspaceId, projectId: fixture.projectId,
      taskId: fixture.taskId, checkpoints: resumed.source.document.checkpoints.length, sourceHistoryVersions: resumed.source.projectSourceSnapshots.length,
      checks: ["persisted-accepted-canonical-provenance", "current-human-done-task-content", "complete-source-history", "independent-checkpoints",
        "fresh-native-and-stdio-parity", "all-three-publication-receipts", "capture-and-command-receipts", "no-overwrite-or-duplicates"] }));
  }
} finally { await client.close(); }
