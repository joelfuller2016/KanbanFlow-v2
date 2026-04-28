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
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    public Feature Feature { get; set; } = null!;
    public List<Subtask> Subtasks { get; set; } = [];
    public List<TaskComment> Comments { get; set; } = [];
    public List<TaskDependency> Dependencies { get; set; } = [];
    public List<TaskDependency> BlockingDependencies { get; set; } = [];
}

public enum WorkStatus
{
    Backlog,
    Todo,
    InProgress,
    Review,
    Done
}

public enum TaskPriority
{
    Low,
    Medium,
    High,
    Critical
}
