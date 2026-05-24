namespace KanbanFlow.Models;

/// <summary>
/// A captured brainstorm — the lightweight "dumping ground" entity behind the
/// Ideas inbox (#180). An Idea lives <em>before</em> the committed Project graph:
/// a title, a freeform markdown <see cref="Body"/>, and an optional <see cref="Plan"/>.
/// A promising one is later promoted into a full <see cref="Project"/> via
/// <c>IdeaService.PromoteAsync</c>, which sets <see cref="Status"/> to
/// <see cref="IdeaStatus.Promoted"/> and links it through <see cref="PromotedProjectId"/>.
/// </summary>
public class Idea
{
    public int Id { get; set; }
    public required string Title { get; set; }

    /// <summary>Markdown brain-dump (the raw idea).</summary>
    public string? Body { get; set; }

    /// <summary>Optional markdown plan (problem · approach · scope · value), composed by the caller.</summary>
    public string? Plan { get; set; }

    public IdeaStatus Status { get; set; } = IdeaStatus.New;

    /// <summary>Comma-separated freeform tags for grouping the inbox (LIKE-searchable).</summary>
    public string? Tags { get; set; }

    public TaskPriority? Priority { get; set; }

    /// <summary>
    /// Set when the idea is promoted to a project. The FK uses <c>OnDelete(SetNull)</c>
    /// so deleting the project clears the link without deleting the originating idea.
    /// </summary>
    public int? PromotedProjectId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Project? PromotedProject { get; set; }
}

public enum IdeaStatus
{
    New,
    Triaging,
    Promoted,
    Archived,
    Rejected
}
