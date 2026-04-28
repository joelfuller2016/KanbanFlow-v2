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

public enum TaskDependencyType
{
    Blocks,
    DependsOn
}
