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

    // ── Stubs ──

    public Task<TaskItem> ReassignToFeatureAsync(int taskId, int newFeatureId, string? actor, CancellationToken ct = default)
        => throw new NotImplementedException("TODO: implement in follow-up PR. Closes #83 (cross-project boundary check).");

    public Task<TaskItem> UpdateAsync(int taskId, TaskUpdate dto, string? actor, CancellationToken ct = default)
        => throw new NotImplementedException("TODO: implement in follow-up PR. Owns the PUT /api/tasks/{id} body + UI inline edits.");

    public Task DeleteAsync(int taskId, string? actor, CancellationToken ct = default)
        => throw new NotImplementedException("TODO: implement in follow-up PR. Closes the UI half of #88 for deletes.");
}
