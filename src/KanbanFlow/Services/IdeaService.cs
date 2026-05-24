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
        ArgumentNullException.ThrowIfNull(dto);

        // Service is the trust boundary — REST, MCP, and Blazor callers all flow through here (#88).
        if (string.IsNullOrWhiteSpace(dto.Title))
            throw new ValidationException("Title cannot be empty or whitespace.");
        var title = dto.Title.Trim();
        if (title.Length > MaxTitleLength)
            throw new ValidationException($"Title cannot exceed {MaxTitleLength} characters.");
        if (dto.Priority is not null && !Enum.IsDefined(dto.Priority.Value))
            throw new ValidationException($"Invalid priority '{dto.Priority.Value}'.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);

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
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        db.Ideas.Add(idea);
        await db.SaveChangesAsync(ct);

        AddActivityEvent(
            db, IdeaProjectId(idea), "Idea", idea.Id, actor,
            "IdeaCreated", null, IdeaActivitySnapshot(idea),
            $"Idea '{idea.Title}' created");

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
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

    public async Task<IdeaPromoteResult> PromoteAsync(int ideaId, string? actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var idea = await db.Ideas.FirstOrDefaultAsync(i => i.Id == ideaId, ct);
        if (idea is null)
            throw new IdeaNotFoundException(ideaId);
        if (idea.Status == IdeaStatus.Promoted || idea.PromotedProjectId is not null)
            throw new ValidationException($"Idea {ideaId} is already promoted to project {idea.PromotedProjectId}.");

        var ideaBefore = IdeaActivitySnapshot(idea);

        var project = new Project
        {
            Name = idea.Title,
            Description = idea.Body,
            Goals = idea.Plan,
            Readme = ComposeProjectReadme(idea)
        };

        // Atomic promote: create the project (assigns its id), link + flip the idea, then
        // audit both sides — one transaction so we never leave a half-promoted idea.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        db.Projects.Add(project);
        await db.SaveChangesAsync(ct);

        idea.Status = IdeaStatus.Promoted;
        idea.PromotedProjectId = project.Id;
        idea.UpdatedAt = DateTime.UtcNow;

        AddActivityEvent(
            db, project.Id, "Project", project.Id, actor,
            "ProjectCreatedFromIdea", null,
            new { project.Id, project.Name, FromIdeaId = idea.Id },
            $"Project '{project.Name}' created from idea {idea.Id}");
        AddActivityEvent(
            db, project.Id, "Idea", idea.Id, actor,
            "IdeaPromoted", ideaBefore, IdeaActivitySnapshot(idea),
            $"Idea '{idea.Title}' promoted to project {project.Id}");

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new IdeaPromoteResult(idea.Id, project.Id);
    }

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
