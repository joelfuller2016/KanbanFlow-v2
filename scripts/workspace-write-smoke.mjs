import assert from "node:assert/strict";
import { openWorkspaceStdio, workspaceNativePacket, workspaceToolValue } from "./workspace-transport.mjs";
import { workspaceWriteTools } from "./workspace-mcp-tools.mjs";

const base = process.env.KANBANFLOW_API_URL;
assert.ok(base, "Set KANBANFLOW_API_URL to an explicitly disposable workspace app.");
const prefix = `workspace-write-${Date.now()}`;
const actor = "workspace-smoke";
const domain = value => Array.isArray(value) ? value.map(domain) : value && typeof value === "object"
  ? Object.fromEntries(Object.entries(value).filter(([, item]) => item !== null).map(([key, item]) => [key, domain(item)])) : value;
const equal = (actual, expected) => assert.deepEqual(domain(actual), domain(expected));
async function api(method, path, body, expected = 200) {
  const response = await fetch(base + path, { method, headers: { "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body), signal: AbortSignal.timeout(20000) });
  const text = await response.text();
  assert.equal(response.status, expected, `${method} ${path}: ${text}`);
  return text ? JSON.parse(text) : null;
}
const nativePacket = (name, args = {}) => workspaceNativePacket(base, "tools/call", { name, arguments: args });
const native = async (name, args = {}) => workspaceToolValue(await nativePacket(name, args));
const client = await openWorkspaceStdio(base);
const stdio = async (name, args = {}) => workspaceToolValue(await client.call(name, args));
let commands = 0;
try {
  const nativeTools = (await workspaceNativePacket(base, "tools/list", {})).result.tools;
  const stdioTools = (await client.rpc("tools/list", {})).result.tools;
  for (const { name } of workspaceWriteTools) {
    assert.ok(nativeTools.some(tool => tool.name === name), `Native discovery missing ${name}`);
    assert.ok(stdioTools.some(tool => tool.name === name), `Stdio discovery missing ${name}`);
  }
  const capabilities = await api("GET", "/api/workspace-capabilities");
  equal(await native("get_workspace_capabilities"), capabilities);
  equal(await stdio("get_workspace_capabilities"), capabilities);
  assert.ok(capabilities.componentProperties.markdown.editableFields.includes("content"));
  assert.ok(capabilities.placementProperties.editableFields.includes("parentId"));
  assert.ok(capabilities.actionPayloads.move_components.fields.includes("placements"));

  const body = "\r\n    Original research Ω  \r\n".repeat(5000) + "LAST ORIGINAL LINE  \r\n";
  const plan = "\r\n    Plan with significant trailing spaces  \r\n";
  const capture = { operationId: `${prefix}-capture`, actor, title: `${prefix} idea A`, body, plan };
  const created = await api("POST", "/api/workspaces", capture);
  const key = created.workspaceId;
  assert.equal(created.revision, 1);
  for (const call of [native, stdio]) {
    const replay = await call("create_workspace", capture);
    equal(replay, { ...created, replayed: true });
  }
  const unsupported = { ...capture, operationId: `${prefix}-unsupported`, unsupportedReadme: "This requested text must never be silently lost." };
  await api("POST", "/api/workspaces", unsupported, 400);
  const unsupportedNative = await nativePacket("create_workspace", unsupported);
  assert.ok(unsupportedNative.error || unsupportedNative.result?.isError, "Native MCP must reject unsupported top-level arguments.");
  const unsupportedStdio = await client.call("create_workspace", unsupported);
  assert.ok(unsupportedStdio.error, "Stdio MCP must reject unsupported top-level arguments.");
  assert.equal((await api("POST", "/api/workspaces", { ...capture, body: body + "changed" }, 409)).code, "operation_conflict");
  const context = () => api("GET", `/api/workspaces/${key}/context`);
  const initial = await context();
  assert.equal(initial.originalSource.body, body);
  assert.equal(initial.originalSource.plan, plan);
  let revision = initial.revision;
  async function command(transport, name, action, payload) {
    const envelope = { workspaceId: key, operationId: `${prefix}-${++commands}`, expectedRevision: revision, payload, actor };
    const result = transport === "REST"
      ? await api("POST", `/api/workspaces/${key}/commands`, { ...envelope, workspaceId: undefined, action })
      : await (transport === "native" ? native : stdio)(name, envelope);
    assert.equal(result.revision, revision + 1);
    revision = result.revision;
    return { result, envelope };
  }
  async function routeCommand(method, suffix, payload) {
    const result = await api(method, `/api/workspaces/${key}/${suffix}`, {
      operationId: `${prefix}-route-${++commands}`, expectedRevision: revision, actor, ...payload,
    });
    assert.equal(result.revision, revision + 1);
    revision = result.revision;
    return result;
  }
  const content = "\r\n    Full editable content Ω  \r\n".repeat(4000) + "FINAL EDIT REQUIREMENT  \r\n";
  const added = await command("native", "create_workspace_component", "create_component", {
    component: { id: "smoke-research", type: "research", title: "Evidence", content, url: "https://example.com/research" },
    placement: { id: "smoke-research-place", parentId: "place-frame-research", x: 20, y: 60, width: 330, height: 220 },
  });
  equal(await stdio("create_workspace_component", added.envelope), { ...added.result, replayed: true });
  assert.equal((await stdio("get_workspace_component", { workspaceId: key, componentId: "smoke-research" })).component.content, content);
  await command("stdio", "move_workspace_components", "move_components", { placements: [
    { placementId: "smoke-research-place", x: 45, y: 80, width: 360, height: 250, order: 11, zIndex: 3 },
    { placementId: "place-frame-research", x: 730, y: 80 },
  ] });
  const moved = await native("get_workspace_context", { workspaceId: key });
  const placement = moved.document.placements.find(item => item.id === "smoke-research-place");
  equal(placement, { id: "smoke-research-place", componentId: "smoke-research", parentId: "place-frame-research", x: 45, y: 80, width: 360, height: 250, order: 11, zIndex: 3 });
  const beforeInvalid = await context();
  const invalid = { operationId: `${prefix}-invalid`, expectedRevision: revision, action: "move_components", actor,
    payload: { placements: [{ placementId: "smoke-research-place", x: 999 }, { placementId: "missing", x: 2 }] } };
  await api("POST", `/api/workspaces/${key}/commands`, invalid, 404);
  equal(await context(), beforeInvalid);
  await api("POST", `/api/workspaces/${key}/commands`, { ...invalid, expectedRevision: 0 }, 409);
  equal(await context(), beforeInvalid);
  const badId = { ...added.envelope, operationId: ` ${prefix}-metadata `, expectedRevision: revision };
  const badStdio = await client.call("create_workspace_component", badId);
  assert.equal(badStdio.error.data.status, 400);
  const badNative = await nativePacket("create_workspace_component", badId);
  assert.equal(badNative.result.isError, true);
  assert.match(badNative.result.content[0].text, /400/);
  equal(await context(), beforeInvalid);

  const edited = content + "\r\n  appended with whitespace  \r\n";
  await command("REST", null, "update_component", { componentId: "smoke-research", content: edited });
  assert.equal((await native("get_workspace_component", { workspaceId: key, componentId: "smoke-research" })).component.content, edited);
  await routeCommand("POST", "placements", { placement: { id: "smoke-extra-place", componentId: "smoke-research", x: 1500, y: 100 } });
  await routeCommand("DELETE", "placements/smoke-extra-place", {});
  assert.equal((await context()).document.components.find(item => item.id === "smoke-research").content, edited);
  await routeCommand("PATCH", "components/smoke-research", { title: "Evidence with full content" });
  await routeCommand("POST", "connectors", { connector: { id: "smoke-connector", fromComponentId: "smoke-research", toComponentId: "source-brief", label: "supports" } });
  await routeCommand("DELETE", "connectors/smoke-connector", {});
  const beforeMismatch = await context();
  await api("PATCH", `/api/workspaces/${key}/components/smoke-research`, {
    operationId: `${prefix}-mismatch`, expectedRevision: revision, componentId: "different-target", content: "must not replace", actor,
  }, 400);
  equal(await context(), beforeMismatch);
  const reservedId = "section/Ω?#%+";
  await command("native", "create_workspace_component", "create_component", {
    component: { id: reservedId, title: "Identifier transport parity", content: "Full reserved-ID text  \r\n" },
  });
  const reservedDetail = await api("GET", `/api/workspaces/${key}/component?${new URLSearchParams({ componentId: reservedId })}`);
  equal(await stdio("get_workspace_component", { workspaceId: key, componentId: reservedId }), reservedDetail);
  equal(await native("get_workspace_component", { workspaceId: key, componentId: reservedId }), reservedDetail);
  assert.equal(reservedDetail.component.id, reservedId);
  await command("stdio", "add_workspace_comment", "add_comment", { componentId: "smoke-research", text: "  Full comment Ω  \r\n" });
  await command("native", "save_workspace_checkpoint", "save_checkpoint", {
    clientId: "client-a", sessionId: prefix, objective: "  Continue A  ", focusedComponentIds: ["smoke-research"],
    completedChanges: ["Added research"], openQuestions: ["What remains?"], nextActions: ["Review plan"], lastAcknowledgedActivityId: (await context()).lastActivityId,
  });
  await command("stdio", "save_workspace_checkpoint", "save_checkpoint", {
    clientId: "client-b", sessionId: prefix, objective: "Independent B", nextActions: ["Keep both clients"],
  });
  const other = await stdio("create_workspace", { operationId: `${prefix}-other`, actor, title: `${prefix} idea B`, body: "Independent idea" });
  await native("get_workspace_context", { workspaceId: other.workspaceId });
  const fresh = await openWorkspaceStdio(base);
  try {
    const resumed = workspaceToolValue(await fresh.call("get_workspace_context", { workspaceId: key }));
    assert.equal(resumed.document.checkpoints.find(item => item.clientId === "client-a").objective, "  Continue A  ");
    assert.equal(resumed.document.checkpoints.find(item => item.clientId === "client-b").objective, "Independent B");
    assert.equal(resumed.document.components.find(item => item.id === "smoke-research").content, edited);
    assert.equal(resumed.originalSource.body, body);
  } finally { await fresh.close(); }
  await command("REST", null, "propose_plan", { title: "Reviewed draft", items: [
    { id: "deliver-first", title: "Build", description: "  exact scope  ", acceptanceCriteria: ["Verified"], dependsOn: [] },
    { id: "deliver-second", title: "Test", description: "Full test scope", acceptanceCriteria: ["Pass"], dependsOn: ["deliver-first"] },
  ] });
  const draft = (await context()).document.planRevisions.at(-1);
  await command("native", "accept_workspace_plan", "accept_plan", { planRevisionId: draft.id });
  assert.equal((await stdio("get_workspace_context", { workspaceId: key })).acceptedPlan.id, draft.id);

  const project = await api("POST", "/api/projects", { name: `${prefix} existing project`, description: "Description", goals: "Goals", readme: "# Full readme\n\nRetain this project document" }, 201);
  const opened = await native("create_workspace", { operationId: `${prefix}-project`, projectId: project.id, actor });
  assert.equal(opened.workspaceId, `project:${project.id}`);
  assert.equal(opened.createdIdea, false);
  const projectContext = await stdio("get_workspace_context", { workspaceId: opened.workspaceId });
  assert.equal(projectContext.originalSource.readme, "# Full readme\n\nRetain this project document");
  assert.equal(projectContext.kanbanUrl, `/project/${project.id}`);
  console.log(JSON.stringify({ result: "PASS", writeTools: workspaceWriteTools.length, commands, nativeTools: nativeTools.length, stdioTools: stdioTools.length,
    workspaceId: key, otherWorkspaceId: other.workspaceId, projectWorkspaceId: opened.workspaceId, fullSourceCharacters: body.length,
    fullComponentCharacters: edited.length, checks: ["all-three-transports", "capture-and-command-replay", "full-text", "atomic-layout", "strict-identifiers", "stale-conflicts", "independent-client-resume", "plan-proposal-acceptance", "existing-project-open"] }));
} finally { await client.close(); }
