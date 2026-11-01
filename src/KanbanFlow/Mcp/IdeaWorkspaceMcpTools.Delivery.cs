using System.ComponentModel;
using System.Text.Json;
using KanbanFlow.Services.IdeaWorkspaces;
using ModelContextProtocol.Server;

namespace KanbanFlow.Mcp;

public static partial class IdeaWorkspaceMcpTools
{
    [McpServerTool(Name = "link_workspace_project")]
    [Description("Associate an idea with an existing project, preserving complete documents and the project's primary workspace. Payload: projectId,expectedCurrentProjectId? for deliberate relinking. Both source linkage and snapshots save atomically; the idea keeps its workspace key.")]
    public static Task<IdeaWorkspaceMutationResult> LinkWorkspaceProject(IIdeaWorkspaceService service,
        string workspaceId, string operationId, long expectedRevision, JsonElement payload,
        string actor = "mcp-agent", CancellationToken cancellationToken = default)
        => Mutate(service, workspaceId, operationId, expectedRevision, "link_project", payload, actor, cancellationToken);

    [McpServerTool(Name = "create_workspace_project")]
    [Description("Create and associate one new project from an unlinked idea while preserving its full body/plan and workspace. Payload: name?; reuse the same operationId/envelope for a safe retry. A missing/deleted existing project is never silently recreated.")]
    public static Task<IdeaWorkspaceMutationResult> CreateWorkspaceProject(IIdeaWorkspaceService service,
        string workspaceId, string operationId, long expectedRevision, JsonElement payload,
        string actor = "mcp-agent", CancellationToken cancellationToken = default)
        => Mutate(service, workspaceId, operationId, expectedRevision, "create_project", payload, actor, cancellationToken);

    [McpServerTool(Name = "preview_workspace_publication")]
    [Description("Review the current accepted project plan's real Kanban publication without saving. Require current source and project-plan revisions. Optional itemIds are canonical IDs; predecessors must already be published or included. Preview retains complete descriptions/criteria and reports existing live tasks and feature choices.")]
    public static Task<IdeaWorkspacePublicationPreview> PreviewWorkspacePublication(IIdeaWorkspaceService service,
        string workspaceId, long expectedRevision, long expectedProjectPlanRevision,
        string[]? itemIds = null, int? featureId = null, string? featureName = null,
        CancellationToken cancellationToken = default)
        => Read(() => service.PreviewPublicationAsync(workspaceId,
            new IdeaWorkspacePublicationPreviewRequest(expectedRevision, expectedProjectPlanRevision,
                itemIds, featureId, featureName), cancellationToken));

    [McpServerTool(Name = "publish_workspace_plan")]
    [Description("Publish selected current accepted canonical plan items as real Kanban tasks in one audited transaction. Payload: expectedProjectPlanRevision,itemIds?,featureId?,featureName?. Preview first. Stable provenance prevents duplicates; existing mapped tasks and human completion remain intact. No GitHub changes are performed.")]
    public static Task<IdeaWorkspaceMutationResult> PublishWorkspacePlan(IIdeaWorkspaceService service,
        string workspaceId, string operationId, long expectedRevision, JsonElement payload,
        string actor = "mcp-agent", CancellationToken cancellationToken = default)
        => Mutate(service, workspaceId, operationId, expectedRevision, "publish_plan", payload, actor, cancellationToken);
}
