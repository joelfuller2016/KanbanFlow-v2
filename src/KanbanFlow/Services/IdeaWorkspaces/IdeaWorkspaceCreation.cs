using System.Text.Json.Serialization;

namespace KanbanFlow.Services.IdeaWorkspaces;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record IdeaWorkspaceCreate(
    [property: JsonRequired] string OperationId,
    [property: JsonRequired] string Actor,
    string? Title = null, string? Body = null, string? Plan = null,
    string? Tags = null, string? Priority = null, int? ProjectId = null);

public sealed record IdeaWorkspaceCreateResult(
    string WorkspaceId, long Revision, string OperationId,
    int? IdeaId, int? ProjectId, int ActivityId,
    bool CreatedWorkspace, bool CreatedIdea, bool Replayed = false);
