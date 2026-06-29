namespace KanbanFlow.Models;

/// <summary>
/// An observation appended to a <see cref="MemoryNode"/> (#221) — a small, additive
/// note that enriches the parent memory without superseding it (e.g. "confirmed again
/// on 6/28", "saw the same error in feature X"). Observations are write-once detail
/// rows; they cascade-delete with their parent node.
/// </summary>
public class Observation
{
    public int Id { get; set; }

    /// <summary>The owning <see cref="MemoryNode"/>. Cascade-deletes with the node.</summary>
    public int MemoryNodeId { get; set; }

    /// <summary>The observation text. Never empty.</summary>
    public required string Content { get; set; }

    /// <summary>Optional provenance (who/what recorded this observation).</summary>
    public string? Source { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public MemoryNode MemoryNode { get; set; } = null!;
}
