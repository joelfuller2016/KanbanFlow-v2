import test from "node:test";
import assert from "node:assert/strict";
import { createServer } from "node:http";
import { once } from "node:events";
import { existsSync, mkdirSync, mkdtempSync, readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { openWorkspaceStdio, workspaceToolValue } from "./workspace-transport.mjs";
import { workspaceReadTools, workspaceWriteTools } from "./workspace-mcp-tools.mjs";

// These fixtures verify the real wrapper/HTTP adapter, not production workspace persistence.
async function withAdapter(status, payload, run, options) {
  const requests = [];
  const server = createServer(async (request, response) => {
    let body = "";
    for await (const chunk of request) body += chunk;
    requests.push({ method: request.method, url: request.url, body: body ? JSON.parse(body) : undefined });
    response.writeHead(status, { "Content-Type": "application/json" });
    response.end(JSON.stringify(payload));
  });
  server.listen(0, "127.0.0.1"); await once(server, "listening");
  const client = await openWorkspaceStdio(`http://127.0.0.1:${server.address().port}`, options);
  try { await run(client, requests); }
  finally { await client.close(); await new Promise(resolve => server.close(resolve)); }
}

test("rejected workspace arguments are recorded once without logging their authored payload", async () => {
  const scratch = fileURLToPath(new URL("../_sandbox/579/", import.meta.url));
  mkdirSync(scratch, { recursive: true });
  const logPath = path.join(mkdtempSync(path.join(scratch, "usage-rejection-")), "usage.jsonl");
  const privateMarker = "PRIVATE-REJECTED-WORKSPACE-BODY";
  await withAdapter(200, {}, async (client, requests) => {
    const packet = await client.call("create_workspace", {
      operationId: "usage-rejection", title: "No source should be saved", actor: "usage-regression",
      unsupportedReadme: privateMarker,
    });
    assert.equal(packet.error.code, -32602);
    assert.equal(requests.length, 0, "rejected fields must not reach the HTTP API");
    const text = existsSync(logPath) ? readFileSync(logPath, "utf8") : "";
    const rows = text.split("\n").filter(Boolean).map(line => JSON.parse(line));
    assert.equal(rows.length, 1, "the actual wrapper must record this rejected tools/call");
    assert.equal(rows[0].tool, "create_workspace");
    assert.equal(rows[0].outcome, "error");
    assert.equal(rows[0].code, -32602);
    assert.equal(rows[0].actor, "usage-regression");
    for (const value of [privateMarker, "unsupportedReadme", "usage-rejection", "No source should be saved"])
      assert.equal(text.includes(value), false, "only allowlisted usage metadata may be logged");
  }, { usageLog: logPath });
});

test("workspace read tools are discoverable through actual stdio with complete paging metadata", async () => {
  await withAdapter(200, { items: [], total: 150, nextCursor: "next-page" }, async (client, requests) => {
    const tools = (await client.rpc("tools/list", {})).result.tools;
    for (const expected of workspaceReadTools) assert.ok(tools.some(tool => tool.name === expected.name));
    assert.deepEqual(workspaceToolValue(await client.call("list_workspaces", { kind: "linked", q: "plan Ω", limit: 17, cursor: "snapshot/next+" })), { items: [], total: 150, nextCursor: "next-page" });
    const url = new URL(requests[0].url, "http://fixture");
    assert.equal(url.pathname, "/api/workspaces");
    assert.equal(url.searchParams.get("kind"), "linked");
    assert.equal(url.searchParams.get("q"), "plan Ω");
    assert.equal(url.searchParams.get("cursor"), "snapshot/next+");
    assert.equal(url.searchParams.get("limit"), "17");
  });
});

test("workspace write tools are discoverable and preserve complete mutation payloads and revisions", async () => {
  const content = "\r\n    exact content Ω  \r\n".repeat(3000);
  const payload = { component: { id: "note-a", type: "markdown", title: "Source", content }, placement: { id: "place-a", x: 10, y: 20 } };
  await withAdapter(200, { workspaceId: "idea:12", revision: 8, operationId: "op-a", componentId: "note-a" }, async (client, requests) => {
    const tools = (await client.rpc("tools/list", {})).result.tools;
    for (const expected of workspaceWriteTools) assert.ok(tools.some(tool => tool.name === expected.name));
    const result = workspaceToolValue(await client.call("create_workspace_component", { workspaceId: "idea:12", operationId: "op-a", expectedRevision: 7, payload, actor: "test-writer" }));
    assert.equal(result.revision, 8);
    assert.deepEqual(requests[0], { method: "POST", url: "/api/workspaces/idea%3A12/commands", body: {
      operationId: "op-a", expectedRevision: 7, action: "create_component", payload, actor: "test-writer",
    } });
  });
});

test("workspace capture preserves empty and complete authored text with an explicit actor", async () => {
  const body = "\r\n    capture original Ω  \r\n".repeat(2000);
  await withAdapter(200, { workspaceId: "idea:13", revision: 1 }, async (client, requests) => {
    await client.call("create_workspace", { operationId: "capture", title: "A new idea", body, plan: "", actor: "capture-client" });
    assert.deepEqual(requests[0], { method: "POST", url: "/api/workspaces", body: { operationId: "capture", title: "A new idea", body, plan: "", actor: "capture-client" } });
  });
});

test("workspace publication preview forwards exact revision and subset fields without a write actor", async () => {
  const description = "\r\n    full publication preview Ω  \r\n".repeat(3000);
  await withAdapter(200, { projectPlanRevision: 9, items: [{ canonicalItemId: "legacy/item%2F+", description, willCreate: true }] }, async (client, requests) => {
    const result = workspaceToolValue(await client.call("preview_workspace_publication", {
      workspaceId: "idea:12", expectedRevision: 7, expectedProjectPlanRevision: 9,
      itemIds: ["legacy/item%2F+"], featureName: "  New feature Ω  ",
    }));
    assert.equal(result.items[0].description, description);
    assert.deepEqual(requests[0], { method: "POST", url: "/api/workspaces/idea%3A12/publication-preview", body: {
      expectedRevision: 7, expectedProjectPlanRevision: 9, itemIds: ["legacy/item%2F+"], featureName: "  New feature Ω  ",
    } });
  });
});

test("workspace tools refuse unknown top-level fields before any HTTP write", async () => {
  await withAdapter(200, {}, async (client, requests) => {
    for (const [name, args] of [
      ["create_workspace", { operationId: "capture", title: "Idea", unsupportedReadme: "must not disappear" }],
      ["create_workspace_component", { workspaceId: "idea:1", operationId: "add", expectedRevision: 0, payload: {}, content: "outside payload" }],
      ["get_workspace_context", { workspaceId: "idea:1", unsupportedFilter: true }],
    ]) {
      const packet = await client.call(name, args);
      assert.equal(packet.error.code, -32602);
      assert.match(packet.error.message, /Unsupported workspace argument/);
    }
    assert.equal(requests.length, 0);
  });
});

test("workspace component/context/change forwarding preserves complete authored content", async () => {
  const content = "\r\n    Ω evidence  \r\n".repeat(6000) + "FINAL REQUIREMENT  \r\n";
  await withAdapter(200, { content, revision: 11, nextCursor: null }, async (client, requests) => {
    for (const [name, args, path] of [
      ["get_workspace_context", { workspaceId: "idea:12" }, "/api/workspaces/idea%3A12/context"],
      ["get_workspace_component", { workspaceId: "idea:12", componentId: "research-a" }, "/api/workspaces/idea%3A12/component"],
      ["list_workspace_components", { workspaceId: "project:8", limit: 2 }, "/api/workspaces/project%3A8/components"],
      ["get_workspace_changes", { workspaceId: "idea:12", afterActivityId: 900, limit: 50 }, "/api/workspaces/idea%3A12/changes"],
    ]) {
      assert.equal(workspaceToolValue(await client.call(name, args)).content, content);
      assert.equal(requests.at(-1).url.split("?")[0], path);
    }
    const query = new URL(requests.at(-1).url, "http://fixture").searchParams;
    assert.equal(query.get("afterActivityId"), "900");
  });
});

test("workspace component query addressing preserves slashes Unicode percent and query delimiters", async () => {
  const componentId = "section/Ω?#%+";
  await withAdapter(200, { component: { id: componentId, content: "Full source" } }, async (client, requests) => {
    const result = workspaceToolValue(await client.call("get_workspace_component", { workspaceId: "idea:12", componentId }));
    assert.equal(result.component.id, componentId);
    const address = new URL(requests[0].url, "http://fixture");
    assert.equal(address.pathname, "/api/workspaces/idea%3A12/component");
    assert.equal(address.searchParams.get("componentId"), componentId);
  });
});

for (const status of [400, 404, 409]) {
  test(`workspace stdio retains HTTP ${status} and the complete domain error`, async () => {
    const payload = { code: "workspace_conflict", message: "Reload the current revision.", detail: "full domain detail" };
    await withAdapter(status, payload, async client => {
      const packet = await client.call("get_workspace_context", { workspaceId: "idea:12" });
      assert.equal(packet.error.code, -32000);
      assert.equal(packet.error.data.status, status);
      assert.deepEqual(packet.error.data.payload, payload);
    });
  });
}
