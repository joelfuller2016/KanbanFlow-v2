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
