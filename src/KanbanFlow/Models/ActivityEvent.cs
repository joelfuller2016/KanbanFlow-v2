namespace KanbanFlow.Models;

public class ActivityEvent
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public required string EntityType { get; set; }
    public int EntityId { get; set; }
    // Historical context only: no FKs, so child history survives task deletion/reassignment.
    public int? TaskId { get; set; }
    public int? RelatedTaskId { get; set; }
    public string? Actor { get; set; }
    public required string EventType { get; set; }
    public string? BeforeJson { get; set; }
    public string? AfterJson { get; set; }
    public string? Message { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Project Project { get; set; } = null!;
}
