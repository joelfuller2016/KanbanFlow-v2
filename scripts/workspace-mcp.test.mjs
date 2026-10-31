import test from "node:test";
import assert from "node:assert/strict";
import { createServer } from "node:http";
import { once } from "node:events";
import { openWorkspaceStdio, workspaceToolValue } from "./workspace-transport.mjs";
import { workspaceReadTools } from "./workspace-mcp-tools.mjs";

// These fixtures verify the real wrapper/HTTP adapter, not production workspace persistence.
async function withAdapter(status, payload, run) {
  const requests = [];
  const server = createServer(async (request, response) => {
    requests.push({ method: request.method, url: request.url });
    response.writeHead(status, { "Content-Type": "application/json" });
    response.end(JSON.stringify(payload));
  });
  server.listen(0, "127.0.0.1"); await once(server, "listening");
  const client = await openWorkspaceStdio(`http://127.0.0.1:${server.address().port}`);
  try { await run(client, requests); }
  finally { await client.close(); await new Promise(resolve => server.close(resolve)); }
}

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

test("workspace component/context/change forwarding preserves complete authored content", async () => {
  const content = "\r\n    Ω evidence  \r\n".repeat(6000) + "FINAL REQUIREMENT  \r\n";
  await withAdapter(200, { content, revision: 11, nextCursor: null }, async (client, requests) => {
    for (const [name, args, path] of [
      ["get_workspace_context", { workspaceId: "idea:12" }, "/api/workspaces/idea%3A12/context"],
      ["get_workspace_component", { workspaceId: "idea:12", componentId: "research-a" }, "/api/workspaces/idea%3A12/components/research-a"],
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
