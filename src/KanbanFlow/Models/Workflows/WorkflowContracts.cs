using KanbanFlow.Models;

namespace KanbanFlow.Models.Workflows;

public sealed record CaptureRequest(string Title, string Original, int? ProjectId, string Source = "board");
public sealed record BriefDraft(string Outcome, string Scope, string Constraints, string Acceptance);
public sealed record TaskDraft(Guid Id, string Title, string Description, string Acceptance,
    int? RepositoryId, string FileReferences, TaskPriority Priority, string Assignee, Guid[] Dependencies);
public sealed record EvidenceDraft(string Summary, string Checks, string CodeReference);
public sealed record MutationReceipt(Guid RequestId, int Version, string? Value = null);
public sealed record ProjectSummary(int Id, string Name, int RequestCount);
public sealed record RequestSummary(Guid Id, int? ProjectId, string Title, string? ProjectName,
    int Version, bool Archived, int TaskCount, int DoneCount, DateTime UpdatedAt);

public sealed class WorkflowException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
