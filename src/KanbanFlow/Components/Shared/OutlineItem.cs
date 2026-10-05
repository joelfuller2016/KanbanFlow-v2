namespace KanbanFlow.Components.Shared;

// Presentation identity: IDs can overlap between the three existing entity types.
public enum OutlineItemKind { Feature, Task, Subtask }

public readonly record struct OutlineItem(OutlineItemKind Kind, int Id);
