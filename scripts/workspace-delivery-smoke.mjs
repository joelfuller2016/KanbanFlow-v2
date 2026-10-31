import assert from "node:assert/strict";
import { openWorkspaceStdio, workspaceNativePacket, workspaceToolValue } from "./workspace-transport.mjs";

const base = process.env.KANBANFLOW_API_URL?.replace(/\/$/, "");
assert.ok(base, "Set an explicit disposable API URL. This check creates and removes labeled fixture projects/tasks.");
const address = new URL(base);
assert.ok(["127.0.0.1", "localhost", "[::1]"].includes(address.hostname), "Use a disposable loopback app.");
assert.ok(!address.username && !address.password && address.pathname === "/" && !address.search && !address.hash,
  "Provide a local API origin without credentials, path, query or fragment.");
const prefix = `workspace-delivery-${Date.now()}`;
const actor = "delivery-smoke";
const normalized = value => Array.isArray(value) ? value.map(normalized) : value && typeof value === "object"
  ? Object.fromEntries(Object.entries(value).filter(([, entry]) => entry !== null).map(([key, entry]) => [key, normalized(entry)])) : value;
const equal = (actual, expected) => assert.deepEqual(normalized(actual), normalized(expected));
async function api(method, path, body, expected = 200) {
  const response = await fetch(base + path, { method, headers: { "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body), signal: AbortSignal.timeout(20000) });
  const raw = await response.text();
  assert.equal(response.status, expected, `${method} ${path}: ${raw}`);
  return raw ? JSON.parse(raw) : null;
}
const nativePacket = (name, args = {}) => workspaceNativePacket(base, "tools/call", { name, arguments: args });
const native = async (name, args = {}) => workspaceToolValue(await nativePacket(name, args));
const client = await openWorkspaceStdio(base);
const stdio = async (name, args = {}) => workspaceToolValue(await client.call(name, args));
const context = key => api("GET", `/api/workspaces/${key}/context`);
const changes = key => api("GET", `/api/workspaces/${key}/changes?limit=500`);
let sequence = 0;
async function command(transport, key, action, payload, name, explicit) {
  const current = await context(key);
  const envelope = explicit ?? { workspaceId: key, operationId: `${prefix}-${++sequence}`, expectedRevision: current.revision, action, payload, actor };
  let result;
  if (transport === "REST") result = await api("POST", `/api/workspaces/${key}/commands`, { ...envelope, workspaceId: undefined });
  else {
    const { action: ignored, ...args } = envelope;
    result = await (transport === "native" ? native : stdio)(name ?? "workspace_command", name ? args : { ...args, action });
  }
  return { result, envelope };
}
const sourceText = "\r\n    Complete source Ω  \r\n".repeat(4000) + "FINAL ORIGINAL REQUIREMENT  \r\n";
const taskText = "\r\n    Complete task specification Ω  \r\n".repeat(3000) + "FINAL TASK REQUIREMENT  \r\n";
const checks = [];
let a, b, project, createdProject;
try {
  for (const name of ["link_workspace_project", "create_workspace_project", "preview_workspace_publication", "publish_workspace_plan"]) {
    const tools = (await workspaceNativePacket(base, "tools/list", {})).result.tools;
    assert.ok(tools.some(tool => tool.name === name), `Native discovery missing ${name}`);
    assert.ok((await client.rpc("tools/list", {})).result.tools.some(tool => tool.name === name), `Stdio discovery missing ${name}`);
  }
  project = await api("POST", "/api/projects", { name: `${prefix} standalone`, description: "Current project description\n    retain internal spaces  \nEND", goals: "Existing goals", readme: "# Existing readme\n\n" + sourceText + "END" }, 201);
  a = await native("create_workspace", { operationId: `${prefix}-a`, actor, title: `${prefix} A`, body: sourceText, plan: "  Original A plan  \r\n" });
  b = await stdio("create_workspace", { operationId: `${prefix}-b`, actor, title: `${prefix} B`, body: "Different B source  \r\n", plan: "Different B plan" });
  const linkBody = { operationId: `${prefix}-link-a`, expectedRevision: a.revision, actor, projectId: project.id };
  await api("POST", `/api/workspaces/${a.workspaceId}/project-association`, linkBody);
  await command("native", b.workspaceId, "link_project", { projectId: project.id }, "link_workspace_project");
  const [linkedA, linkedB, primary] = await Promise.all([context(a.workspaceId), context(b.workspaceId), context(`project:${project.id}`)]);
  assert.equal(linkedA.projectId, project.id);
  assert.equal(linkedB.projectId, project.id);
  assert.equal(linkedA.source.status, "Promoted");
  assert.equal(linkedA.source.projectId, project.id);
  assert.equal(linkedA.originalSource.body, sourceText);
  assert.equal(linkedA.originalProjectSource.readme, project.readme);
  assert.equal(primary.originalSource.readme, project.readme);
  assert.equal(primary.kind, "project");
  checks.push("standalone-project-and-two-exact-source-associations");

  const baseline = linkedA.projectPlanRevision;
  await command("native", a.workspaceId, "propose_plan", { title: "A contribution", expectedProjectPlanRevision: baseline, items: [
    { id: "step-1", title: "A first task", description: taskText, acceptanceCriteria: ["  Exact A criterion Ω  ", "Preserve final requirement"], dependsOn: [] },
    { id: "step-2", title: "A dependent task", description: "  Dependency direction stays explicit  \r\n", acceptanceCriteria: ["Predecessor retained"], dependsOn: ["step-1"] },
  ] }, "propose_workspace_plan");
  await command("stdio", b.workspaceId, "propose_plan", { title: "B contribution", expectedProjectPlanRevision: baseline, items: [
    { id: "step-1", title: "B independently named task", description: "  Distinct source provenance  \r\n", acceptanceCriteria: ["Do not reuse A's task"], dependsOn: [] },
  ] }, "propose_workspace_plan");
  const planA = (await context(a.workspaceId)).document.planRevisions.at(-1);
  const planB = (await context(b.workspaceId)).document.planRevisions.at(-1);
  await command("native", a.workspaceId, "accept_plan", { planRevisionId: planA.id, expectedProjectPlanRevision: baseline }, "accept_workspace_plan");
  const beforeStale = await context(b.workspaceId);
  const stale = { operationId: `${prefix}-stale`, expectedRevision: beforeStale.revision, actor, action: "accept_plan", payload: { planRevisionId: planB.id, expectedProjectPlanRevision: baseline } };
  const failure = await api("POST", `/api/workspaces/${b.workspaceId}/commands`, stale, 409);
  const nativeFailure = await nativePacket("accept_workspace_plan", { workspaceId: b.workspaceId, ...stale, action: undefined });
  assert.equal(nativeFailure.result.isError, true);
  assert.ok(nativeFailure.result.content.some(item => item.text?.includes(failure.code)));
  const stdioFailure = await client.call("accept_workspace_plan", { workspaceId: b.workspaceId, ...stale, action: undefined });
  assert.equal(stdioFailure.error.data.status, 409);
  assert.equal(stdioFailure.error.data.payload.code, failure.code);
  equal(await context(b.workspaceId), beforeStale);
  await command("stdio", b.workspaceId, "accept_plan", { planRevisionId: planB.id, expectedProjectPlanRevision: beforeStale.projectPlanRevision }, "accept_workspace_plan");
  let canonical = await context(`project:${project.id}`);
  assert.equal(canonical.acceptedPlan.items.length, 3);
  const firstA = canonical.acceptedPlan.items.find(item => item.provenance.sourceWorkspaceId === a.workspaceId && item.provenance.sourceItemId === "step-1");
  const secondA = canonical.acceptedPlan.items.find(item => item.provenance.sourceWorkspaceId === a.workspaceId && item.provenance.sourceItemId === "step-2");
  const firstB = canonical.acceptedPlan.items.find(item => item.provenance.sourceWorkspaceId === b.workspaceId);
  assert.notEqual(firstA.id, firstB.id);
  assert.equal(firstA.description, taskText);
  assert.deepEqual(secondA.dependsOn, [firstA.id]);
  assert.equal((await context(a.workspaceId)).acceptedPlan.id, (await context(b.workspaceId)).acceptedPlan.id);
  checks.push("canonical-expansion-distinct-provenance-and-cross-client-stale-conflicts");

  let publishing = await context(b.workspaceId);
  const previewArgs = { workspaceId: b.workspaceId, expectedRevision: publishing.revision, expectedProjectPlanRevision: publishing.projectPlanRevision, itemIds: [firstA.id] };
  const beforePreview = { source: publishing, project: canonical, changes: await changes(b.workspaceId), tasks: await api("GET", `/api/projects/${project.id}/tasks`) };
  const { workspaceId: ignoredKey, ...previewBody } = previewArgs;
  const preview = await api("POST", `/api/workspaces/${b.workspaceId}/publication-preview`, previewBody);
  equal(await native("preview_workspace_publication", previewArgs), preview);
  equal(await stdio("preview_workspace_publication", previewArgs), preview);
  assert.equal(preview.newTaskCount, 1);
  assert.equal(preview.items[0].description, taskText);
  equal(await context(b.workspaceId), beforePreview.source);
  equal(await context(`project:${project.id}`), beforePreview.project);
  equal(await changes(b.workspaceId), beforePreview.changes);
  equal(await api("GET", `/api/projects/${project.id}/tasks`), beforePreview.tasks);
  assert.equal((await api("POST", `/api/workspaces/${b.workspaceId}/publication-preview`, { ...previewBody, itemIds: [secondA.id] }, 400)).code, "missing_publication_dependency");
  checks.push("full-content-three-transport-readonly-preview-and-subset-validation");

  const publishEnvelope = { workspaceId: b.workspaceId, operationId: `${prefix}-publish-first`, expectedRevision: publishing.revision,
    actor, action: "publish_plan", payload: { expectedProjectPlanRevision: publishing.projectPlanRevision, itemIds: [firstA.id] } };
  const published = await command("REST", b.workspaceId, "publish_plan", publishEnvelope.payload, null, publishEnvelope);
  const firstTask = published.result.publication.items[0];
  assert.equal(published.result.publication.newTaskCount, 1);
  const replay = await command("native", b.workspaceId, "publish_plan", publishEnvelope.payload, "publish_workspace_plan", publishEnvelope);
  equal(replay.result, { ...published.result, replayed: true });
  let task = await api("GET", `/api/tasks/${firstTask.taskId}`);
  assert.equal(task.description, taskText);
  const criteria = await api("GET", `/api/tasks/${firstTask.taskId}/acceptance-criteria`);
  assert.deepEqual(criteria.map(item => item.text), ["  Exact A criterion Ω  ", "Preserve final requirement"]);
  for (const criterion of criteria) await api("PUT", `/api/acceptance-criteria/${criterion.id}`, { completed: true });
  await api("PUT", `/api/tasks/${firstTask.taskId}`, { name: "Human reviewed task", description: "Human-edited description Ω  \r\n", priority: "High", assignee: "human-verifier", actor: "human-verifier" });
  await api("PATCH", `/api/tasks/${firstTask.taskId}/status`, { status: "Done", actor: "human-verifier" });
  const human = await api("GET", `/api/tasks/${firstTask.taskId}`);
  const humanCriteria = await api("GET", `/api/tasks/${firstTask.taskId}/acceptance-criteria`);
  publishing = await context(b.workspaceId);
  const repeated = await command("stdio", b.workspaceId, "publish_plan", { expectedProjectPlanRevision: publishing.projectPlanRevision, itemIds: [firstA.id] }, "publish_workspace_plan");
  assert.equal(repeated.result.publication.newTaskCount, 0);
  assert.equal(repeated.result.publication.items[0].taskId, firstTask.taskId);
  equal(await api("GET", `/api/tasks/${firstTask.taskId}`), human);
  equal(await api("GET", `/api/tasks/${firstTask.taskId}/acceptance-criteria`), humanCriteria);
  publishing = await context(b.workspaceId);
  const remaining = await command("native", b.workspaceId, "publish_plan", { expectedProjectPlanRevision: publishing.projectPlanRevision, itemIds: [secondA.id, firstB.id] }, "publish_workspace_plan");
  assert.equal(remaining.result.publication.newTaskCount, 2);
  assert.equal((await api("GET", `/api/projects/${project.id}/tasks`)).length, 3);
  const secondTask = remaining.result.publication.items.find(item => item.canonicalItemId === secondA.id);
  const dependency = await api("GET", `/api/tasks/${secondTask.taskId}/dependencies`);
  assert.ok(dependency.some(edge => edge.taskItemId === secondTask.taskId && edge.dependsOnTaskItemId === firstTask.taskId));
  equal(await api("GET", `/api/tasks/${firstTask.taskId}`), human);
  checks.push("subset-publication-replay-real-criteria-dependencies-and-human-done-preservation");

  const projectKey = `project:${project.id}`;
  canonical = await context(projectKey);
  await command("REST", projectKey, "propose_plan", { title: "Expanded canonical plan", expectedProjectPlanRevision: canonical.revision,
    items: [...canonical.acceptedPlan.items, { id: "new-increment", title: "One new increment", description: "  Expand the accepted project plan  \r\n", acceptanceCriteria: ["Create only one new task"], dependsOn: [firstA.id] }] });
  canonical = await context(projectKey);
  const expanded = canonical.document.planRevisions.at(-1);
  await command("native", projectKey, "accept_plan", { planRevisionId: expanded.id, expectedProjectPlanRevision: canonical.revision }, "accept_workspace_plan");
  canonical = await context(projectKey);
  const expansion = await command("stdio", projectKey, "publish_plan", { expectedProjectPlanRevision: canonical.revision }, "publish_workspace_plan");
  assert.equal(expansion.result.publication.newTaskCount, 1);
  assert.equal(expansion.result.publication.existingTaskCount, 3);
  assert.equal((await api("GET", `/api/projects/${project.id}/tasks`)).length, 4);
  equal(await api("GET", `/api/tasks/${firstTask.taskId}`), human);
  const reference = await command("native", a.workspaceId, "create_component", { component: { type: "task-reference", title: "Live human work", content: "Keep this reference", taskId: firstTask.taskId } }, "create_workspace_component");
  const referenceId = reference.result.componentId;
  assert.equal((await native("get_workspace_component", { workspaceId: a.workspaceId, componentId: referenceId })).task.status, "Done");
  checks.push("canonical-copy-expands-one-task-and-live-reference-reads-done");

  const removedTask = remaining.result.publication.items.find(item => item.canonicalItemId === firstB.id).taskId;
  await api("DELETE", `/api/tasks/${removedTask}?actor=delivery-smoke`, undefined, 204);
  publishing = await context(b.workspaceId);
  const beforeMissing = await context(projectKey);
  const missing = await api("POST", `/api/workspaces/${b.workspaceId}/commands`, {
    operationId: `${prefix}-missing`, expectedRevision: publishing.revision, actor, action: "publish_plan",
    payload: { expectedProjectPlanRevision: publishing.projectPlanRevision },
  }, 409);
  assert.equal(missing.code, "publication_target_missing");
  equal(await context(projectKey), beforeMissing);
  await api("DELETE", `/api/projects/${project.id}`, undefined, 204);
  const deleted = await stdio("get_workspace_context", { workspaceId: a.workspaceId });
  assert.equal(deleted.projectMissing, true);
  assert.equal(deleted.originalProjectSource.readme, project.readme);
  assert.equal(deleted.originalSource.body, sourceText);
  const missingReference = await native("get_workspace_component", { workspaceId: a.workspaceId, componentId: referenceId });
  assert.equal(missingReference.taskMissing, true);
  assert.equal(missingReference.component.content, "Keep this reference");
  checks.push("missing-task-and-project-retain-content-without-silent-recreation");

  const c = await native("create_workspace", { operationId: `${prefix}-c`, actor, title: `${prefix} new-project source`, body: sourceText, plan: "  Full new-project plan  \r\n" });
  const createEnvelope = { workspaceId: c.workspaceId, operationId: `${prefix}-new-project`, expectedRevision: c.revision, actor, action: "create_project", payload: { name: `${prefix} created project` } };
  const creation = await command("stdio", c.workspaceId, "create_project", createEnvelope.payload, "create_workspace_project", createEnvelope);
  createdProject = creation.result.projectId;
  assert.ok(createdProject);
  equal((await command("native", c.workspaceId, "create_project", createEnvelope.payload, "create_workspace_project", createEnvelope)).result, { ...creation.result, replayed: true });
  const newContext = await context(c.workspaceId);
  assert.equal(newContext.originalSource.body, sourceText);
  assert.equal(newContext.projectSource.body, sourceText);
  assert.equal(newContext.projectSource.plan, "  Full new-project plan  \r\n");
  assert.ok(newContext.projectSource.readme.includes(sourceText));
  assert.equal(newContext.kanbanUrl, `/project/${createdProject}`);
  checks.push("new-project-exact-documents-and-cross-transport-idempotency");

  // Compatibility path: the optional old Ideas board can promote after a canvas was already saved.
  const legacy = await native("create_workspace", { operationId: `${prefix}-legacy`, actor,
    title: `${prefix} legacy promotion`, body: sourceText, plan: "Legacy plan" });
  await native("promote_idea", { ideaId: Number(legacy.workspaceId.split(":")[1]), actor });
  const legacyLive = await context(legacy.workspaceId);
  assert.ok(legacyLive.projectId);
  await command("stdio", legacy.workspaceId, "add_comment", { text: "Ordinary edit observes the promoted target" }, "add_workspace_comment");
  const capturedLegacy = await context(legacy.workspaceId);
  await api("DELETE", `/api/projects/${legacyLive.projectId}`, undefined, 204);
  const missingLegacy = await context(legacy.workspaceId);
  assert.equal(missingLegacy.projectId, legacyLive.projectId);
  assert.equal(missingLegacy.projectMissing, true);
  assert.equal(missingLegacy.originalProjectSource.readme, capturedLegacy.originalProjectSource.readme);
  assert.equal(missingLegacy.originalSource.body, sourceText);
  await api("POST", `/api/workspaces/${legacy.workspaceId}/project-association`, {
    operationId: `${prefix}-implicit-relink`, expectedRevision: missingLegacy.revision, actor, projectId: createdProject,
  }, 409);
  await command("native", legacy.workspaceId, "link_project", { projectId: createdProject, expectedCurrentProjectId: legacyLive.projectId }, "link_workspace_project");
  assert.equal((await context(legacy.workspaceId)).projectId, createdProject);
  assert.equal((await context(`project:${legacyLive.projectId}`)).originalSource.readme, capturedLegacy.originalProjectSource.readme);
  checks.push("legacy-promotion-after-canvas-save-retains-deleted-target-and-requires-explicit-relink");

  const collisionKey = `project:${createdProject}`;
  await command("native", collisionKey, "propose_plan", { title: "Canonical identity base", items: [
    { id: "step-1", title: "Preserved existing item", description: "Complete original unpublished item Ω  \r\n", acceptanceCriteria: [], dependsOn: [] },
  ] }, "propose_workspace_plan");
  let collisionContext = await context(collisionKey);
  await command("native", collisionKey, "accept_plan", { planRevisionId: collisionContext.document.planRevisions.at(-1).id,
    expectedProjectPlanRevision: collisionContext.revision }, "accept_workspace_plan");
  collisionContext = await context(collisionKey);
  const retained = collisionContext.acceptedPlan.items[0];
  await command("stdio", collisionKey, "propose_plan", { title: "Ambiguous duplicate origin", items: [retained,
    { id: "step-1", title: "Different authored item must not replace it", description: "Second complete item Ω  \r\n", acceptanceCriteria: [], dependsOn: [] },
  ] }, "propose_workspace_plan");
  const beforeCollision = await context(collisionKey);
  const collisionEvents = await changes(collisionKey);
  const collision = await api("POST", `/api/workspaces/${collisionKey}/commands`, {
    operationId: `${prefix}-collision`, expectedRevision: beforeCollision.revision, actor, action: "accept_plan",
    payload: { planRevisionId: beforeCollision.document.planRevisions.at(-1).id, expectedProjectPlanRevision: beforeCollision.revision },
  }, 409);
  assert.equal(collision.code, "duplicate_canonical_identity");
  equal(await context(collisionKey), beforeCollision);
  equal(await changes(collisionKey), collisionEvents);
  assert.equal((await api("GET", `/api/projects/${createdProject}/tasks`)).length, 0);
  checks.push("resolved-provenance-collision-refuses-without-dropping-either-authored-item");
  console.log(JSON.stringify({ result: "PASS", prefix, checks, workspaceA: a.workspaceId, workspaceB: b.workspaceId,
    removedProjectId: project.id, createdProjectId: createdProject, preservedSourceCharacters: sourceText.length,
    preservedTaskCharacters: taskText.length, originalTaskId: firstTask.taskId, liveReferenceId: referenceId,
    newProjectWorkspace: c.workspaceId, commands: sequence }));
} finally { await client.close(); }
