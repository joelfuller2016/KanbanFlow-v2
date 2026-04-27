namespace KanbanFlow.Models;

public class Feature
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public int? ParentFeatureId { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Project Project { get; set; } = null!;
    public Feature? ParentFeature { get; set; }
    public List<Feature> ChildFeatures { get; set; } = [];
    public List<TaskItem> Tasks { get; set; } = [];
}
