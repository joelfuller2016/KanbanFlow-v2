using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KanbanFlow.Models;
using KanbanFlow.Data;
using KanbanFlow.Models.Workflows;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Services.Workflows;

public sealed partial class WorkspaceService(IDbContextFactory<AppDbContext> factory, TimeProvider clock) : IWorkspaceService
{
    private static readonly JsonSerializerOptions AuditJson = new(JsonSerializerDefaults.Web);
    public static string Hash<T>(T payload) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload))));
    public static TaskDraft[] Drafts(PlanVersion? plan) => plan is null ? [] : JsonSerializer.Deserialize<TaskDraft[]>(plan.DraftJson)!;

    public async Task<List<ProjectSummary>> ProjectsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Projects.AsNoTracking().OrderBy(p => p.Name)
            .Select(p => new ProjectSummary(p.Id, p.Name, db.WorkRequests.Count(r => r.ProjectId == p.Id))).ToListAsync(ct);
    }

    public async Task<List<RequestSummary>> RequestsAsync(int? projectId = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.WorkRequests.AsNoTracking().Where(r => projectId == null || r.ProjectId == projectId)
            .OrderBy(r => r.Archived).ThenByDescending(r => r.UpdatedAt).Take(200)
            .Select(r => new RequestSummary(r.Id, r.ProjectId, r.Title, r.Project == null ? null : r.Project.Name,
                r.Version, r.Archived, r.WorkItems.Count, r.WorkItems.Count(w => w.Task.Status == WorkStatus.Done), r.UpdatedAt)).ToListAsync(ct);
    }

    public async Task<WorkRequest?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await Graph(db).AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct);
    }

    public async Task<WorkRequest?> GetForIdeaAsync(int ideaId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await Graph(db).AsNoTracking().SingleOrDefaultAsync(r => r.IdeaId == ideaId, ct);
    }

    public async Task<WorkRequest?> GetForTaskAsync(int taskId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await Graph(db).AsNoTracking().SingleOrDefaultAsync(r => r.WorkItems.Any(w => w.TaskItemId == taskId), ct);
    }

    public Task<MutationReceipt> AdoptIdeaAsync(int ideaId, string actor, string key, CancellationToken ct = default) =>
        WriteAsync(actor, "idea.workflow", key, new { ideaId }, async db =>
        {
            var request = await db.WorkRequests.SingleOrDefaultAsync(r => r.IdeaId == ideaId, ct);
            if (request is not null) return new MutationReceipt(request.Id, request.Version);
            var idea = await db.Ideas.SingleOrDefaultAsync(i => i.Id == ideaId, ct) ?? throw Missing("Idea");
            request = IdeaWorkflowCapture.Add(db, idea, idea.Body ?? idea.Title, actor, "legacy", exact: false);
            Audit(db, request.ProjectId, request.Id, actor, "IdeaWorkflowAdopted", request.IdeaId);
            return new MutationReceipt(request.Id, request.Version);
        }, ct);

    public async Task<List<ProjectRepository>> RepositoriesAsync(int? projectId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.ProjectRepositories.AsNoTracking().Where(r => r.ProjectId == projectId).OrderBy(r => r.Name).ToListAsync(ct);
    }

    public Task<int> CreateProjectAsync(string name, string actor, string key, CancellationToken ct = default) =>
        WriteAsync(actor, "project.create", key, new { name }, async db =>
        {
            var project = new Project { Name = Required(name, "Project name", 200).Trim(), CreatedAt = Now, UpdatedAt = Now };
            db.Projects.Add(project);
            await db.SaveChangesWithAuditAsync(actor, ct);
            Audit(db, project.Id, Guid.Empty, actor, "WorkspaceProjectCreated", project.Id);
            return project.Id;
        }, ct);

    public Task<int> RegisterRepositoryAsync(int projectId, string name, string path, string projectsRoot,
        string actor, string key, CancellationToken ct = default) =>
        WriteAsync(actor, "repository.register", key, new { projectId, name, path }, async db =>
        {
            if (!await db.Projects.AnyAsync(p => p.Id == projectId, ct)) throw Missing("Project");
            var resolved = RepositoryPaths.Resolve(path, projectsRoot);
            var existing = await db.ProjectRepositories.FirstOrDefaultAsync(r => r.ProjectId == projectId && r.LocalPath == resolved, ct);
            if (existing is not null) return existing.Id;
            var repository = new ProjectRepository { ProjectId = projectId, Name = Required(name, "Repository name", 120).Trim(), LocalPath = resolved };
            db.ProjectRepositories.Add(repository);
            await db.SaveChangesWithAuditAsync(actor, ct);
            Audit(db, projectId, Guid.Empty, actor, "WorkspaceRepositoryRegistered", repository.Id);
            return repository.Id;
        }, ct);

    public Task<MutationReceipt> CaptureAsync(CaptureRequest input, string actor, string key, CancellationToken ct = default) =>
        WriteAsync(actor, "request.capture", key, input, async db =>
        {
            ArgumentNullException.ThrowIfNull(input);
            Required(input.Title, "Title", 200);
            Required(input.Original, "Original request", 50000);
            Required(input.Source, "Source", 100);
            var idea = await IdeaService.CreateInContextAsync(db, new IdeaCreate(input.Title, input.Original), actor, ct, input.Source);
            var request = await db.WorkRequests.SingleAsync(r => r.IdeaId == idea.Id, ct);
            if (input.ProjectId is int projectId)
                await IdeaService.PromoteInContextAsync(db, idea.Id, actor, ct, projectId);
            Audit(db, request.ProjectId, request.Id, actor, "RequestCaptured", request.IdeaId);
            return new MutationReceipt(request.Id, request.Version);
        }, ct);

    public Task<MutationReceipt> AttachProjectAsync(Guid id, int expected, int projectId, string actor, string key, CancellationToken ct = default) =>
        ChangeAsync(id, expected, actor, "request.project", key, new { projectId }, async (db, request) =>
        {
            if (request.WorkItems.Count != 0) throw Invalid("A published request keeps its project. Create a linked request for another project.");
            await IdeaService.PromoteInContextAsync(db, request.IdeaId, actor, ct, projectId);
        }, ct);

    public Task<MutationReceipt> SaveBriefAsync(Guid id, int expected, BriefDraft input, string actor, string key, CancellationToken ct = default) =>
        ChangeAsync(id, expected, actor, "brief.save", key, input, (db, request) =>
        {
            request.Briefs.Add(new BriefRevision { RequestId = id, Number = request.Briefs.Count + 1,
                IntentHash = IntentHash(request),
                Outcome = Required(input.Outcome, "Desired outcome", 10000), Scope = Bounded(input.Scope, "Scope", 10000),
                Constraints = Bounded(input.Constraints, "Constraints", 10000), Acceptance = Required(input.Acceptance, "Acceptance outcomes", 10000), Author = actor, CreatedAt = Now });
            return Task.CompletedTask;
        }, ct);

    public Task<MutationReceipt> AddMessageAsync(Guid id, int expected, string kind, string text, string actor, string key, CancellationToken ct = default) =>
        ChangeAsync(id, expected, actor, "message.add", key, new { kind, text }, (db, request) =>
        {
            if (kind is not ("clarification" or "assistant-report" or "progress" or "question" or "decision")) throw Invalid("Select a supported message type.");
            request.Messages.Add(new RequestMessage { RequestId = id, Kind = kind, Text = Required(text, "Message", 50000), Author = actor, Source = "reported", CreatedAt = Now });
            return Task.CompletedTask;
        }, ct);

    public Task<MutationReceipt> ArchiveAsync(Guid id, int expected, bool archived, string actor, string key, CancellationToken ct = default) =>
        ChangeAsync(id, expected, actor, "request.archive", key, new { archived }, (db, request) =>
        {
            if (archived && request.WorkItems.Any(w => w.Task.Status == WorkStatus.InProgress)) throw Invalid("Move active work out of In progress before archiving.");
            request.Archived = archived;
            if (request.Idea.Status != IdeaStatus.Promoted)
                request.Idea.Status = archived ? IdeaStatus.Archived : IdeaStatus.Triaging;
            return Task.CompletedTask;
        }, ct, allowArchived: true);

    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private static IQueryable<WorkRequest> Graph(AppDbContext db) => db.WorkRequests.AsSplitQuery()
        .Include(r => r.Idea).Include(r => r.Project).Include(r => r.Messages).Include(r => r.Briefs).Include(r => r.Plans).Include(r => r.Results)
        .Include(r => r.WorkItems).ThenInclude(w => w.Task).ThenInclude(t => t.AcceptanceCriteria)
        .Include(r => r.WorkItems).ThenInclude(w => w.Task).ThenInclude(t => t.Feature)
        .Include(r => r.WorkItems).ThenInclude(w => w.Task).ThenInclude(t => t.Comments)
        .Include(r => r.WorkItems).ThenInclude(w => w.Task).ThenInclude(t => t.Subtasks)
        .Include(r => r.WorkItems).ThenInclude(w => w.Task).ThenInclude(t => t.Dependencies)
        .Include(r => r.WorkItems).ThenInclude(w => w.Evidence);

    private Task<MutationReceipt> ChangeAsync<T>(Guid id, int expected, string actor, string operation, string key, T payload,
        Func<AppDbContext, WorkRequest, Task> change, CancellationToken ct, bool allowArchived = false) =>
        WriteAsync(actor, operation, key, new { id, expected, payload }, async db =>
        {
            var request = await Graph(db).SingleOrDefaultAsync(r => r.Id == id, ct) ?? throw Missing("Request");
            if (request.Version != expected) throw new WorkflowException("conflict", "This request changed in another tab or client. Reload before saving again.");
            if (request.Archived && !allowArchived) throw Invalid("Restore this archived request before editing it.");
            var before = new { request.Id, request.Version, request.ProjectId, request.FeatureId, request.Archived };
            await change(db, request);
            request.Version++;
            request.UpdatedAt = Now;
            Audit(db, request.ProjectId, id, actor, operation, request.IdeaId);
            db.ActivityEvents.Local.Last().BeforeJson = JsonSerializer.Serialize(before, AuditJson);
            return new MutationReceipt(id, request.Version);
        }, ct);

    private async Task<T> WriteAsync<T, TPayload>(string actor, string operation, string key, TPayload payload,
        Func<AppDbContext, Task<T>> write, CancellationToken ct)
    {
        Required(actor, "Actor", 200);
        Required(key, "Operation key", 100);
        var hash = Hash(payload);
        await using var db = await factory.CreateDbContextAsync(ct);
        // Microsoft.Data.Sqlite uses BEGIN IMMEDIATE: graph reads and writes share the writer reservation.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var receipt = await db.WorkflowReceipts.SingleOrDefaultAsync(r => r.Actor == actor && r.Operation == operation && r.Key == key, ct);
        if (receipt is not null)
        {
            if (receipt.PayloadHash != hash) throw new WorkflowException("conflict", "This operation key was already used for different content.");
            return JsonSerializer.Deserialize<T>(receipt.ResultJson)!;
        }
        var result = await write(db);
        foreach (var audit in db.ActivityEvents.Local.Where(e => db.Entry(e).State == EntityState.Added && e.EntityType == "WorkRequest"))
        {
            var original = JsonSerializer.Deserialize<JsonElement>(audit.AfterJson ?? "{}");
            audit.AfterJson = JsonSerializer.Serialize(new { requestId = original.GetProperty("requestId"), operation, payload, result }, AuditJson);
            audit.Message = $"{operation} by {actor}";
        }
        db.WorkflowReceipts.Add(new WorkflowReceipt { Actor = actor, Operation = operation, Key = key, PayloadHash = hash, ResultJson = JsonSerializer.Serialize(result), CreatedAt = Now });
        await db.SaveChangesWithAuditAsync(actor, ct);
        await transaction.CommitAsync(ct);
        return result;
    }

    private void Audit(AppDbContext db, int? projectId, Guid requestId, string actor, string action, int entityId) =>
        db.ActivityEvents.Add(new ActivityEvent { ProjectId = projectId ?? 0, EntityType = "WorkRequest", EntityId = entityId,
            EventType = action, Actor = actor, AfterJson = JsonSerializer.Serialize(new { requestId }), CreatedAt = Now });

    public static string Required(string? value, string field, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) throw Invalid($"{field} is required.");
        return Bounded(value, field, max);
    }
    public static string Bounded(string? value, string field, int max)
    {
        if (value is null || value.Length > max || value.Contains('\0')) throw Invalid($"{field} must contain at most {max} characters and no null bytes.");
        return value;
    }
    private static WorkflowException Missing(string entity) => new("not_found", $"{entity} was not found.");
    private static WorkflowException Invalid(string message) => new("invalid", message);
}
