using System.Data;
using System.Text.Json;
using KanbanFlow.Models;
using KanbanFlow.Models.IdeaWorkspaces;
using KanbanFlow.Services.Workflows;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Services.IdeaWorkspaces;

public sealed partial class IdeaWorkspaceService
{
    private const string CreationReceiptScope = "$create_workspace";

    public async Task<IdeaWorkspaceCreateResult> CreateAsync(IdeaWorkspaceCreate request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateIdentifier(request.OperationId, "OperationId", 200);
        ValidateActor(request.Actor);
        if (request.ProjectId is <= 0)
            throw Invalid("invalid_project", "ProjectId must be a positive integer.");
        if (request.ProjectId is not null && (request.Title is not null || request.Body is not null ||
            request.Plan is not null || request.Tags is not null || request.Priority is not null))
            throw Invalid("source_selection_conflict", "Opening an existing project cannot also edit title, body, plan, tags or priority. Use a workspace command for edits.");
        if (request.ProjectId is null && (string.IsNullOrWhiteSpace(request.Title) || request.Title.Trim().Length > 500))
            throw Invalid("invalid_title", "A new idea needs a nonblank title of at most 500 characters.");
        TaskPriority? priority = null;
        if (request.Priority is not null)
        {
            var name = Enum.GetNames<TaskPriority>().SingleOrDefault(p => p.Equals(request.Priority.Trim(), StringComparison.OrdinalIgnoreCase));
            if (name is null) throw Invalid("invalid_priority", "Priority must be Low, Medium, High or Critical, or null.");
            priority = Enum.Parse<TaskPriority>(name);
        }
        var payload = JsonSerializer.SerializeToElement(new
        {
            request.Title, request.Body, request.Plan, request.Tags, request.Priority, request.ProjectId
        }, JsonOptions);
        var hash = HashCommand(CreationReceiptScope,
            new IdeaWorkspaceCommand(request.OperationId, 0, "create_workspace", payload, request.Actor));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.Database.OpenConnectionAsync(ct);
        if (db.Database.GetDbConnection() is not SqliteConnection connection)
            throw new InvalidOperationException("Idea workspaces require the application's SQLite provider.");
        ct.ThrowIfCancellationRequested();
        try
        {
            await using var sqliteTransaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
            await using var transaction = await db.Database.UseTransactionAsync(sqliteTransaction, ct);
            var prior = await db.Set<IdeaWorkspaceReceipt>().AsNoTracking()
                .SingleOrDefaultAsync(r => r.WorkspaceId == CreationReceiptScope && r.OperationId == request.OperationId, ct);
            if (prior is not null)
            {
                if (prior.RequestHash != hash)
                    throw Conflict("operation_conflict", "This creation operation ID was already used for different content or actor.");
                return Deserialize<IdeaWorkspaceCreateResult>(prior.ResultJson) with { Replayed = true };
            }

            var now = DateTime.UtcNow;
            IdeaWorkspaceKey key;
            IdeaWorkspaceSource source;
            if (request.ProjectId is { } projectId)
            {
                key = new IdeaWorkspaceKey("project", projectId);
                source = await ReadSourceAsync(db, key, ct)
                    ?? throw NotFound("project_not_found", $"Project {projectId} does not exist. Saved missing-source workspaces can still be read by their stable key.");
            }
            else
            {
                // This context owns the source, workspace, audit and receipt transaction. Calling
                // public IdeaService.CreateAsync here would open a separate transaction and trim
                // authored Body/Plan, so perform only the bounded domain creation in this context.
                var idea = new Idea
                {
                    Title = request.Title!.Trim(), Body = request.Body, Plan = request.Plan,
                    Tags = request.Tags, Priority = priority, Status = IdeaStatus.New,
                    CreatedAt = now, UpdatedAt = now
                };
                db.Ideas.Add(idea);
                IdeaWorkflowCapture.Add(db, idea, string.IsNullOrWhiteSpace(request.Body) ? request.Title! : request.Body,
                    request.Actor, "idea-workspace");
                await db.SaveChangesAsync(ct);
                key = new IdeaWorkspaceKey("idea", idea.Id);
                source = new IdeaWorkspaceSource("idea", idea.Id, idea.Title, idea.Body, idea.Plan,
                    null, IdeaStatus.New.ToString(), idea.Tags, idea.Priority?.ToString(), null, now, now);
                db.ActivityEvents.Add(new ActivityEvent
                {
                    ProjectId = 0, EntityType = "Idea", EntityId = idea.Id, Actor = request.Actor,
                    EventType = "IdeaCreated", BeforeJson = null, AfterJson = Serialize(source),
                    Message = "Idea captured through the shared workspace store", CreatedAt = now
                });
            }

            var state = await db.Set<IdeaWorkspaceState>().SingleOrDefaultAsync(s => s.WorkspaceId == key.WorkspaceId, ct);
            var createdWorkspace = state is null;
            ActivityEvent? activity = null;
            if (state is null)
            {
                var document = CreateDocument(source);
                state = new IdeaWorkspaceState
                {
                    WorkspaceId = key.WorkspaceId, Kind = key.Kind, SourceId = key.SourceId,
                    LinkedProjectId = source.ProjectId, Revision = 1,
                    OriginalSourceJson = Serialize(source), DocumentJson = Serialize(document),
                    CreatedAt = now, UpdatedAt = now
                };
                db.Set<IdeaWorkspaceState>().Add(state);
                activity = new ActivityEvent
                {
                    ProjectId = source.ProjectId ?? 0, EntityType = key.AuditEntityType,
                    EntityId = key.SourceId, Actor = request.Actor, EventType = "IdeaWorkspaceCreated",
                    BeforeJson = null, AfterJson = SerializeSnapshot(state, document),
                    Message = "Workspace source snapshot and initial layout saved", CreatedAt = now
                };
                db.ActivityEvents.Add(activity);
                await db.SaveChangesAsync(ct);
            }
            var activityId = activity?.Id ?? await db.ActivityEvents.AsNoTracking()
                .Where(a => a.EntityType == key.AuditEntityType && a.EntityId == key.SourceId)
                .Select(a => (int?)a.Id).MaxAsync(ct) ?? 0;
            var result = new IdeaWorkspaceCreateResult(key.WorkspaceId, state.Revision, request.OperationId,
                key.Kind == "idea" ? key.SourceId : null, source.ProjectId, activityId,
                createdWorkspace, key.Kind == "idea");
            db.Set<IdeaWorkspaceReceipt>().Add(new IdeaWorkspaceReceipt
            {
                WorkspaceId = CreationReceiptScope, OperationId = request.OperationId, RequestHash = hash,
                ResultJson = Serialize(result), Actor = request.Actor, CreatedAt = now
            });
            await db.SaveChangesAsync(ct);
            await sqliteTransaction.CommitAsync(ct);
            return result;
        }
        catch (DbUpdateConcurrencyException)
        {
            throw Conflict("revision_conflict", "The workspace changed during creation. Retry the same operation ID.");
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
        {
            throw Conflict("workspace_busy", "Another writer is active. Retry the same creation operation ID.");
        }
    }
}
