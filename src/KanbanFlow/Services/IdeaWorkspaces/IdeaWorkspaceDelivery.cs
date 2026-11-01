using System.Text.Json.Serialization;

namespace KanbanFlow.Services.IdeaWorkspaces;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record IdeaWorkspacePublicationPreviewRequest(
    [property: JsonRequired] long ExpectedRevision,
    [property: JsonRequired] long ExpectedProjectPlanRevision,
    IReadOnlyList<string>? ItemIds = null, int? FeatureId = null, string? FeatureName = null);

public sealed record IdeaWorkspaceFeatureOption(int Id, string Name);

public sealed record IdeaWorkspacePublicationPreviewItem(
    string CanonicalItemId, string SourceWorkspaceId, string SourceItemId,
    string Title, string Description, IReadOnlyList<string> AcceptanceCriteria,
    IReadOnlyList<string> DependsOn, bool WillCreate, IdeaWorkspaceTaskReference? ExistingTask);

public sealed record IdeaWorkspacePublicationPreview(
    string WorkspaceId, long Revision, int ProjectId, long ProjectPlanRevision,
    string PlanRevisionId, string PlanTitle, int? FeatureId, string FeatureName,
    IReadOnlyList<IdeaWorkspaceFeatureOption> Features,
    IReadOnlyList<IdeaWorkspacePublicationPreviewItem> Items,
    int NewTaskCount, int ExistingTaskCount);

public sealed record IdeaWorkspacePublishedItem(
    string CanonicalItemId, string SourceWorkspaceId, string SourceItemId,
    int TaskId, int FeatureId, bool Created);

public sealed record IdeaWorkspacePublicationResult(
    int ProjectId, int NewTaskCount, int ExistingTaskCount,
    IReadOnlyList<IdeaWorkspacePublishedItem> Items);
