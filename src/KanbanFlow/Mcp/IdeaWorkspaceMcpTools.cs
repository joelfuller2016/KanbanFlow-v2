using System.ComponentModel;
using KanbanFlow.Services.IdeaWorkspaces;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace KanbanFlow.Mcp;

/// <summary>Idea/project canvas tools over the same durable services as REST and the human interface.</summary>
[McpServerToolType]
public static partial class IdeaWorkspaceMcpTools
{
    [McpServerTool(Name = "get_workspace_capabilities")]
    [Description("Discover the workspace contract, supported component types, operations and paging limits before composing a workspace.")]
    public static IdeaWorkspaceCapabilities GetWorkspaceCapabilities(IIdeaWorkspaceService service)
        => service.GetCapabilities();

    [McpServerTool(Name = "list_workspaces")]
    [Description("Discover every idea and project workspace, including standalone projects and archived ideas. Results are summaries; follow nextCursor until null for a complete stable traversal.")]
    public static Task<IdeaWorkspacePage<IdeaWorkspaceSummary>> ListWorkspaces(
        IIdeaWorkspaceService service,
        [Description("Optional kind: all, idea, project or linked.")] string? kind = null,
        [Description("Optional idea status: New, Triaging, Promoted, Archived or Rejected.")] string? status = null,
        [Description("Search full source and developed workspace/plan content.")] string? q = null,
        [Description("Opaque continuation cursor; keep filters and explicit page size unchanged.")] string? cursor = null,
        [Description("Page size, 1 through 500; default 50. Follow nextCursor for all results.")] int? limit = null,
        CancellationToken cancellationToken = default)
        => Read(() => service.ListAsync(kind, status, q, cursor, limit, cancellationToken));

    [McpServerTool(Name = "get_workspace_context")]
    [Description("Resume an idea/project workspace with original and current source documents, revision, saved client checkpoints, plan context and Kanban links. All authored content is retained.")]
    public static Task<IdeaWorkspaceContext> GetWorkspaceContext(
        IIdeaWorkspaceService service,
        [Description("Stable key from list_workspaces, such as idea:12 or project:8.")] string workspaceId,
        CancellationToken cancellationToken = default)
        => Read(() => service.GetAsync(workspaceId, cancellationToken));

    [McpServerTool(Name = "list_workspace_components")]
    [Description("Read a complete bounded page of component summaries and placements. Follow nextCursor; get_workspace_component returns full authored content.")]
    public static Task<IdeaWorkspacePage<IdeaWorkspaceComponentSummary>> ListWorkspaceComponents(
        IIdeaWorkspaceService service,
        [Description("Stable idea/project workspace key.")] string workspaceId,
        [Description("Opaque continuation cursor from the preceding page.")] string? cursor = null,
        [Description("Page size 1 through 500; keep unchanged while continuing.")] int? limit = null,
        CancellationToken cancellationToken = default)
        => Read(() => service.ListComponentsAsync(workspaceId, cursor, limit, cancellationToken));

    [McpServerTool(Name = "get_workspace_component")]
    [Description("Read one component's complete content and placements. A task reference resolves the current real task state and reports removed targets explicitly.")]
    public static Task<IdeaWorkspaceComponentDetail> GetWorkspaceComponent(
        IIdeaWorkspaceService service,
        [Description("Stable idea/project workspace key.")] string workspaceId,
        [Description("Component ID from a component page or saved context.")] string componentId,
        CancellationToken cancellationToken = default)
        => Read(() => service.GetComponentAsync(workspaceId, componentId, cancellationToken));

    [McpServerTool(Name = "get_workspace_changes")]
    [Description("Read complete ascending workspace activity after a saved checkpoint. Follow nextCursor to its watermark; full authored before/after details are retained.")]
    public static Task<IdeaWorkspaceChangePage> GetWorkspaceChanges(
        IIdeaWorkspaceService service,
        [Description("Stable idea/project workspace key.")] string workspaceId,
        [Description("Opaque continuation cursor, preserving its initial afterActivityId and page size.")] string? cursor = null,
        [Description("Last acknowledged activity position; default zero.")] int? afterActivityId = null,
        [Description("Page size 1 through 500.")] int? limit = null,
        CancellationToken cancellationToken = default)
        => Read(() => service.GetChangesAsync(workspaceId, cursor, afterActivityId, limit, cancellationToken));

    private static async Task<T> Read<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (IdeaWorkspaceException ex)
        {
            // Native tools use MCP's error envelope; preserve the same actionable domain code/status as REST.
            throw new McpException($"{ex.Code} ({ex.StatusCode}): {ex.Message}");
        }
    }
}
