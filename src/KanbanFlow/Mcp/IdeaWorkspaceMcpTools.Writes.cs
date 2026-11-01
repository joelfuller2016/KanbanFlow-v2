using System.ComponentModel;
using System.Text.Json;
using KanbanFlow.Services.IdeaWorkspaces;
using ModelContextProtocol.Server;

namespace KanbanFlow.Mcp;

public static partial class IdeaWorkspaceMcpTools
{
    [McpServerTool(Name = "create_workspace")]
    [Description("Capture a new idea workspace with exact original body/plan, or get/create the primary workspace for an existing projectId. Use either title/body/plan/tags/priority OR projectId. Reuse operationId for the same request to avoid duplicate ideas.")]
    public static Task<IdeaWorkspaceCreateResult> CreateWorkspace(IIdeaWorkspaceService service,
        string operationId, string? title = null, string? body = null, string? plan = null,
        string? tags = null, string? priority = null, int? projectId = null,
        string actor = "mcp-agent", CancellationToken cancellationToken = default)
        => Read(() => service.CreateAsync(new IdeaWorkspaceCreate(operationId, actor, title, body, plan,
            tags, priority, projectId), cancellationToken));

    [McpServerTool(Name = "workspace_command")]
    [Description("Apply one supported workspace action through the audited save boundary. First read capabilities/context. Reuse operationId for an identical retry; stale expectedRevision returns a conflict. Payload is a JSON object matching the discovered action fields.")]
    public static Task<IdeaWorkspaceMutationResult> WorkspaceCommand(IIdeaWorkspaceService service,
        string workspaceId, string operationId, long expectedRevision, string action, JsonElement payload,
        string actor = "mcp-agent", CancellationToken cancellationToken = default)
        => Mutate(service, workspaceId, operationId, expectedRevision, action, payload, actor, cancellationToken);

    [McpServerTool(Name = "create_workspace_component")]
    [Description("Add a supported typed component and optional independent placement. Payload: component {id?,type,title,content,url?,taskId?}, placement? {id?,parentId?,x,y,width,height,zIndex,order}. ParentId is a frame placement ID; child coordinates are relative.")]
    public static Task<IdeaWorkspaceMutationResult> CreateWorkspaceComponent(IIdeaWorkspaceService service,
        string workspaceId, string operationId, long expectedRevision, JsonElement payload,
        string actor = "mcp-agent", CancellationToken cancellationToken = default)
        => Mutate(service, workspaceId, operationId, expectedRevision, "create_component", payload, actor, cancellationToken);

    [McpServerTool(Name = "update_workspace_component")]
    [Description("Edit a component without replacing omitted fields. Payload contains componentId and explicit title/content/url/taskId changes. Authored text and whitespace are retained; unsupported fields fail.")]
    public static Task<IdeaWorkspaceMutationResult> UpdateWorkspaceComponent(IIdeaWorkspaceService service,
        string workspaceId, string operationId, long expectedRevision, JsonElement payload,
        string actor = "mcp-agent", CancellationToken cancellationToken = default)
        => Mutate(service, workspaceId, operationId, expectedRevision, "update_component", payload, actor, cancellationToken);

    [McpServerTool(Name = "remove_workspace_component")]
    [Description("Explicitly remove underlying component content and its appearances; complete before content remains in history. Payload: componentId. Prefer remove_component_placement to remove only an appearance.")]
    public static Task<IdeaWorkspaceMutationResult> RemoveWorkspaceComponent(IIdeaWorkspaceService service,
        string workspaceId, string operationId, long expectedRevision, JsonElement payload,
        string actor = "mcp-agent", CancellationToken cancellationToken = default)
        => Mutate(service, workspaceId, operationId, expectedRevision, "remove_component", payload, actor, cancellationToken);

    [McpServerTool(Name = "add_workspace_placement")]
    [Description("Show an existing component in another place without copying its content. Payload: placement {id?,componentId,parentId?,x,y,width,height,zIndex,order}.")]
    public static Task<IdeaWorkspaceMutationResult> AddWorkspacePlacement(IIdeaWorkspaceService service,
        string workspaceId, string operationId, long expectedRevision, JsonElement payload,
        string actor = "mcp-agent", CancellationToken cancellationToken = default)
        => Mutate(service, workspaceId, operationId, expectedRevision, "add_placement", payload, actor, cancellationToken);

    [McpServerTool(Name = "remove_component_placement")]
    [Description("Remove one appearance while retaining the underlying component and discussion. Payload: placementId. A parent with children cannot disappear silently.")]
    public static Task<IdeaWorkspaceMutationResult> RemoveComponentPlacement(IIdeaWorkspaceService service,
        string workspaceId, string operationId, long expectedRevision, JsonElement payload,
        string actor = "mcp-agent", CancellationToken cancellationToken = default)
        => Mutate(service, workspaceId, operationId, expectedRevision, "remove_placement", payload, actor, cancellationToken);

    [McpServerTool(Name = "move_workspace_components")]
    [Description("Atomically move, resize, reorder or group placements. Payload: placements [{placementId,x?,y?,width?,height?,parentId?,zIndex?,order?}]. Invalid members/cycles/stale versions roll back the entire batch.")]
    public static Task<IdeaWorkspaceMutationResult> MoveWorkspaceComponents(IIdeaWorkspaceService service,
        string workspaceId, string operationId, long expectedRevision, JsonElement payload,
        string actor = "mcp-agent", CancellationToken cancellationToken = default)
        => Mutate(service, workspaceId, operationId, expectedRevision, "move_components", payload, actor, cancellationToken);

    [McpServerTool(Name = "add_workspace_connector")]
    [Description("Connect two components without changing task dependencies. Payload: connector {fromComponentId,toComponentId,label,id?}. Labels: related, supports, answers, depends-on, contradicts, leads-to.")]
    public static Task<IdeaWorkspaceMutationResult> AddWorkspaceConnector(IIdeaWorkspaceService service,
        string workspaceId, string operationId, long expectedRevision, JsonElement payload,
        string actor = "mcp-agent", CancellationToken cancellationToken = default)
        => Mutate(service, workspaceId, operationId, expectedRevision, "create_connector", payload, actor, cancellationToken);

    [McpServerTool(Name = "remove_workspace_connector")]
    [Description("Remove an explicit canvas connector. Payload: connectorId. This does not delete components or task dependencies.")]
    public static Task<IdeaWorkspaceMutationResult> RemoveWorkspaceConnector(IIdeaWorkspaceService service,
        string workspaceId, string operationId, long expectedRevision, JsonElement payload,
        string actor = "mcp-agent", CancellationToken cancellationToken = default)
        => Mutate(service, workspaceId, operationId, expectedRevision, "remove_connector", payload, actor, cancellationToken);

    [McpServerTool(Name = "add_workspace_comment")]
    [Description("Append full attributed discussion to a workspace/component. Payload: text, componentId?, replyToId?. Replies retain their original thread anchor; the server assigns comment identity.")]
    public static Task<IdeaWorkspaceMutationResult> AddWorkspaceComment(IIdeaWorkspaceService service,
        string workspaceId, string operationId, long expectedRevision, JsonElement payload,
        string actor = "mcp-agent", CancellationToken cancellationToken = default)
        => Mutate(service, workspaceId, operationId, expectedRevision, "add_comment", payload, actor, cancellationToken);

    [McpServerTool(Name = "save_workspace_checkpoint")]
    [Description("Save this client's session objective, focus, completed changes, questions, next actions and last acknowledged activity position. Payload: clientId,sessionId,objective,focusedComponentIds[],completedChanges[],openQuestions[],nextActions[],lastAcknowledgedActivityId. Other clients/workspaces retain independent checkpoints.")]
    public static Task<IdeaWorkspaceMutationResult> SaveWorkspaceCheckpoint(IIdeaWorkspaceService service,
        string workspaceId, string operationId, long expectedRevision, JsonElement payload,
        string actor = "mcp-agent", CancellationToken cancellationToken = default)
        => Mutate(service, workspaceId, operationId, expectedRevision, "save_checkpoint", payload, actor, cancellationToken);

    [McpServerTool(Name = "propose_workspace_plan")]
    [Description("Save a draft plan revision without changing accepted work. Payload: title,items[{id?,title,description,acceptanceCriteria[],dependsOn[]}]. Preserve stable item IDs in later revisions; the server assigns revision identity.")]
    public static Task<IdeaWorkspaceMutationResult> ProposeWorkspacePlan(IIdeaWorkspaceService service,
        string workspaceId, string operationId, long expectedRevision, JsonElement payload,
        string actor = "mcp-agent", CancellationToken cancellationToken = default)
        => Mutate(service, workspaceId, operationId, expectedRevision, "propose_plan", payload, actor, cancellationToken);

    [McpServerTool(Name = "accept_workspace_plan")]
    [Description("Accept a reviewed plan revision within existing authorization. Payload: planRevisionId,expectedProjectPlanRevision? for a shared project. Publication is separate; acceptance does not create GitHub issues or tasks.")]
    public static Task<IdeaWorkspaceMutationResult> AcceptWorkspacePlan(IIdeaWorkspaceService service,
        string workspaceId, string operationId, long expectedRevision, JsonElement payload,
        string actor = "mcp-agent", CancellationToken cancellationToken = default)
        => Mutate(service, workspaceId, operationId, expectedRevision, "accept_plan", payload, actor, cancellationToken);

    private static Task<IdeaWorkspaceMutationResult> Mutate(IIdeaWorkspaceService service,
        string workspaceId, string operationId, long expectedRevision, string action, JsonElement payload,
        string actor, CancellationToken cancellationToken)
        => Read(() => service.ExecuteAsync(workspaceId,
            new IdeaWorkspaceCommand(operationId, expectedRevision, action, payload, actor), cancellationToken));
}
