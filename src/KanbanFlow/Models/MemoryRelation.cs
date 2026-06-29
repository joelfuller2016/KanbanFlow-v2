namespace KanbanFlow.Models;

/// <summary>
/// A directed edge in the memory graph (#221): <c>(FromType, FromId) --RelationType--&gt; (ToType, ToId)</c>.
/// The endpoints are discriminator pairs (a type name + an id) rather than hard foreign keys, so an
/// edge can span memory nodes <em>and</em> existing card ids (Task / Project / Feature / …) without
/// cascade surprises — the same no-FK approach <see cref="ActivityEvent"/> uses for its audit rows.
/// <see cref="RelationType"/> is written in the active voice ("decided", "supersedes", "relates_to").
/// </summary>
public class MemoryRelation
{
    public int Id { get; set; }

    /// <summary>Type discriminator of the source endpoint, e.g. "MemoryNode", "Task", "Project".</summary>
    public required string FromType { get; set; }

    /// <summary>Id of the source endpoint within <see cref="FromType"/> (no hard FK).</summary>
    public int FromId { get; set; }

    /// <summary>Active-voice relation verb, e.g. "decided", "supersedes", "relates_to", "derived_from".</summary>
    public required string RelationType { get; set; }

    /// <summary>Type discriminator of the target endpoint, e.g. "MemoryNode", "Task", "Project".</summary>
    public required string ToType { get; set; }

    /// <summary>Id of the target endpoint within <see cref="ToType"/> (no hard FK).</summary>
    public int ToId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
