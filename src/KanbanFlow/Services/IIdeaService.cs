using KanbanFlow.Models;

namespace KanbanFlow.Services;

/// <summary>
/// Audit-aware mutations for the Ideas inbox (#180). Every REST endpoint, MCP tool,
/// and Blazor component MUST go through this service so each change produces an
/// <see cref="ActivityEvent"/> row — the same single-audit-boundary rule that #88
/// established for tasks. Idea activity events use <c>EntityType = "Idea"</c> and the
/// idea's id as <c>EntityId</c>; their <c>ProjectId</c> is the promoted project id (or
/// <c>0</c> while the idea is still pre-project, since <see cref="ActivityEvent"/> has
/// no FK to <see cref="Project"/>).
/// </summary>
public interface IIdeaService
{
    /// <summary>
    /// Create a new idea (always starts <see cref="IdeaStatus.New"/>). Validates the
    /// title (non-empty, &lt;= 500 chars) and the optional priority enum. Emits an
    /// <c>IdeaCreated</c> activity event.
    /// </summary>
    /// <exception cref="ValidationException">Title empty/too long or priority undefined.</exception>
    Task<Idea> CreateAsync(IdeaCreate dto, string? actor, CancellationToken ct = default);

    /// <summary>Read a single idea, or <c>null</c> when it does not exist.</summary>
    Task<Idea?> GetByIdAsync(int ideaId, CancellationToken ct = default);

    /// <summary>
    /// List ideas newest-first with optional filters. <paramref name="tag"/> and
    /// <paramref name="search"/> are case-insensitive substring matches (case-folded
    /// <c>Contains</c>); <paramref name="search"/> matches title or body. <paramref name="limit"/>
    /// is clamped to 1..500 and <paramref name="offset"/> floored at 0.
    /// </summary>
    Task<IReadOnlyList<Idea>> ListAsync(
        IdeaStatus? status = null, string? tag = null, string? search = null,
        int limit = 100, int offset = 0, CancellationToken ct = default);

    /// <summary>
    /// Update mutable fields. <c>null</c> properties on <paramref name="dto"/> mean
    /// "leave unchanged". Idempotent when nothing actually changes (no event written).
    /// Validates title/status/priority. Emits an <c>IdeaUpdated</c> event when a field changed.
    /// </summary>
    /// <exception cref="IdeaNotFoundException">The idea does not exist.</exception>
    /// <exception cref="ValidationException">
    /// Title empty/too long, an enum value undefined, or <paramref name="dto"/> sets
    /// <see cref="IdeaStatus.Promoted"/> — a derived status that only <see cref="PromoteAsync"/> may
    /// write, since it alone also creates the <see cref="Project"/> and links it via
    /// <see cref="Idea.PromotedProjectId"/>. Leave <see cref="IdeaUpdate.Status"/> <c>null</c> to edit
    /// an already-promoted idea's other fields.
    /// </exception>
    Task<Idea> UpdateAsync(int ideaId, IdeaUpdate dto, string? actor, CancellationToken ct = default);

    /// <summary>
    /// Delete an idea. Emits an <c>IdeaDeleted</c> event <em>before</em> removal so the
    /// audit row survives (ActivityEvent has no FK to the idea).
    /// </summary>
    /// <exception cref="IdeaNotFoundException">The idea does not exist.</exception>
    Task DeleteAsync(int ideaId, string? actor, CancellationToken ct = default);

    /// <summary>
    /// Promote an idea into a new <see cref="Project"/> (Title → Name, Body → Description,
    /// Plan → Goals, Body+Plan → Readme), set <see cref="IdeaStatus.Promoted"/> +
    /// <see cref="Idea.PromotedProjectId"/>, and emit audit events for both the idea
    /// (<c>IdeaPromoted</c>) and the new project (<c>ProjectCreatedFromIdea</c>).
    /// </summary>
    /// <exception cref="IdeaNotFoundException">The idea does not exist.</exception>
    /// <exception cref="ValidationException">The idea is already promoted.</exception>
    Task<IdeaPromoteResult> PromoteAsync(int ideaId, string? actor, CancellationToken ct = default);
}

/// <summary>Create payload for <see cref="IIdeaService.CreateAsync"/>.</summary>
public sealed record IdeaCreate(
    string Title,
    string? Body = null,
    string? Plan = null,
    string? Tags = null,
    TaskPriority? Priority = null);

/// <summary>Update payload for <see cref="IIdeaService.UpdateAsync"/>. <c>null</c> fields are unchanged.</summary>
public sealed record IdeaUpdate(
    string? Title = null,
    string? Body = null,
    string? Plan = null,
    IdeaStatus? Status = null,
    string? Tags = null,
    TaskPriority? Priority = null);

/// <summary>Result of <see cref="IIdeaService.PromoteAsync"/>: the source idea and the created project.</summary>
public sealed record IdeaPromoteResult(int IdeaId, int ProjectId);
