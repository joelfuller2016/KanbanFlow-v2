namespace KanbanFlow.Models;

public class AcceptanceCriterion
{
    public int Id { get; set; }
    public int TaskItemId { get; set; }
    public required string Text { get; set; }
    public bool Completed { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public TaskItem TaskItem { get; set; } = null!;
}
