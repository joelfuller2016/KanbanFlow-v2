import assert from "node:assert/strict";
import { openWorkspaceStdio, workspaceNativePacket, workspaceToolValue } from "./workspace-transport.mjs";

const base = process.env.KANBANFLOW_API_URL;
const workspaceId = process.env.KANBANFLOW_WORKSPACE_SMOKE_ID;
assert.ok(base && workspaceId, "Set an explicit disposable API URL and a workspace ID from workspace-write-smoke.mjs before restarting the app.");
const native = async (name, args) => workspaceToolValue(await workspaceNativePacket(base, "tools/call", { name, arguments: args }));
const client = await openWorkspaceStdio(base);
try {
  const context = workspaceToolValue(await client.call("get_workspace_context", { workspaceId }));
  const original = context.originalSource;
  const prefix = original.title.replace(/ idea A$/, "");
  assert.ok(prefix.startsWith("workspace-write-"), "Only labeled disposable smoke data is accepted.");
  const content = "\r\n    Full editable content Ω  \r\n".repeat(4000) + "FINAL EDIT REQUIREMENT  \r\n";
  const edited = content + "\r\n  appended with whitespace  \r\n";
  assert.equal(context.document.components.find(item => item.id === "smoke-research").content, edited);
  assert.equal(context.document.checkpoints.find(item => item.clientId === "client-a").objective, "  Continue A  ");
  assert.equal(context.document.checkpoints.find(item => item.clientId === "client-b").objective, "Independent B");
  assert.ok(context.acceptedPlan);
  const captureReplay = await native("create_workspace", { operationId: `${prefix}-capture`, actor: "workspace-smoke", title: original.title, body: original.body, plan: original.plan });
  assert.equal(captureReplay.workspaceId, workspaceId);
  assert.equal(captureReplay.replayed, true);
  assert.equal(captureReplay.revision, 1);
  const commandReplay = workspaceToolValue(await client.call("create_workspace_component", { workspaceId,
    operationId: `${prefix}-1`, expectedRevision: 1, actor: "workspace-smoke", payload: {
      component: { id: "smoke-research", type: "research", title: "Evidence", content, url: "https://example.com/research" },
      placement: { id: "smoke-research-place", parentId: "place-frame-research", x: 20, y: 60, width: 330, height: 220 },
    } }));
  assert.equal(commandReplay.replayed, true);
  assert.equal(commandReplay.revision, 2);
  assert.equal((await native("get_workspace_context", { workspaceId })).revision, context.revision);
  console.log(JSON.stringify({ result: "PASS", workspaceId, revision: context.revision,
    fullComponentCharacters: edited.length, checks: ["persisted-edits", "two-client-checkpoints", "accepted-plan", "capture-receipt", "command-receipt", "no-replay-overwrite"] }));
} finally { await client.close(); }
