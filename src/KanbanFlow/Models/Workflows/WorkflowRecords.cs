using KanbanFlow.Models;

namespace KanbanFlow.Models.Workflows;

public sealed class WorkRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int IdeaId { get; set; }
    public Idea Idea { get; set; } = null!;
    public int? ProjectId { get; set; }
    public Project? Project { get; set; }
    public int? FeatureId { get; set; }
    public required string Title { get; set; }
    public int Version { get; set; } = 1;
    public bool Archived { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<RequestMessage> Messages { get; set; } = [];
    public List<BriefRevision> Briefs { get; set; } = [];
    public List<PlanVersion> Plans { get; set; } = [];
    public List<PlannedTask> WorkItems { get; set; } = [];
    public List<RequestResult> Results { get; set; } = [];
}

public sealed class RequestMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RequestId { get; set; }
    public WorkRequest Request { get; set; } = null!;
    public required string Kind { get; set; }
    public required string Text { get; set; }
    public required string Author { get; set; }
    public required string Source { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class BriefRevision
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RequestId { get; set; }
    public WorkRequest Request { get; set; } = null!;
    public int Number { get; set; }
    public string IntentHash { get; set; } = "";
    public required string Outcome { get; set; }
    public string Scope { get; set; } = "";
    public string Constraints { get; set; } = "";
    public required string Acceptance { get; set; }
    public required string Author { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class PlanVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RequestId { get; set; }
    public WorkRequest Request { get; set; } = null!;
    public int Number { get; set; }
    public Guid BriefRevisionId { get; set; }
    public BriefRevision Brief { get; set; } = null!;
    public required string DraftJson { get; set; }
    public string TaskBaselineJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }
    public DateTime? PublishedAt { get; set; }
}

// Mapping to the existing TaskItem, not a parallel task/card table. No runtime is implied.
public sealed class PlannedTask
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RequestId { get; set; }
    public WorkRequest Request { get; set; } = null!;
    public Guid PlanVersionId { get; set; }
    public PlanVersion Plan { get; set; } = null!;
    public Guid DraftId { get; set; }
    public int TaskItemId { get; set; }
    public TaskItem Task { get; set; } = null!;
    public int SpecificationVersion { get; set; } = 1;
    public required string SpecificationHash { get; set; }
    public required string SpecificationJson { get; set; }
    public List<TaskEvidence> Evidence { get; set; } = [];
}

public sealed class TaskEvidence
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkItemId { get; set; }
    public PlannedTask WorkItem { get; set; } = null!;
    public int SpecificationVersion { get; set; }
    public required string SpecificationHash { get; set; }
    public string TaskStateHash { get; set; } = "";
    public required string Summary { get; set; }
    public required string Checks { get; set; }
    public required string CodeReference { get; set; }
    public required string Author { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? AcceptedAt { get; set; }
    public string? AcceptedBy { get; set; }
}

public sealed class RequestResult
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RequestId { get; set; }
    public WorkRequest Request { get; set; } = null!;
    public required string Conclusion { get; set; }
    public required string Limitations { get; set; }
    public required string Author { get; set; }
    public int RequestVersion { get; set; }
    public string ContextHash { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public sealed class ProjectRepository
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public required string Name { get; set; }
    public required string LocalPath { get; set; }
}

public sealed class WorkflowReceipt
{
    public long Id { get; set; }
    public required string Actor { get; set; }
    public required string Operation { get; set; }
    public required string Key { get; set; }
    public required string PayloadHash { get; set; }
    public required string ResultJson { get; set; }
    public DateTime CreatedAt { get; set; }
}
