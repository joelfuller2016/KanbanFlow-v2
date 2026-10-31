namespace KanbanFlow.Models.IdeaWorkspaces;

/// <summary>Durable canvas state, independent of source-row deletion and legacy workflow tables.</summary>
public sealed class IdeaWorkspaceState
{
    public required string WorkspaceId { get; set; }
    public required string Kind { get; set; }
    public int SourceId { get; set; }
    public int? LinkedProjectId { get; set; }
    public long Revision { get; set; }
    public required string OriginalSourceJson { get; set; }
    public required string DocumentJson { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>The result of one committed command, retained for truthful idempotent retries.</summary>
public sealed class IdeaWorkspaceReceipt
{
    public required string WorkspaceId { get; set; }
    public required string OperationId { get; set; }
    public required string RequestHash { get; set; }
    public required string ResultJson { get; set; }
    public required string Actor { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class IdeaWorkspaceDocument
{
    public int SchemaVersion { get; set; } = 1;
    public List<IdeaWorkspaceComponent> Components { get; set; } = [];
    public List<IdeaWorkspacePlacement> Placements { get; set; } = [];
    public List<IdeaWorkspaceConnector> Connectors { get; set; } = [];
    public List<IdeaWorkspaceComment> Comments { get; set; } = [];
    public List<IdeaWorkspaceCheckpoint> Checkpoints { get; set; } = [];
    public List<IdeaWorkspacePlanRevision> PlanRevisions { get; set; } = [];
    public string? AcceptedPlanRevisionId { get; set; }
}

public sealed class IdeaWorkspaceComponent
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "note";
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public string? Url { get; set; }
    public int? TaskId { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class IdeaWorkspacePlacement
{
    public string Id { get; set; } = "";
    public string ComponentId { get; set; } = "";
    /// <summary>Parent frame PLACEMENT ID. X/Y are relative to that appearance, or canvas-relative at the root.</summary>
    public string? ParentId { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 280;
    public double Height { get; set; } = 180;
    public int ZIndex { get; set; }
    public int Order { get; set; }
}

public sealed class IdeaWorkspaceConnector
{
    public string Id { get; set; } = "";
    public string FromComponentId { get; set; } = "";
    public string ToComponentId { get; set; } = "";
    public string Label { get; set; } = "related";
}

public sealed class IdeaWorkspaceComment
{
    public string Id { get; set; } = "";
    public string? ComponentId { get; set; }
    public string? ReplyToId { get; set; }
    public string Text { get; set; } = "";
    public string Actor { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public sealed class IdeaWorkspaceCheckpoint
{
    public string ClientId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string Objective { get; set; } = "";
    public List<string> FocusedComponentIds { get; set; } = [];
    public List<string> CompletedChanges { get; set; } = [];
    public List<string> OpenQuestions { get; set; } = [];
    public List<string> NextActions { get; set; } = [];
    public int LastAcknowledgedActivityId { get; set; }
    public string Actor { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
}

public sealed class IdeaWorkspacePlanRevision
{
    public string Id { get; set; } = "";
    public int Version { get; set; }
    public string Title { get; set; } = "";
    public string Status { get; set; } = "Draft";
    public List<IdeaWorkspacePlanItem> Items { get; set; } = [];
    public long? ExpectedProjectPlanRevision { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public string? AcceptedBy { get; set; }
    public DateTime? AcceptedAt { get; set; }
}

public sealed class IdeaWorkspacePlanItem
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> AcceptanceCriteria { get; set; } = [];
    public List<string> DependsOn { get; set; } = [];
    public int? PublishedTaskId { get; set; }
    public int? PublishedFeatureId { get; set; }
}
