using System.Text.Json;
using KanbanFlow.Data;
using KanbanFlow.Models;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Services;

/// <summary>
/// Default <see cref="ITaskService"/> implementation. Uses
/// <see cref="IDbContextFactory{AppDbContext}"/> so each call gets its own
/// short-lived <see cref="AppDbContext"/> — safe for concurrent Blazor Server
/// circuits and Minimal API requests alike.
/// </summary>
public sealed class TaskService(IDbContextFactory<AppDbContext> dbFactory) : ITaskService
{
    private static readonly JsonSerializerOptions ActivityJsonOptions = new(JsonSerializerDefaults.Web);

    private static object TaskActivitySnapshot(TaskItem task) => new
    {
        task.Id,
        task.FeatureId,
        task.Name,
        task.Description,
        Status = task.Status.ToString(),
        Priority = task.Priority.ToString(),
        task.Assignee,
        task.ClaimedBy,
        task.ClaimedAt,
        task.SortOrder,
        task.CompletedAt
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

    public async Task<TaskItem> ChangeStatusAsync(int taskId, WorkStatus newStatus, string? actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var task = await db.Tasks
            .Include(t => t.Feature)
            .FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null)
            throw new TaskNotFoundException(taskId);
        if (task.Feature is null)
            throw new InvalidOperationException($"Task {taskId} has no feature; cannot resolve project for audit.");

        var oldStatus = task.Status;

        // Idempotent: if no actual change, skip side effects entirely.
        if (oldStatus == newStatus)
            return task;

        var before = TaskActivitySnapshot(task);

        task.Status = newStatus;
        task.UpdatedAt = DateTime.UtcNow;
        if (newStatus == WorkStatus.Done && oldStatus != WorkStatus.Done)
            task.CompletedAt = DateTime.UtcNow;
        else if (newStatus != WorkStatus.Done)
            task.CompletedAt = null;

        db.TaskComments.Add(new TaskComment
        {
            TaskItemId = task.Id,
            Text = $"Status changed from {oldStatus} to {newStatus}",
            Author = string.IsNullOrWhiteSpace(actor) ? "System" : actor!.Trim(),
            Type = CommentType.StatusChange,
            CreatedAt = DateTime.UtcNow
        });

        AddActivityEvent(
            db, task.Feature.ProjectId, "Task", task.Id, actor,
            "TaskStatusChanged", before, TaskActivitySnapshot(task),
            $"Task '{task.Name}' status changed from {oldStatus} to {newStatus}");

        await db.SaveChangesAsync(ct);
        return task;
    }

    public async Task<TaskItem> ToggleDoneAsync(int taskId, string? actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var task = await db.Tasks
            .Include(t => t.Feature)
            .Include(t => t.Subtasks)
            .FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null)
            throw new TaskNotFoundException(taskId);
        if (task.Feature is null)
            throw new InvalidOperationException($"Task {taskId} has no feature; cannot resolve project for audit.");

        var oldStatus = task.Status;
        var newStatus = oldStatus == WorkStatus.Done ? WorkStatus.Todo : WorkStatus.Done;
        var markSubtasksCompleted = newStatus == WorkStatus.Done;

        var before = TaskActivitySnapshot(task);

        task.Status = newStatus;
        task.UpdatedAt = DateTime.UtcNow;
        task.CompletedAt = newStatus == WorkStatus.Done ? DateTime.UtcNow : null;

        foreach (var subtask in task.Subtasks)
        {
            subtask.Completed = markSubtasksCompleted;
        }

        db.TaskComments.Add(new TaskComment
        {
            TaskItemId = task.Id,
            Text = $"Status changed from {oldStatus} to {newStatus}",
            Author = string.IsNullOrWhiteSpace(actor) ? "System" : actor!.Trim(),
            Type = CommentType.StatusChange,
            CreatedAt = DateTime.UtcNow
        });

        AddActivityEvent(
            db, task.Feature.ProjectId, "Task", task.Id, actor,
            "TaskStatusChanged", before, TaskActivitySnapshot(task),
            $"Task '{task.Name}' status changed from {oldStatus} to {newStatus}");

        await db.SaveChangesAsync(ct);
        return task;
    }

    public async Task<TaskItem> ReassignToFeatureAsync(int taskId, int newFeatureId, string? actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var task = await db.Tasks
            .Include(t => t.Feature)
            .FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null)
            throw new TaskNotFoundException(taskId);
        if (task.Feature is null)
            throw new InvalidOperationException($"Task {taskId} has no feature; cannot resolve project for audit.");

        var oldFeatureId = task.FeatureId;
        var oldProjectId = task.Feature.ProjectId;

        // Idempotent: same feature → no audit, no comment, no UpdatedAt churn.
        if (newFeatureId == oldFeatureId)
            return task;

        // Distinct exception (vs TaskNotFoundException) so the API layer can surface
        // a clear "target feature missing" message — the source task DOES exist here.
        var newFeature = await db.Features.FirstOrDefaultAsync(f => f.Id == newFeatureId, ct);
        if (newFeature is null)
            throw new FeatureNotFoundException(newFeatureId);

        // #83: project-boundary enforcement. Reassigning across projects would orphan
        // requirements/dependencies/activity history rooted in the source project.
        if (newFeature.ProjectId != oldProjectId)
            throw new CrossProjectReassignmentException(taskId, oldProjectId, newFeature.ProjectId);

        var before = TaskActivitySnapshot(task);

        task.FeatureId = newFeatureId;
        task.UpdatedAt = DateTime.UtcNow;

        db.TaskComments.Add(new TaskComment
        {
            TaskItemId = task.Id,
            Text = $"Feature changed: was feature {oldFeatureId}, now feature {newFeatureId}",
            Author = string.IsNullOrWhiteSpace(actor) ? "System" : actor!.Trim(),
            Type = CommentType.FeatureChange,
            CreatedAt = DateTime.UtcNow
        });

        AddActivityEvent(
            db, oldProjectId, "Task", task.Id, actor,
            "TaskFeatureChanged", before, TaskActivitySnapshot(task),
            $"Task '{task.Name}' reassigned from feature {oldFeatureId} to feature {newFeatureId}");

        await db.SaveChangesAsync(ct);
        return task;
    }

    public async Task<TaskItem> UpdateAsync(int taskId, TaskUpdate dto, string? actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var task = await db.Tasks
            .Include(t => t.Feature)
            .FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null)
            throw new TaskNotFoundException(taskId);
        if (task.Feature is null)
            throw new InvalidOperationException($"Task {taskId} has no feature; cannot resolve project for audit.");

        // No-op: caller passed nothing to change.
        if (dto.Name is null && dto.Description is null && dto.Status is null && dto.Priority is null && dto.Assignee is null)
            return task;

        // Validate up-front so we never do half a mutation. Service is the trust boundary
        // — every entry point (REST, MCP, future UI direct calls) flows through here.
        if (dto.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(dto.Name))
                throw new ValidationException("Name cannot be empty or whitespace.");
            if (dto.Name.Length > MaxNameLength)
                throw new ValidationException($"Name cannot exceed {MaxNameLength} characters.");
        }
        if (dto.Status is not null && !Enum.IsDefined(dto.Status.Value))
            throw new ValidationException($"Invalid status '{dto.Status.Value}'.");
        if (dto.Priority is not null && !Enum.IsDefined(dto.Priority.Value))
            throw new ValidationException($"Invalid priority '{dto.Priority.Value}'.");

        var before = TaskActivitySnapshot(task);
        var oldStatus = task.Status;
        var changedFields = new List<string>();
        var statusChanged = false;
        WorkStatus newStatus = oldStatus;

        if (dto.Name is not null && task.Name != dto.Name.Trim())
        {
            task.Name = dto.Name.Trim();
            changedFields.Add("Name");
        }

        if (dto.Description is not null)
        {
            var trimmed = string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description.Trim();
            if (task.Description != trimmed)
            {
                task.Description = trimmed;
                changedFields.Add("Description");
            }
        }

        if (dto.Status is not null && dto.Status.Value != task.Status)
        {
            newStatus = dto.Status.Value;
            task.Status = newStatus;
            // Mirror ChangeStatusAsync's CompletedAt invariant so audit history stays consistent.
            if (newStatus == WorkStatus.Done && oldStatus != WorkStatus.Done)
                task.CompletedAt = DateTime.UtcNow;
            else if (newStatus != WorkStatus.Done)
                task.CompletedAt = null;
            changedFields.Add("Status");
            statusChanged = true;
        }

        if (dto.Priority is not null && dto.Priority.Value != task.Priority)
        {
            task.Priority = dto.Priority.Value;
            changedFields.Add("Priority");
        }

        if (dto.Assignee is not null)
        {
            var trimmed = string.IsNullOrWhiteSpace(dto.Assignee) ? null : dto.Assignee.Trim();
            if (task.Assignee != trimmed)
            {
                task.Assignee = trimmed;
                changedFields.Add("Assignee");
            }
        }

        // All proposed values matched current state → idempotent, skip audit.
        if (changedFields.Count == 0)
            return task;

        task.UpdatedAt = DateTime.UtcNow;

        // If status changed in this Update, emit a StatusChange comment + TaskStatusChanged
        // event (matching ChangeStatusAsync) so the audit trail stays consistent regardless
        // of whether the caller used the dedicated status endpoint or the bulk update path.
        // If other fields also changed, emit an additional Note comment for them.
        if (statusChanged)
        {
            db.TaskComments.Add(new TaskComment
            {
                TaskItemId = task.Id,
                Text = $"Status changed from {oldStatus} to {newStatus}",
                Author = string.IsNullOrWhiteSpace(actor) ? "System" : actor!.Trim(),
                Type = CommentType.StatusChange,
                CreatedAt = DateTime.UtcNow
            });
            AddActivityEvent(
                db, task.Feature.ProjectId, "Task", task.Id, actor,
                "TaskStatusChanged", before, TaskActivitySnapshot(task),
                $"Task '{task.Name}' status changed from {oldStatus} to {newStatus}");
        }

        var nonStatusChanges = changedFields.Where(f => f != "Status").ToList();
        if (nonStatusChanges.Count > 0)
        {
            db.TaskComments.Add(new TaskComment
            {
                TaskItemId = task.Id,
                Text = $"Updated: {string.Join(", ", nonStatusChanges)}",
                Author = string.IsNullOrWhiteSpace(actor) ? "System" : actor!.Trim(),
                Type = CommentType.Note,
                CreatedAt = DateTime.UtcNow
            });
            AddActivityEvent(
                db, task.Feature.ProjectId, "Task", task.Id, actor,
                "TaskUpdated", before, TaskActivitySnapshot(task),
                $"Task '{task.Name}' updated: {string.Join(", ", nonStatusChanges)}");
        }

        await db.SaveChangesAsync(ct);
        return task;
    }

    private const int MaxNameLength = 500;

    public async Task<TaskItem> CreateAsync(TaskCreate dto, string? actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dto);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Validate up-front so we never persist a half-built task. Service is the trust
        // boundary — REST, MCP, and Blazor direct-callers all flow through here (#88).
        if (string.IsNullOrWhiteSpace(dto.Name))
            throw new ValidationException("Name cannot be empty or whitespace.");
        if (dto.Name.Length > MaxNameLength)
            throw new ValidationException($"Name cannot exceed {MaxNameLength} characters.");
        // Defensive enum guard (matches UpdateAsync): rejects undefined/out-of-range
        // values such as a cast numeric the caller failed to validate.
        if (dto.Status is not null && !Enum.IsDefined(dto.Status.Value))
            throw new ValidationException($"Invalid status '{dto.Status.Value}'.");
        if (dto.Priority is not null && !Enum.IsDefined(dto.Priority.Value))
            throw new ValidationException($"Invalid priority '{dto.Priority.Value}'.");

        // Resolve the project for audit attribution; a missing feature is a distinct,
        // actionable error (the task itself does not exist yet).
        var projectId = await db.Features
            .Where(f => f.Id == dto.FeatureId)
            .Select(f => (int?)f.ProjectId)
            .SingleOrDefaultAsync(ct);
        if (projectId is null)
            throw new FeatureNotFoundException(dto.FeatureId);

        var status = dto.Status ?? WorkStatus.Backlog;
        var priority = dto.Priority ?? TaskPriority.Low;

        var maxSort = await db.Tasks
            .Where(t => t.FeatureId == dto.FeatureId)
            .MaxAsync(t => (int?)t.SortOrder, ct) ?? -1;

        var task = new TaskItem
        {
            FeatureId = dto.FeatureId,
            Name = dto.Name.Trim(),
            Description = string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description.Trim(),
            Status = status,
            Priority = priority,
            Assignee = string.IsNullOrWhiteSpace(dto.Assignee) ? null : dto.Assignee.Trim(),
            SortOrder = dto.SortOrder ?? maxSort + 1,
            CompletedAt = status == WorkStatus.Done ? DateTime.UtcNow : null
        };

        // Atomic create + audit: the first save assigns task.Id (needed by the comment +
        // activity event), the second persists those audit rows. Wrap both in one
        // transaction so a failure can't leave a task without its Created comment /
        // TaskCreated event. EF Core + SQLite supports explicit transactions.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        db.Tasks.Add(task);
        // First save assigns task.Id so the comment + activity event can reference it.
        await db.SaveChangesAsync(ct);

        db.TaskComments.Add(new TaskComment
        {
            TaskItemId = task.Id,
            Text = "Task created",
            // Inline REST hardcoded "System"; honor the actor here for parity with the
            // other audit-aware methods (R6). The Created comment is audit-internal.
            Author = string.IsNullOrWhiteSpace(actor) ? "System" : actor!.Trim(),
            Type = CommentType.Created,
            CreatedAt = DateTime.UtcNow
        });

        AddActivityEvent(
            db, projectId.Value, "Task", task.Id, actor,
            "TaskCreated", null, TaskActivitySnapshot(task),
            $"Task '{task.Name}' created");

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return task;
    }

    public async Task DeleteAsync(int taskId, string? actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var task = await db.Tasks
            .Include(t => t.Feature)
            .FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null)
            throw new TaskNotFoundException(taskId);
        if (task.Feature is null)
            throw new InvalidOperationException($"Task {taskId} has no feature; cannot resolve project for audit.");

        // Snapshot + emit the audit event BEFORE removal. ActivityEvent has no FK to the
        // task (intentional — audit history must survive deletion), so the row persists.
        AddActivityEvent(
            db, task.Feature.ProjectId, "Task", task.Id, actor,
            "TaskDeleted", TaskActivitySnapshot(task), null,
            $"Task '{task.Name}' deleted");

        // FK-safe cascade order (R1): Subtask.ChildSubtasks is OnDelete(Restrict)
        // (self-ref), so subtasks MUST be removed leaf-first. Acceptance criteria are
        // removed explicitly. Comments + Dependencies + BlockingDependencies are
        // OnDelete(Cascade) and Requirement.TaskItemId is OnDelete(SetNull) — EF handles
        // those on Remove(task). Leaf-first subtask removal lives in ONE place
        // (ProjectGraphDeleter), shared with the project/feature cascade.
        await ProjectGraphDeleter.DeleteSubtasksForTaskAsync(db, taskId, ct);

        var criteria = await db.AcceptanceCriteria
            .Where(c => c.TaskItemId == taskId)
            .ToListAsync(ct);
        db.AcceptanceCriteria.RemoveRange(criteria);

        db.Tasks.Remove(task);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteProjectAsync(int projectId, string? actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null)
            throw new ProjectNotFoundException(projectId);

        // Snapshot + emit the audit event BEFORE removal. ActivityEvent has no FK to the
        // project (Project is Ignored in AppDbContext), so the row survives the delete.
        AddActivityEvent(
            db, projectId, "Project", projectId, actor,
            "ProjectDeleted", ProjectActivitySnapshot(project), null,
            $"Project '{project.Name}' deleted");

        // FK-safe child cleanup (features -> tasks -> leaf-first subtasks + acceptance criteria
        // + comments; requirement links nulled) — shared with the REST delete path via the
        // service-layer ProjectGraphDeleter so both surfaces behave identically (#155). A single
        // SaveChanges makes the children removal, the project removal, and the audit row one
        // atomic unit.
        await ProjectGraphDeleter.DeleteProjectChildrenAsync(db, projectId, ct);
        db.Projects.Remove(project);
        await db.SaveChangesAsync(ct);
    }

    private static object ProjectActivitySnapshot(Project project) => new
    {
        project.Id,
        project.Name,
        project.Description,
        project.Goals
    };

    public async Task<TaskItem> ClaimAsync(int taskId, string actor, CancellationToken ct = default)
    {
        // The REST endpoint maps a blank actor to 422; surface it as a ValidationException.
        if (string.IsNullOrWhiteSpace(actor))
            throw new ValidationException("Actor is required.");
        var trimmedActor = actor.Trim();

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // #195: AsNoTracking — the claim is persisted via the conditional ExecuteUpdateAsync
        // below, so the loaded entity is only for validation + the audit snapshot. Not tracking
        // it stops SaveChanges from emitting a redundant Tasks UPDATE next to the event insert.
        var task = await db.Tasks
            .Include(t => t.Feature)
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null)
            throw new TaskNotFoundException(taskId);
        if (task.Feature is null)
            throw new InvalidOperationException($"Task {taskId} has no feature; cannot resolve project for audit.");

        // Claimed by someone else → conflict (API → 409). Same actor is idempotent.
        if (!string.IsNullOrWhiteSpace(task.ClaimedBy)
            && !string.Equals(task.ClaimedBy, trimmedActor, StringComparison.OrdinalIgnoreCase))
        {
            throw new TaskClaimConflictException(taskId, task.ClaimedBy, task.ClaimedAt);
        }

        // Idempotent re-claim by the same actor: no audit/UpdatedAt churn.
        if (string.Equals(task.ClaimedBy, trimmedActor, StringComparison.OrdinalIgnoreCase))
            return task;

        var before = TaskActivitySnapshot(task);
        var now = DateTime.UtcNow;

        // Atomic claim + audit (#156): wrap the conditional UPDATE and the TaskClaimed event in
        // one transaction so a claim can never persist without its audit event. The race-safe
        // conditional UPDATE still works inside the transaction (previously it auto-committed,
        // leaving a window where the claim survived but the audit SaveChanges could fail).
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Race-safe claim (R4): a conditional UPDATE that only fires while the row is still
        // unclaimed. Under concurrent claimers (multiple agents), exactly one UPDATE matches
        // (rows == 1); the loser sees rows == 0 and is reconciled below. This closes the
        // load-check-write gap without a rowversion column (schema uses EnsureCreated, not migrations).
        var claimed = await db.Tasks
            .Where(t => t.Id == taskId && t.ClaimedBy == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.ClaimedBy, trimmedActor)
                .SetProperty(t => t.ClaimedAt, now)
                .SetProperty(t => t.UpdatedAt, now), ct);

        if (claimed == 0)
        {
            // Another request claimed the task between our check and our write. Re-read the
            // current owner: same actor ⇒ idempotent success, otherwise a genuine 409 conflict.
            var current = await db.Tasks.AsNoTracking()
                .Where(t => t.Id == taskId)
                .Select(t => new { t.ClaimedBy, t.ClaimedAt, t.UpdatedAt })
                .FirstOrDefaultAsync(ct)
                ?? throw new TaskNotFoundException(taskId);
            if (string.Equals(current.ClaimedBy, trimmedActor, StringComparison.OrdinalIgnoreCase))
            {
                // Reconcile the stale tracked entity with the concurrent winner's values.
                task.ClaimedBy = current.ClaimedBy;
                task.ClaimedAt = current.ClaimedAt;
                task.UpdatedAt = current.UpdatedAt;
                return task;
            }
            throw new TaskClaimConflictException(taskId, current.ClaimedBy!, current.ClaimedAt);
        }

        // We won the claim. Mirror the persisted values onto the tracked entity so the audit
        // snapshot + return value are accurate, then emit the TaskClaimed event.
        task.ClaimedBy = trimmedActor;
        task.ClaimedAt = now;
        task.UpdatedAt = now;

        AddActivityEvent(
            db, task.Feature.ProjectId, "Task", task.Id, trimmedActor,
            "TaskClaimed", before, TaskActivitySnapshot(task),
            $"Task '{task.Name}' claimed by {trimmedActor}");

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return task;
    }

    public async Task<TaskItem> ReleaseAsync(int taskId, string actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actor))
            throw new ValidationException("Actor is required.");
        var trimmedActor = actor.Trim();

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // #195: AsNoTracking — release is persisted via the conditional ExecuteUpdateAsync below;
        // not tracking the loaded entity stops SaveChanges from emitting a redundant Tasks UPDATE.
        var task = await db.Tasks
            .Include(t => t.Feature)
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null)
            throw new TaskNotFoundException(taskId);
        if (task.Feature is null)
            throw new InvalidOperationException($"Task {taskId} has no feature; cannot resolve project for audit.");

        // Only the claiming actor may release; anyone else → conflict (API → 409).
        if (!string.IsNullOrWhiteSpace(task.ClaimedBy)
            && !string.Equals(task.ClaimedBy, trimmedActor, StringComparison.OrdinalIgnoreCase))
        {
            throw new TaskClaimConflictException(taskId, task.ClaimedBy, task.ClaimedAt);
        }

        // Idempotent: already unclaimed → nothing to do, no audit churn.
        if (string.IsNullOrWhiteSpace(task.ClaimedBy))
            return task;

        var before = TaskActivitySnapshot(task);
        var now = DateTime.UtcNow;

        // Atomic release + audit (#156): wrap the conditional UPDATE and the TaskReleased event
        // in one transaction so a release can never persist without its audit event.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Race-safe release (R4): conditional UPDATE that only fires while THIS actor still
        // holds the claim. If the state changed underneath us, rows == 0 → reconcile below.
        var released = await db.Tasks
            .Where(t => t.Id == taskId && t.ClaimedBy != null
                        && t.ClaimedBy.ToLower() == trimmedActor.ToLower())
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.ClaimedBy, (string?)null)
                .SetProperty(t => t.ClaimedAt, (DateTime?)null)
                .SetProperty(t => t.UpdatedAt, now), ct);

        if (released == 0)
        {
            // State changed between check and write. Re-read: still claimed by another actor
            // ⇒ 409 conflict; otherwise it is already released ⇒ idempotent success.
            var current = await db.Tasks.AsNoTracking()
                .Where(t => t.Id == taskId)
                .Select(t => new { t.ClaimedBy, t.ClaimedAt, t.UpdatedAt })
                .FirstOrDefaultAsync(ct)
                ?? throw new TaskNotFoundException(taskId);
            if (!string.IsNullOrWhiteSpace(current.ClaimedBy)
                && !string.Equals(current.ClaimedBy, trimmedActor, StringComparison.OrdinalIgnoreCase))
                throw new TaskClaimConflictException(taskId, current.ClaimedBy!, current.ClaimedAt);
            // Reconcile the stale tracked entity with the already-released DB state.
            task.ClaimedBy = current.ClaimedBy;
            task.ClaimedAt = current.ClaimedAt;
            task.UpdatedAt = current.UpdatedAt;
            return task;
        }

        // Mirror onto the tracked entity for an accurate audit snapshot + return value.
        task.ClaimedBy = null;
        task.ClaimedAt = null;
        task.UpdatedAt = now;

        AddActivityEvent(
            db, task.Feature.ProjectId, "Task", task.Id, trimmedActor,
            "TaskReleased", before, TaskActivitySnapshot(task),
            $"Task '{task.Name}' released by {trimmedActor}");

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return task;
    }

    public async Task<TaskComment> AddCommentAsync(int taskId, string text, string? author, CommentType type, string? actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Resolve project (and prove the task exists) in one round-trip.
        var projectId = await db.Tasks
            .Where(t => t.Id == taskId)
            .Select(t => (int?)t.Feature.ProjectId)
            .SingleOrDefaultAsync(ct);
        if (projectId is null)
            throw new TaskNotFoundException(taskId);

        if (string.IsNullOrWhiteSpace(text))
            throw new ValidationException("Text cannot be empty or whitespace.");

        // Only user-facing comment types may be authored directly; system types
        // (StatusChange/Created/FeatureChange/…) are emitted by the service itself.
        if (!IsUserCommentType(type))
            throw new ValidationException(
                $"Invalid comment type '{type}'. Allowed values: {string.Join(", ", AllowedUserCommentTypes)}.");

        var comment = new TaskComment
        {
            TaskItemId = taskId,
            Text = text.Trim(),
            Author = string.IsNullOrWhiteSpace(author) ? null : author.Trim(),
            Type = type,
            CreatedAt = DateTime.UtcNow
        };

        db.TaskComments.Add(comment);
        AddActivityEvent(
            db, projectId.Value, "Task", taskId, actor,
            "TaskCommentAdded", null, new { comment.Text, comment.Author }, "Comment added");

        await db.SaveChangesAsync(ct);
        return comment;
    }

    // ── Comment-type policy (single source of truth) ──
    // Canonical set + order of user-authorable CommentTypes. Edge layers (REST, MCP) defer
    // to IsUserCommentType for validation and reference THIS list for their 422 message body,
    // so there is exactly one definition (R3).
    public static readonly string[] AllowedUserCommentTypes =
    [
        nameof(CommentType.Note),
        nameof(CommentType.Decision),
        nameof(CommentType.Question),
        nameof(CommentType.Blocked),
        nameof(CommentType.Handoff),
        nameof(CommentType.Verification),
        nameof(CommentType.Comment)
    ];

    public bool IsUserCommentType(CommentType type) =>
        type switch
        {
            CommentType.Note => true,
            CommentType.Decision => true,
            CommentType.Question => true,
            CommentType.Blocked => true,
            CommentType.Handoff => true,
            CommentType.Verification => true,
            CommentType.Comment => true,
            _ => false
        };

}
