namespace KanbanFlow.Models;

public class TaskComment
{
    public int Id { get; set; }
    public int TaskItemId { get; set; }
    public required string Text { get; set; }
    public string? Author { get; set; }
    public CommentType Type { get; set; } = CommentType.Comment;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public TaskItem TaskItem { get; set; } = null!;
}

public enum CommentType
{
    Comment,
    StatusChange,
    Created,
    FeatureChange,
    PriorityChange,
    AssigneeChange
}
