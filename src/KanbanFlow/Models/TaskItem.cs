namespace KanbanFlow.Models;

public class TaskItem
{
    public int Id { get; set; }
    public int FeatureId { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public WorkStatus Status { get; set; } = WorkStatus.Backlog;
    public TaskPriority Priority { get; set; } = TaskPriority.Low;
    public string? Assignee { get; set; }
    public string? ClaimedBy { get; set; }
    public DateTime? ClaimedAt { get; set; }
    /// <summary>
    /// Finite ordering rank. Fractional values let a drag place this task between two
    /// persisted siblings without rewriting either sibling's rank (#307).
    /// </summary>
    public double SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    public Feature Feature { get; set; } = null!;
    public List<Subtask> Subtasks { get; set; } = [];
    public List<AcceptanceCriterion> AcceptanceCriteria { get; set; } = [];
    public List<TaskComment> Comments { get; set; } = [];
    public List<TaskDependency> Dependencies { get; set; } = [];
    public List<TaskDependency> BlockingDependencies { get; set; } = [];
    public List<ExternalWorkItemLink> ExternalLinks { get; set; } = [];
}

// ON-DISK CONTRACT: persisted as a raw ordinal int (no HasConversion). Append only — never
// change, reorder, or remove a member. Locked by PersistedEnumOrdinalTests. See CommentType.
public enum WorkStatus
{
    Backlog = 0,
    Todo = 1,
    InProgress = 2,
    Review = 3,
    Done = 4
}

// ON-DISK CONTRACT: persisted as a raw ordinal int (no HasConversion). Append only — never
// change, reorder, or remove a member. Locked by PersistedEnumOrdinalTests. See CommentType.
public enum TaskPriority
{
    Low = 0,
    Medium = 1,
    High = 2,
    Critical = 3
}
