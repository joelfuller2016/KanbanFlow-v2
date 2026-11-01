namespace KanbanFlow.Models.Workflows;

// Transport DTOs intentionally have no EF navigation properties or cycles.
public sealed record WorkflowContext(Guid Id, int IdeaId, int? ProjectId, int? FeatureId, string Title,
    string IdeaStatus, int Version, bool Archived, bool NeedsBriefReview, string IdeaUrl, string? BoardUrl,
    IReadOnlyList<MessageContext> Messages, IReadOnlyList<BriefContext> Briefs,
    IReadOnlyList<PlanContext> Plans, IReadOnlyList<WorkflowTaskContext> Tasks,
    IReadOnlyList<ResultContext> Results, IReadOnlyList<RepositoryContext> Repositories);
public sealed record MessageContext(Guid Id, string Kind, string Text, string Author, string Source, DateTime CreatedAt);
public sealed record BriefContext(Guid Id, int Number, string Outcome, string Scope, string Constraints, string Acceptance, string Author, DateTime CreatedAt);
public sealed record PlanContext(Guid Id, int Number, Guid BriefRevisionId, TaskDraft[] Tasks, DateTime CreatedAt, DateTime? PublishedAt);
public sealed record WorkflowTaskContext(Guid Id, Guid DraftId, int TaskId, int FeatureId, string Name, string? Description,
    string Status, string Priority, string? Assignee, string? ClaimedBy, string Url,
    int SpecificationVersion, TaskDraft Specification, IReadOnlyList<WorkflowCheck> AcceptanceCriteria,
    IReadOnlyList<WorkflowCheck> Subtasks, IReadOnlyList<int> DependsOnTaskIds,
    IReadOnlyList<WorkflowComment> Comments, IReadOnlyList<EvidenceContext> Evidence, string EditToken);
public sealed record WorkflowCheck(int Id, string Text, bool Completed);
public sealed record WorkflowComment(int Id, string? Author, string Type, string Text, DateTime CreatedAt);
public sealed record EvidenceContext(Guid Id, string Summary, string Checks, string CodeReference, string Author,
    DateTime CreatedAt, DateTime? AcceptedAt, string? AcceptedBy, bool Current);
public sealed record ResultContext(Guid Id, string Conclusion, string Limitations, string Author, DateTime CreatedAt, bool Current);
public sealed record RepositoryContext(int Id, int ProjectId, string Name, string LocalPath);

public sealed class WorkflowOptions
{
    public string ProjectsRoot { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Projects");
}

public sealed class WorkflowCommandInput
{
    public string Operation { get; set; } = "";
    public string Actor { get; set; } = "";
    public string Key { get; set; } = "";
    public Guid? RequestId { get; set; }
    public int? ExpectedVersion { get; set; }
    public int? IdeaId { get; set; }
    public int? ProjectId { get; set; }
    public CaptureRequest? Capture { get; set; }
    public BriefDraft? Brief { get; set; }
    public TaskDraft[]? Tasks { get; set; }
    public Dictionary<int, string>? TaskBaselines { get; set; }
    public Guid? PlanId { get; set; }
    public Guid? ItemId { get; set; }
    public KanbanFlow.Models.WorkStatus? Status { get; set; }
    public EvidenceDraft? Evidence { get; set; }
    public Guid? EvidenceId { get; set; }
    public int[]? CheckedCriterionIds { get; set; }
    public string? Kind { get; set; }
    public string? Text { get; set; }
    public string? Limitations { get; set; }
    public bool? Archived { get; set; }
    public string? Name { get; set; }
    public string? Path { get; set; }
}
public sealed record WorkflowCommandResult(Guid? RequestId, int? Version, int? ResourceId = null);
