import assert from "node:assert/strict";
import { openWorkspaceStdio, workspaceNativePacket, workspaceToolValue } from "./workspace-transport.mjs";

const base = process.env.KANBANFLOW_API_URL;
assert.ok(base, "Set KANBANFLOW_API_URL to the explicit disposable workspace app.");
const prefix = `workspace-read-${Date.now()}`;
// The native SDK omits optional null properties; REST retains them. Compare the same domain values.
const domainValue = value => Array.isArray(value) ? value.map(domainValue)
  : value && typeof value === "object" ? Object.fromEntries(Object.entries(value).filter(([, item]) => item !== null).map(([key, item]) => [key, domainValue(item)])) : value;
const sameDomain = (actual, expected) => assert.deepEqual(domainValue(actual), domainValue(expected));
async function api(method, path, body, expected = 200) {
  const response = await fetch(base + path, {
    method, headers: { "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body), signal: AbortSignal.timeout(20000),
  });
  const text = await response.text();
  assert.equal(response.status, expected, `${method} ${path}: ${text}`);
  return text ? JSON.parse(text) : null;
}
const native = async (name, args = {}) => workspaceToolValue(await workspaceNativePacket(base, "tools/call", { name, arguments: args }));
const client = await openWorkspaceStdio(base);
const stdio = async (name, args = {}) => workspaceToolValue(await client.call(name, args));
try {
  const required = ["get_workspace_capabilities", "list_workspaces", "get_workspace_context", "list_workspace_components", "get_workspace_component", "get_workspace_changes"];
  const nativeTools = (await workspaceNativePacket(base, "tools/list", {})).result.tools;
  const stdioTools = (await client.rpc("tools/list", {})).result.tools;
  for (const name of required) {
    assert.ok(nativeTools.some(tool => tool.name === name), `Native discovery missing ${name}`);
    assert.ok(stdioTools.some(tool => tool.name === name), `Stdio discovery missing ${name}`);
  }
  const capabilities = await api("GET", "/api/workspace-capabilities");
  assert.deepEqual(await native("get_workspace_capabilities"), capabilities);
  assert.deepEqual(await stdio("get_workspace_capabilities"), capabilities);
  const source = "BEGIN\r\n    Full Ω research  \r\n" + "retain every source line  \r\n".repeat(5000) + "FINAL REQUIREMENT";
  const ideas = [];
  for (const status of ["New", "Triaging", "Archived", "Rejected"]) {
    const idea = await api("POST", "/api/ideas", { title: `${prefix} ${status}`, body: source, plan: "Plan-only search marker", tags: prefix }, 201);
    if (status !== "New") await api("PATCH", `/api/ideas/${idea.id}`, { status, expectedWorkflowVersion: idea.workflowVersion });
    ideas.push(idea);
  }
  const project = await api("POST", "/api/projects", { name: `${prefix} standalone project`, description: "Existing project with no idea" }, 201);
  const first = await api("GET", "/api/workspaces?limit=2");
  assert.ok(first.nextCursor, "The real mixed catalog must have a continuation.");
  const late = await api("POST", "/api/ideas", { title: `${prefix} later insert` }, 201);
  const items = [...first.items];
  let cursor = first.nextCursor;
  let pages = 1;
  while (cursor) {
    const page = await (pages % 2 ? native : stdio)("list_workspaces", { cursor });
    assert.equal(page.total, first.total);
    items.push(...page.items); cursor = page.nextCursor; pages++;
  }
  const keys = items.map(item => item.workspaceId);
  assert.equal(keys.length, first.total);
  assert.equal(new Set(keys).size, keys.length);
  for (const idea of ideas) assert.ok(keys.includes(`idea:${idea.id}`));
  assert.ok(keys.includes(`project:${project.id}`));
  assert.ok(!keys.includes(`idea:${late.id}`), "Insertion cannot change an already-started snapshot.");
  assert.ok((await stdio("list_workspaces", { q: `${prefix} later insert` })).items.some(item => item.workspaceId === `idea:${late.id}`));
  const key = `idea:${ideas[0].id}`;
  const context = await api("GET", `/api/workspaces/${key}/context`);
  sameDomain(await native("get_workspace_context", { workspaceId: key }), context);
  sameDomain(await stdio("get_workspace_context", { workspaceId: key }), context);
  assert.equal(context.source.body, source);
  const detail = await api("GET", `/api/workspaces/${key}/components/source-brief`);
  assert.equal(detail.component.content, source);
  sameDomain(await native("get_workspace_component", { workspaceId: key, componentId: "source-brief" }), detail);
  sameDomain(await stdio("get_workspace_component", { workspaceId: key, componentId: "source-brief" }), detail);
  const components = await stdio("list_workspace_components", { workspaceId: key, limit: 2 });
  assert.equal(components.items.length, 2);
  assert.ok(components.nextCursor);
  const remaining = await native("list_workspace_components", { workspaceId: key, cursor: components.nextCursor });
  assert.equal(remaining.total, components.total);
  const changes = await api("GET", `/api/workspaces/${key}/changes`);
  sameDomain(await native("get_workspace_changes", { workspaceId: key }), changes);
  sameDomain(await stdio("get_workspace_changes", { workspaceId: key }), changes);
  const projectContext = await stdio("get_workspace_context", { workspaceId: `project:${project.id}` });
  assert.equal(projectContext.kanbanUrl, `/project/${project.id}`);
  const badRest = await api("GET", "/api/workspaces?cursor=not-a-cursor", undefined, 400);
  const badNative = await workspaceNativePacket(base, "tools/call", { name: "list_workspaces", arguments: { cursor: "not-a-cursor" } });
  assert.equal(badNative.result.isError, true);
  assert.ok(badNative.result.content.some(item => item.text?.includes(badRest.code)));
  const badStdio = await client.call("list_workspaces", { cursor: "not-a-cursor" });
  assert.equal(badStdio.error.data.status, 400);
  assert.equal(badStdio.error.data.payload.code, badRest.code);
  console.log(JSON.stringify({ result: "PASS", readTools: required.length, nativeTools: nativeTools.length, stdioTools: stdioTools.length, snapshotPages: pages, catalogTotal: first.total, exactSourceCharacters: source.length, transports: ["REST", "native MCP", "stdio MCP"], fixturePrefix: prefix, workspaceId: key, projectWorkspaceId: `project:${project.id}` }));
} finally {
  await client.close();
  // Data stays only in the explicit disposable database as evidence for later write/restart checks.
}
