// Additive workspace tool metadata; dispatch stays in mcp-server so existing protocol/error handling is shared.
const workspaceId = { type: "string", description: "Stable key returned by list_workspaces, such as idea:12 or project:8." };
const cursor = { type: "string", description: "Opaque nextCursor from the preceding page. Keep its filters unchanged; restart explicitly if it expires." };
const limit = { type: "integer", minimum: 1, maximum: 500, description: "Bounded page size; follow nextCursor until null to retrieve the complete result." };
const tool = (name, description, properties = {}, required = []) => ({
  name, description,
  inputSchema: { type: "object", properties, required, additionalProperties: false },
});

export const workspaceReadTools = [
  tool("get_workspace_capabilities", "Discover the workspace contract, supported component types, operations and paging limits before composing a workspace."),
  tool("list_workspaces", "Discover every idea and project workspace, including standalone projects. Results are summaries; follow every nextCursor for complete snapshot traversal.", {
    kind: { type: "string", enum: ["all", "idea", "project", "linked"], description: "Workspace kind filter; all includes both ideas and projects." },
    status: { type: "string", description: "Optional idea triage status filter. Archived/rejected remain discoverable when unfiltered." },
    q: { type: "string", description: "Search source and developed workspace/plan content." },
    cursor, limit,
  }),
  tool("get_workspace_context", "Resume a workspace with complete original/current source, revision, saved client checkpoints, plan context and Kanban links. Use component pages/detail for individual canvas content.", { workspaceId }, ["workspaceId"]),
  tool("list_workspace_components", "List a complete bounded page of workspace component summaries and placements. Follow nextCursor; use get_workspace_component for full authored content.", { workspaceId, cursor, limit }, ["workspaceId"]),
  tool("get_workspace_component", "Read one workspace component's full content and placements; task references resolve current real task status rather than a saved copy.", {
    workspaceId, componentId: { type: "string", description: "Component id from a component page or workspace context." },
  }, ["workspaceId", "componentId"]),
  tool("get_workspace_changes", "Read complete ascending workspace activity after a saved checkpoint. Follow nextCursor to its watermark; all authored before/after details are retained.", {
    workspaceId, cursor, limit,
    afterActivityId: { type: "integer", minimum: 0, description: "Last acknowledged activity position; default zero. Keep unchanged while following a cursor." },
  }, ["workspaceId"]),
];

export const workspaceWriteActions = Object.freeze({
  create_workspace_component: "create_component",
  update_workspace_component: "update_component",
  remove_workspace_component: "remove_component",
  add_workspace_placement: "add_placement",
  remove_component_placement: "remove_placement",
  move_workspace_components: "move_components",
  add_workspace_connector: "create_connector",
  remove_workspace_connector: "remove_connector",
  add_workspace_comment: "add_comment",
  save_workspace_checkpoint: "save_checkpoint",
  propose_workspace_plan: "propose_plan",
  accept_workspace_plan: "accept_plan",
});
const writeProperties = {
  workspaceId,
  operationId: { type: "string", description: "Unique operation identity. Reuse it with identical content to retry safely." },
  expectedRevision: { type: "integer", minimum: 0, description: "Current workspace revision from get_workspace_context; stale writes conflict." },
  payload: { type: "object", description: "Action payload matching get_workspace_capabilities descriptors. Complete authored strings are retained; unknown fields fail." },
  actor: { type: "string", description: "Reported actor label, default mcp-agent; the existing local trust model is unchanged." },
};
export const workspaceWriteTools = [
  tool("create_workspace", "Capture a new idea with full original text, or get/create the primary workspace of an existing projectId without making a dummy idea. Reuse operationId for the same request.", {
    operationId: writeProperties.operationId, actor: writeProperties.actor,
    title: { type: "string", description: "New idea title; omit when selecting an existing projectId." },
    body: { type: ["string", "null"], description: "Complete authored source; whitespace is preserved." },
    plan: { type: ["string", "null"], description: "Complete initial plan; whitespace is preserved." },
    tags: { type: ["string", "null"] },
    priority: { type: "string", enum: ["Low", "Medium", "High", "Critical"] },
    projectId: { type: "integer", minimum: 1, description: "Existing project source; omit title/body/plan/tags/priority in this mode." },
  }, ["operationId"]),
  tool("workspace_command", "Apply one discovered workspace action through the same audited save boundary as the UI/API. Identical retries return the committed receipt.", {
    ...writeProperties, action: { type: "string", enum: Object.values(workspaceWriteActions) },
  }, ["workspaceId", "operationId", "expectedRevision", "action", "payload"]),
  ...Object.entries(workspaceWriteActions).map(([name, action]) => tool(name,
    `Apply ${action} using the capability-described payload. Saves atomically with audit and operation receipt; stale revisions or unsupported properties fail explicitly.`,
    writeProperties, ["workspaceId", "operationId", "expectedRevision", "payload"])),
];
