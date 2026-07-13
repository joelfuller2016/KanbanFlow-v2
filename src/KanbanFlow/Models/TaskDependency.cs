namespace KanbanFlow.Models;

public class TaskDependency
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public int TaskItemId { get; set; }
    public int DependsOnTaskItemId { get; set; }
    public TaskDependencyType Type { get; set; } = TaskDependencyType.DependsOn;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Project Project { get; set; } = null!;
    public TaskItem TaskItem { get; set; } = null!;
    public TaskItem DependsOnTaskItem { get; set; } = null!;
}

// ON-DISK CONTRACT: persisted as a raw ordinal int (no HasConversion). Append only — never
// change, reorder, or remove a member. Locked by PersistedEnumOrdinalTests. See CommentType.
// This table is currently EMPTY on dev boxes, so a reorder here is completely invisible locally.
public enum TaskDependencyType
{
    Blocks = 0,
    DependsOn = 1
}
