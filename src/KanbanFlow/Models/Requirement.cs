namespace KanbanFlow.Models;

public class Requirement
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public required string Text { get; set; }
    public string? Description { get; set; }
    public RequirementPriority Priority { get; set; } = RequirementPriority.P1;
    public RequirementStatus Status { get; set; } = RequirementStatus.Open;
    public int? FeatureId { get; set; }
    public int? TaskItemId { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Project Project { get; set; } = null!;
    public Feature? Feature { get; set; }
    public TaskItem? TaskItem { get; set; }
}

// ON-DISK CONTRACT: persisted as a raw ordinal int (no HasConversion). Append only — never
// change, reorder, or remove a member. Locked by PersistedEnumOrdinalTests. See CommentType.
public enum RequirementPriority { P0 = 0, P1 = 1, P2 = 2 }
// ON-DISK CONTRACT: persisted as a raw ordinal int (no HasConversion). Append only — never
// change, reorder, or remove a member. Locked by PersistedEnumOrdinalTests. See CommentType.
public enum RequirementStatus { Open = 0, InProgress = 1, Done = 2 }
