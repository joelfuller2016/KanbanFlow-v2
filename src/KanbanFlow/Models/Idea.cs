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

    /// <summary>Version read with these fields; pass it as ExpectedWorkflowVersion when editing. Zero identifies an uncaptured legacy idea.</summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public int WorkflowVersion { get; set; }

    public Project? PromotedProject { get; set; }
}

// ON-DISK CONTRACT: persisted as a raw ordinal int (no HasConversion). Append only — never
// change, reorder, or remove a member. Locked by PersistedEnumOrdinalTests. See CommentType.
// Note this one is especially exposed: the live DB currently holds only {0,1,3}, so removing
// or reordering Promoted(2) would look completely harmless here and corrupt every other install.
public enum IdeaStatus
{
    New = 0,
    Triaging = 1,
    Promoted = 2,
    Archived = 3,
    Rejected = 4
}
