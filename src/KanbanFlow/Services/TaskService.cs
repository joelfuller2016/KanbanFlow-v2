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
    // TODO: extract these helpers (mirror of the private statics in ApiEndpoints.cs)
    // into a shared internal static class once a second service lands. Duplicated here
    // intentionally to keep the blast radius of this PR small — touching ApiEndpoints
    // to extract them would mean updating 14 call sites and ship as a separate concern.
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

    public Task DeleteAsync(int taskId, string? actor, CancellationToken ct = default)
        => throw new NotImplementedException("TODO: implement in follow-up PR. Closes the UI half of #88 for deletes.");
}
