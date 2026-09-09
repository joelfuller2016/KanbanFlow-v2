namespace KanbanFlow.Models;

/// <summary>
/// Durable identity for a task's record in an external work tracker. Human-editable task names and
/// descriptions are deliberately not part of this key (#336).
/// </summary>
public sealed class ExternalWorkItemLink
{
    public int Id { get; set; }
    public int TaskId { get; set; }
    public ExternalProvider Provider { get; set; }
    public ExternalLinkState State { get; set; } = ExternalLinkState.Active;
    public required string RepositoryExternalId { get; set; }
    public required string RepositoryOwner { get; set; }
    public required string RepositoryName { get; set; }
    public string? ExternalNodeId { get; set; }
    public int? ExternalNumber { get; set; }
    public string? Url { get; set; }
    public string? QuarantineReason { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public TaskItem Task { get; set; } = null!;
}

// ON-DISK CONTRACT: persisted as a raw ordinal int. Append only; never reorder/remove.
public enum ExternalProvider
{
    GitHub = 0
}

// ON-DISK CONTRACT: persisted as a raw ordinal int. Append only; never reorder/remove.
public enum ExternalLinkState
{
    Active = 0,
    Quarantined = 1
}
