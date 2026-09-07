using System.Text.Json;
using KanbanFlow.Data;
using KanbanFlow.Models;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Services;

/// <summary>
/// Default <see cref="IIdeaService"/> implementation. Uses
/// <see cref="IDbContextFactory{AppDbContext}"/> so each call gets its own
/// short-lived <see cref="AppDbContext"/> — safe for concurrent Blazor Server
/// circuits and Minimal API requests alike. Mirrors the audit conventions of
/// <see cref="TaskService"/> (#88): every mutation writes an <see cref="ActivityEvent"/>.
/// </summary>
public sealed class IdeaService(IDbContextFactory<AppDbContext> dbFactory) : IIdeaService
{
    private const int MaxTitleLength = 500;
    private static readonly JsonSerializerOptions ActivityJsonOptions = new(JsonSerializerDefaults.Web);

    private static object IdeaActivitySnapshot(Idea idea) => new
    {
        idea.Id,
        idea.Title,
        idea.Body,
        idea.Plan,
        Status = idea.Status.ToString(),
        idea.Tags,
        Priority = idea.Priority?.ToString(),
        idea.PromotedProjectId,
        idea.CreatedAt,
        idea.UpdatedAt
    };

    private static void AddActivityEvent(
        AppDbContext db, int projectId, string entityType, int entityId,
        string? actor, string eventType, object? before, object? after, string? message)
    {
        db.ActivityEvents.Add(new ActivityEvent
        {
            ProjectId = projectId,
            EntityType = entityType,
            EntityId = entityId,
            Actor = string.IsNullOrWhiteSpace(actor) ? null : actor.Trim(),
            EventType = eventType,
            BeforeJson = before is null ? null : JsonSerializer.Serialize(before, ActivityJsonOptions),
            AfterJson = after is null ? null : JsonSerializer.Serialize(after, ActivityJsonOptions),
            Message = message
        });
    }

    public async Task<Idea> CreateAsync(IdeaCreate dto, string? actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var idea = await CreateInContextAsync(db, dto, actor, ct);
        await tx.CommitAsync(ct);
        return idea;
    }

    internal static async Task<Idea> CreateInContextAsync(AppDbContext db, IdeaCreate dto, string? actor, CancellationToken ct, string source = "idea")
    {
        ArgumentNullException.ThrowIfNull(dto);

        // Service is the trust boundary — REST, MCP, and Blazor callers all flow through here (#88).
        if (string.IsNullOrWhiteSpace(dto.Title))
            throw new ValidationException("Title cannot be empty or whitespace.");
        var title = dto.Title.Trim();
        if (title.Length > MaxTitleLength)
            throw new ValidationException($"Title cannot exceed {MaxTitleLength} characters.");
        if (dto.Priority is not null && !Enum.IsDefined(dto.Priority.Value))
            throw new ValidationException($"Invalid priority '{dto.Priority.Value}'.");


        var idea = new Idea
        {
            Title = title,
            Body = Normalize(dto.Body),
            Plan = Normalize(dto.Plan),
            Tags = Normalize(dto.Tags),
            Priority = dto.Priority,
            Status = IdeaStatus.New
        };

        // Atomic create + audit: first save assigns idea.Id (needed by the activity event),
        // second persists the audit row. One transaction so a failure can't leave an idea
        // without its IdeaCreated event.

        db.Ideas.Add(idea);
        Workflows.IdeaWorkflowCapture.Add(db, idea, string.IsNullOrWhiteSpace(dto.Body) ? dto.Title : dto.Body, actor, source);
        await db.SaveChangesAsync(ct);

        AddActivityEvent(
            db, IdeaProjectId(idea), "Idea", idea.Id, actor,
            "IdeaCreated", null, IdeaActivitySnapshot(idea),
            $"Idea '{idea.Title}' created");

        await db.SaveChangesAsync(ct);
        return idea;
    }

    public async Task<Idea?> GetByIdAsync(int ideaId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Ideas.AsNoTracking().FirstOrDefaultAsync(i => i.Id == ideaId, ct);
    }

    public async Task<IReadOnlyList<Idea>> ListAsync(
        IdeaStatus? status = null, string? tag = null, string? search = null,
        int limit = 100, int offset = 0, CancellationToken ct = default)
    {
        var take = Math.Clamp(limit, 1, 500);
        var skip = Math.Max(0, offset);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var query = db.Ideas.AsNoTracking().AsQueryable();

        if (status is not null)
            query = query.Where(i => i.Status == status);
        if (!string.IsNullOrWhiteSpace(tag))
        {
            // Case-insensitive substring match (EF translates ToLower() to SQLite lower()).
            var t = tag.Trim().ToLower();
            query = query.Where(i => i.Tags != null && i.Tags.ToLower().Contains(t));
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(i =>
                i.Title.ToLower().Contains(s) || (i.Body != null && i.Body.ToLower().Contains(s)));
        }

        return await query
            .OrderByDescending(i => i.CreatedAt)
            .ThenByDescending(i => i.Id)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);
    }

    public async Task<Idea> UpdateAsync(int ideaId, IdeaUpdate dto, string? actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dto);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var idea = await db.Ideas.FirstOrDefaultAsync(i => i.Id == ideaId, ct);
        if (idea is null)
            throw new IdeaNotFoundException(ideaId);

        var before = IdeaActivitySnapshot(idea);
        var originalBody = idea.Body;
        var originalPlan = idea.Plan;
        var changed = false;

        if (dto.Title is not null)
        {
            if (string.IsNullOrWhiteSpace(dto.Title))
                throw new ValidationException("Title cannot be empty or whitespace.");
            var title = dto.Title.Trim();
            if (title.Length > MaxTitleLength)
                throw new ValidationException($"Title cannot exceed {MaxTitleLength} characters.");
            if (idea.Title != title) { idea.Title = title; changed = true; }
        }
        if (dto.Body is not null)
        {
            var body = Normalize(dto.Body);
            if (idea.Body != body) { idea.Body = body; changed = true; }
        }
        if (dto.Plan is not null)
        {
            var plan = Normalize(dto.Plan);
            if (idea.Plan != plan) { idea.Plan = plan; changed = true; }
        }
        if (dto.Tags is not null)
        {
            var tags = Normalize(dto.Tags);
            if (idea.Tags != tags) { idea.Tags = tags; changed = true; }
        }
        if (dto.Status is not null)
        {
            if (!Enum.IsDefined(dto.Status.Value))
                throw new ValidationException($"Invalid status '{dto.Status.Value}'.");
            // One predicate, both directions (#283 + #285). Refusing at the service closes all three
            // callers at once — PATCH /api/ideas/{id}, MCP update_idea, IdeaModal — per the #88
            // single-boundary rule.
            if (!IsStatusTransitionAllowed(idea.Status, dto.Status.Value))
                throw new ValidationException(StatusTransitionRefusal(idea, dto.Status.Value));
            if (idea.Status != dto.Status.Value) { idea.Status = dto.Status.Value; changed = true; }
        }
        if (dto.Priority is not null)
        {
            if (!Enum.IsDefined(dto.Priority.Value))
                throw new ValidationException($"Invalid priority '{dto.Priority.Value}'.");
            if (idea.Priority != dto.Priority.Value) { idea.Priority = dto.Priority.Value; changed = true; }
        }

        if (!changed)
            return idea; // idempotent: nothing to persist, no audit noise

        var workflow = await db.WorkRequests.Include(r => r.Messages).SingleOrDefaultAsync(r => r.IdeaId == ideaId, ct)
            ?? Workflows.IdeaWorkflowCapture.Add(db, idea, originalBody ?? idea.Title, actor, "legacy", exact: false);
        workflow.Title = idea.Title;
        // Promotion is terminal for ideas; archiving its workflow is an independent action.
        // A routine title/notes edit must not restore an explicitly archived promoted request.
        if (idea.Status != IdeaStatus.Promoted)
            workflow.Archived = idea.Status is IdeaStatus.Archived or IdeaStatus.Rejected;
        if (originalBody != idea.Body) Workflows.IdeaWorkflowCapture.RecordSummaryEdit(workflow, actor, "notes", idea.Body);
        if (originalPlan != idea.Plan) Workflows.IdeaWorkflowCapture.RecordSummaryEdit(workflow, actor, "plan notes", idea.Plan);
        if (originalBody == idea.Body && originalPlan == idea.Plan) workflow.Version++;
        workflow.UpdatedAt = DateTime.UtcNow;
        idea.UpdatedAt = DateTime.UtcNow;
        AddActivityEvent(
            db, IdeaProjectId(idea), "Idea", idea.Id, actor,
            "IdeaUpdated", before, IdeaActivitySnapshot(idea),
            $"Idea '{idea.Title}' updated");

        await db.SaveChangesAsync(ct);
        return idea;
    }

    public async Task DeleteAsync(int ideaId, string? actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var idea = await db.Ideas.FirstOrDefaultAsync(i => i.Id == ideaId, ct);
        if (idea is null)
            throw new IdeaNotFoundException(ideaId);

        // Snapshot + queue the audit event from the pre-removal state, then queue the delete.
        // ActivityEvent has no FK to the idea, so the row survives the delete (same
        // audit-survives-deletion guarantee as tasks, #85/#88).
        AddActivityEvent(
            db, IdeaProjectId(idea), "Idea", idea.Id, actor,
            "IdeaDeleted", IdeaActivitySnapshot(idea), null,
            $"Idea '{idea.Title}' deleted");
        db.Ideas.Remove(idea);

        // Single SaveChanges so the audit insert and the delete commit atomically in one EF
        // transaction (matches TaskService.DeleteAsync) — never an IdeaDeleted row with the idea
        // still present.
        await db.SaveChangesAsync(ct);
    }

    public async Task<IdeaPromoteResult> PromoteAsync(int ideaId, string? actor, int? existingProjectId = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var result = await PromoteInContextAsync(db, ideaId, actor, ct, existingProjectId);
        await tx.CommitAsync(ct);
        return result;
    }

    internal static async Task<IdeaPromoteResult> PromoteInContextAsync(AppDbContext db, int ideaId, string? actor, CancellationToken ct, int? existingProjectId = null)
    {
        var idea = await db.Ideas.FirstOrDefaultAsync(i => i.Id == ideaId, ct);
        if (idea is null)
            throw new IdeaNotFoundException(ideaId);
        // Fast-path reject for the common (non-concurrent) double-promote. The authoritative
        // guard is the conditional UPDATE below — this snapshot read can go stale under a
        // concurrent promote, so it is an optimization, not the race protection.
        if (idea.Status == IdeaStatus.Promoted || idea.PromotedProjectId is not null)
        {
            if (existingProjectId is int sameProject && idea.PromotedProjectId == sameProject)
                return new IdeaPromoteResult(idea.Id, sameProject);
            throw AlreadyPromoted(idea);
        }

        var ideaBefore = IdeaActivitySnapshot(idea);

        var project = existingProjectId is int target
            ? await db.Projects.SingleOrDefaultAsync(p => p.Id == target, ct) ?? throw new ProjectNotFoundException(target)
            : new Project { Name = idea.Title, Description = idea.Body, Goals = idea.Plan, Readme = ComposeProjectReadme(idea) };
        var createdProject = existingProjectId is null;

        // Atomic promote: create the project (assigns its id), then flip the idea with a
        // conditional UPDATE, then audit both sides — one transaction so we never leave a
        // half-promoted idea.

        if (createdProject) db.Projects.Add(project);
        await db.SaveChangesAsync(ct);

        // Conditional flip closes the check-then-act race: only promote while the idea is still
        // un-promoted. A concurrent PromoteAsync that commits first sets these columns, so our
        // WHERE then matches 0 rows and we roll back — never a second project or orphaned row.
        // ExecuteUpdate runs inside the ambient transaction opened above.
        var now = DateTime.UtcNow;
        var rows = await db.Ideas
            .Where(i => i.Id == ideaId && i.Status != IdeaStatus.Promoted && i.PromotedProjectId == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.Status, IdeaStatus.Promoted)
                .SetProperty(i => i.PromotedProjectId, project.Id)
                .SetProperty(i => i.UpdatedAt, now), ct);
        if (rows == 0)
        {
            // A concurrent promote won: undo our just-inserted project (no orphan) and report
            // against the now-committed state.
            var current = await db.Ideas.AsNoTracking().FirstOrDefaultAsync(i => i.Id == ideaId, ct);
            throw current is null ? new IdeaNotFoundException(ideaId) : AlreadyPromoted(current);
        }

        // Reflect the committed flip on the tracked entity and capture the `after` audit snapshot
        // from those promoted values NOW — before suppressing the redundant write. Marking the entry
        // Unchanged reverts its current values to the originally-loaded (pre-promotion) ones, so the
        // snapshot MUST be taken first; then Unchanged stops SaveChanges from re-issuing an UPDATE
        // (ExecuteUpdate already persisted the flip). Order is load-bearing — do not move ideaAfter.
        idea.Status = IdeaStatus.Promoted;
        idea.PromotedProjectId = project.Id;
        idea.UpdatedAt = now;
        var ideaAfter = IdeaActivitySnapshot(idea);
        db.Entry(idea).State = EntityState.Unchanged;

        var workflow = await db.WorkRequests.SingleOrDefaultAsync(r => r.IdeaId == ideaId, ct)
            ?? Workflows.IdeaWorkflowCapture.Add(db, idea, idea.Body ?? idea.Title, actor, "legacy", exact: false);
        workflow.ProjectId = project.Id;
        workflow.Version++;
        workflow.UpdatedAt = now;

        if (createdProject) AddActivityEvent(
            db, project.Id, "Project", project.Id, actor,
            "ProjectCreatedFromIdea", null,
            new { project.Id, project.Name, FromIdeaId = idea.Id },
            $"Project '{project.Name}' created from idea {idea.Id}");
        AddActivityEvent(
            db, project.Id, "Idea", idea.Id, actor,
            "IdeaPromoted", ideaBefore, ideaAfter,
            $"Idea '{idea.Title}' promoted to project {project.Id}");

        await db.SaveChangesAsync(ct);
        return new IdeaPromoteResult(idea.Id, project.Id);
    }

    /// <summary>
    /// THE status-transition rule, in one place: <b><see cref="IdeaStatus.Promoted"/> is derived and
    /// terminal.</b> Every other transition is free — ideas are a triage inbox, not a workflow.
    ///
    /// <para><b>Derived</b> (#283): nothing may transition <em>into</em> <c>Promoted</c> here.
    /// <see cref="PromoteAsync"/> is its only legitimate writer, because it alone also creates the
    /// <see cref="Project"/> and sets <see cref="Idea.PromotedProjectId"/> in the same transaction.
    /// Setting it here would strand the idea as promoted-with-no-project.</para>
    ///
    /// <para><b>Terminal</b> (#285): nothing may transition <em>out of</em> <c>Promoted</c> here.
    /// <see cref="IdeaUpdate"/> carries no <c>PromotedProjectId</c> field, so moving a promoted idea
    /// back to <c>New</c> would leave its project link dangling with no way to ever clear it.</para>
    ///
    /// <para>Both strandings are permanently unrecoverable, which is why this is a hard refusal rather
    /// than a repair. Together they keep <c>Status == Promoted</c> and <c>PromotedProjectId != null</c>
    /// in step across every public write path. A self-transition (<paramref name="from"/> ==
    /// <paramref name="to"/>) is always allowed: it changes nothing, so it cannot break either rule, and
    /// it keeps PATCH idempotent for clients that round-trip the whole entity.</para>
    ///
    /// <para>This predicate exists because the rule used to be implicit, and got re-implemented — badly
    /// — at each new site: #89, then #283, then #285, all the same shape. It now lives here, once.</para>
    /// </summary>
    private static bool IsStatusTransitionAllowed(IdeaStatus from, IdeaStatus to) =>
        from == to || (from != IdeaStatus.Promoted && to != IdeaStatus.Promoted);

    /// <summary>
    /// The actionable refusal for a transition <see cref="IsStatusTransitionAllowed"/> rejected. A
    /// disallowed transition always crosses the <see cref="IdeaStatus.Promoted"/> boundary in exactly
    /// one direction, so <paramref name="to"/> alone picks the message. Each one names what the caller
    /// CAN do instead of only saying no.
    /// </summary>
    private static string StatusTransitionRefusal(Idea idea, IdeaStatus to) =>
        to == IdeaStatus.Promoted
            // Into Promoted (#283) — point at the one path that promotes properly.
            ? "Promoted is a derived status and cannot be set directly. " +
              "Use POST /api/ideas/{id}/promote (MCP: promote_idea) to promote an idea."
            // Out of Promoted (#285) — promotion is one-way; the rest of the idea is still editable.
            : $"Idea {idea.Id} is promoted, and promotion is permanent: its status cannot be changed to " +
              $"{to}. Every other field (title, body, plan, tags, priority) is still editable — omit " +
              "`status` from the update to change them.";

    /// <summary>
    /// Message for an already-promoted idea. Names the target project when the link is intact,
    /// or explains the orphan case (Status is Promoted but the project was removed, nulling the
    /// FK via OnDelete(SetNull)) rather than rendering a blank "project .".
    /// </summary>
    private static ValidationException AlreadyPromoted(Idea idea) => new(
        idea.PromotedProjectId is int pid
            ? $"Idea {idea.Id} is already promoted to project {pid}."
            : $"Idea {idea.Id} is already marked promoted; its project was removed.");

    /// <summary>Trim to null: blank strings become <c>null</c> for clean storage.</summary>
    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Audit attribution for an idea event. Ideas are pre-project, so use the promoted
    /// project id once set, else 0 ("no project"). Legal because <see cref="ActivityEvent"/>
    /// has no FK to <see cref="Project"/>; the event is identified by EntityType+EntityId.
    /// </summary>
    private static int IdeaProjectId(Idea idea) => idea.PromotedProjectId ?? 0;

    /// <summary>Compose the promoted project's readme from the idea's body + plan (markdown).</summary>
    private static string? ComposeProjectReadme(Idea idea)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(idea.Body)) parts.Add(idea.Body.Trim());
        if (!string.IsNullOrWhiteSpace(idea.Plan)) parts.Add($"## Plan\n\n{idea.Plan.Trim()}");
        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }
}
