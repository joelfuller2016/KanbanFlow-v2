using System.Text.Json;
using KanbanFlow.Models.IdeaWorkspaces;

namespace KanbanFlow.Services.IdeaWorkspaces;

public interface IIdeaWorkspaceService
{
    Task<IdeaWorkspaceContext> GetAsync(string workspaceId, CancellationToken ct = default);
    Task<IdeaWorkspaceMutationResult> ExecuteAsync(string workspaceId, IdeaWorkspaceCommand command, CancellationToken ct = default);
}

public sealed record IdeaWorkspaceCommand(
    string OperationId, long ExpectedRevision, string Action, JsonElement Payload, string Actor);

public sealed record IdeaWorkspaceMutationResult(
    string WorkspaceId, long Revision, string OperationId, string Action,
    string? ComponentId, string? PlacementId, int ActivityId, bool Replayed = false);

public sealed record IdeaWorkspaceSource(
    string Kind, int Id, string Title, string? Body, string? Plan,
    string? Readme, string? Status, string? Tags, string? Priority, int? ProjectId,
    DateTime CreatedAt, DateTime UpdatedAt);

public sealed record IdeaWorkspaceContext(
    string WorkspaceId, string Kind, int SourceId, string Title, long Revision,
    IdeaWorkspaceSource? Source, IdeaWorkspaceSource OriginalSource,
    bool SourceMissing, int? ProjectId, bool ProjectMissing,
    string WorkspaceUrl, string? KanbanUrl, string? AcceptedPlanWorkspaceId,
    DateTime UpdatedAt, int LastActivityId, IdeaWorkspaceDocument Document);

public sealed class IdeaWorkspaceException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

public readonly record struct IdeaWorkspaceKey(string Kind, int SourceId)
{
    public string WorkspaceId => $"{Kind}:{SourceId}";
    public string AuditEntityType => Kind == "idea" ? "IdeaWorkspace" : "ProjectWorkspace";

    public static IdeaWorkspaceKey Parse(string workspaceId)
    {
        var parts = workspaceId?.Split(':');
        if (parts is not { Length: 2 } || parts[0] is not ("idea" or "project") ||
            !int.TryParse(parts[1], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var id) || id <= 0 ||
            parts[1] != id.ToString(System.Globalization.CultureInfo.InvariantCulture))
            throw new IdeaWorkspaceException(400, "invalid_workspace", "Workspace must be idea:{positive-id} or project:{positive-id}.");
        return new IdeaWorkspaceKey(parts[0], id);
    }
}
