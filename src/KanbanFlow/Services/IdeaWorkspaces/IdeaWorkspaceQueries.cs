using KanbanFlow.Models.IdeaWorkspaces;

namespace KanbanFlow.Services.IdeaWorkspaces;

public sealed record IdeaWorkspaceCapabilities(
    int ContractVersion, IReadOnlyList<string> ComponentTypes, IReadOnlyList<string> Actions,
    int DefaultPageSize, int MaximumPageSize, int SnapshotLifetimeSeconds,
    string PlacementParentSemantics, string AuthoredContentPolicy);

public sealed record IdeaWorkspacePage<T>(
    IReadOnlyList<T> Items, int Total, string? NextCursor, int Limit,
    long? SnapshotRevision = null, string? SnapshotId = null, DateTimeOffset? SnapshotExpiresAt = null);

/// <summary>Catalog metadata only; complete source/document content is obtained through context/detail reads.</summary>
public sealed record IdeaWorkspaceSummary(
    string WorkspaceId, string Kind, int SourceId, string Title, string? Status,
    string? Tags, int? ProjectId, bool IsLinked, long Revision, DateTime UpdatedAt,
    bool SourceMissing, string WorkspaceUrl, string? KanbanUrl);

/// <summary>Component metadata, deliberately omitting authored text rather than clipping it.</summary>
public sealed record IdeaWorkspaceComponentSummary(
    string Id, string Type, string Title, int? TaskId, int PlacementCount,
    int ContentLength, bool ContentIncluded = false);

public sealed record IdeaWorkspaceTaskReference(
    int Id, string Name, string? Description, string Status, string Priority,
    string? Assignee, int FeatureId, int ProjectId, DateTime UpdatedAt);

public sealed record IdeaWorkspaceComponentDetail(
    string WorkspaceId, long Revision, IdeaWorkspaceComponent Component,
    IReadOnlyList<IdeaWorkspacePlacement> Placements, IReadOnlyList<IdeaWorkspaceComment> Comments,
    IdeaWorkspaceTaskReference? Task, bool TaskMissing);

/// <summary>Complete before/after JSON remains available, including removed authored content.</summary>
public sealed record IdeaWorkspaceChange(
    int Id, string EventType, string? Actor, string? BeforeJson,
    string? AfterJson, string? Message, DateTime CreatedAt);

public sealed record IdeaWorkspaceChangePage(
    IReadOnlyList<IdeaWorkspaceChange> Items, int Total, string? NextCursor, int Limit,
    int Watermark, int AfterActivityId, long SnapshotRevision);
