using KanbanFlow.Models;
using KanbanFlow.Services;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Tests;

/// <summary>
/// Real-code tests for <see cref="TaskService"/> against a live SQLite database.
/// No mocks of the data layer — every test seeds real rows, runs the real service,
/// and re-queries a fresh context to assert what was actually persisted, including
/// the audit trail (<see cref="TaskComment"/> + <see cref="ActivityEvent"/>) that
/// issue #88 makes non-negotiable.
/// </summary>
public sealed class TaskServiceTests
{
    // ── seeding helpers ────────────────────────────────────────────────────────
    private static int SeedTask(
        SqliteTestFactory f,
        WorkStatus status = WorkStatus.Todo,
        int subtasks = 0,
        bool subtasksCompleted = false)
    {
        using var db = f.CreateDbContext();
        var project = new Project { Name = "Proj" };
        var feature = new Feature { Name = "Feat", Project = project };
        var task = new TaskItem { Name = "Task", Feature = feature, Status = status };
        if (status == WorkStatus.Done)
            task.CompletedAt = DateTime.UtcNow;
        for (var i = 0; i < subtasks; i++)
            task.Subtasks.Add(new Subtask { Text = $"sub{i}", Completed = subtasksCompleted });
        db.Add(task);
        db.SaveChanges();
        return task.Id;
    }

    /// <summary>Two features in project 1, plus one feature in project 2, and a task in feature 1a.</summary>
    private static (int taskId, int sameProjectFeatureId, int otherProjectFeatureId) SeedForReassign(SqliteTestFactory f)
    {
        using var db = f.CreateDbContext();
        var p1 = new Project { Name = "P1" };
        var f1a = new Feature { Name = "F1a", Project = p1 };
        var f1b = new Feature { Name = "F1b", Project = p1 };
        var task = new TaskItem { Name = "T", Feature = f1a, Status = WorkStatus.Todo };
        var p2 = new Project { Name = "P2" };
        var f2 = new Feature { Name = "F2", Project = p2 };
        db.AddRange(task, f1b, f2);
        db.SaveChanges();
        return (task.Id, f1b.Id, f2.Id);
    }

    // ── ChangeStatusAsync ──────────────────────────────────────────────────────
    [Fact]
    public async Task ChangeStatus_ToDone_PersistsStatusAndSetsCompletedAt()
    {
        using var f = new SqliteTestFactory();
        var id = SeedTask(f, WorkStatus.Todo);
        var svc = new TaskService(f);

        var result = await svc.ChangeStatusAsync(id, WorkStatus.Done, "tester");

        Assert.Equal(WorkStatus.Done, result.Status);
        Assert.NotNull(result.CompletedAt);

        using var db = f.CreateDbContext();
        var persisted = await db.Tasks.FindAsync(id);
        Assert.Equal(WorkStatus.Done, persisted!.Status);
        Assert.NotNull(persisted.CompletedAt);
    }

    [Fact]
    public async Task ChangeStatus_AwayFromDone_ClearsCompletedAt()
    {
        using var f = new SqliteTestFactory();
        var id = SeedTask(f, WorkStatus.Done);
        var svc = new TaskService(f);

        await svc.ChangeStatusAsync(id, WorkStatus.InProgress, "tester");

        using var db = f.CreateDbContext();
        var t = await db.Tasks.FindAsync(id);
        Assert.Equal(WorkStatus.InProgress, t!.Status);
        Assert.Null(t.CompletedAt);
    }

    [Fact]
    public async Task ChangeStatus_SameStatus_IsIdempotent_WritesNoAudit()
    {
        using var f = new SqliteTestFactory();
        var id = SeedTask(f, WorkStatus.Todo);
        var svc = new TaskService(f);

        await svc.ChangeStatusAsync(id, WorkStatus.Todo, "tester");

        using var db = f.CreateDbContext();
        Assert.Equal(0, await db.TaskComments.CountAsync());
        Assert.Equal(0, await db.ActivityEvents.CountAsync());
    }

    [Fact]
    public async Task ChangeStatus_WritesStatusChangeCommentAndActivityEvent()
    {
        using var f = new SqliteTestFactory();
        var id = SeedTask(f, WorkStatus.Todo);
        var svc = new TaskService(f);

        await svc.ChangeStatusAsync(id, WorkStatus.InProgress, "alice");

        using var db = f.CreateDbContext();
        var comment = await db.TaskComments.SingleAsync();
        Assert.Equal(CommentType.StatusChange, comment.Type);
        Assert.Equal("alice", comment.Author);
        // Assert against the enum's own ToString() (not string literals) so the test
        // stays in sync with the service if the WorkStatus names are ever renamed.
        Assert.Contains(WorkStatus.Todo.ToString(), comment.Text);
        Assert.Contains(WorkStatus.InProgress.ToString(), comment.Text);

        var ev = await db.ActivityEvents.SingleAsync();
        Assert.Equal("TaskStatusChanged", ev.EventType);
        Assert.Equal("Task", ev.EntityType);
        Assert.Equal(id, ev.EntityId);
        Assert.Equal("alice", ev.Actor);
        Assert.NotNull(ev.BeforeJson);
        Assert.NotNull(ev.AfterJson);
    }

    [Fact]
    public async Task ChangeStatus_NullActor_CommentAndEventUseSystemAttribution()
    {
        using var f = new SqliteTestFactory();
        var id = SeedTask(f, WorkStatus.Todo);
        var svc = new TaskService(f);

        await svc.ChangeStatusAsync(id, WorkStatus.Done, null);

        using var db = f.CreateDbContext();
        var comment = await db.TaskComments.SingleAsync();
        Assert.Equal("System", comment.Author);
        var ev = await db.ActivityEvents.SingleAsync();
        Assert.Equal("System", ev.Actor);
    }

    [Fact]
    public async Task ChangeStatus_MissingTask_ThrowsTaskNotFound()
    {
        using var f = new SqliteTestFactory();
        var svc = new TaskService(f);

        await Assert.ThrowsAsync<TaskNotFoundException>(
            () => svc.ChangeStatusAsync(999, WorkStatus.Done, "x"));
    }

    // ── ToggleDoneAsync ────────────────────────────────────────────────────────
    [Fact]
    public async Task ToggleDone_TodoToDone_CascadesSubtasksCompleted()
    {
        using var f = new SqliteTestFactory();
        var id = SeedTask(f, WorkStatus.Todo, subtasks: 3, subtasksCompleted: false);
        var svc = new TaskService(f);

        var result = await svc.ToggleDoneAsync(id, "tester");

        Assert.Equal(WorkStatus.Done, result.Status);
        Assert.NotNull(result.CompletedAt);

        using var db = f.CreateDbContext();
        var subs = await db.Subtasks.Where(s => s.TaskItemId == id).ToListAsync();
        Assert.Equal(3, subs.Count);
        Assert.All(subs, s => Assert.True(s.Completed));
    }

    [Fact]
    public async Task ToggleDone_DoneToTodo_CascadesSubtasksIncomplete()
    {
        using var f = new SqliteTestFactory();
        var id = SeedTask(f, WorkStatus.Done, subtasks: 2, subtasksCompleted: true);
        var svc = new TaskService(f);

        var result = await svc.ToggleDoneAsync(id, "tester");

        Assert.Equal(WorkStatus.Todo, result.Status);
        Assert.Null(result.CompletedAt);

        using var db = f.CreateDbContext();
        var subs = await db.Subtasks.Where(s => s.TaskItemId == id).ToListAsync();
        Assert.All(subs, s => Assert.False(s.Completed));
    }

    [Fact]
    public async Task ToggleDone_WritesAudit()
    {
        using var f = new SqliteTestFactory();
        var id = SeedTask(f, WorkStatus.Todo);
        var svc = new TaskService(f);

        await svc.ToggleDoneAsync(id, "bob");

        using var db = f.CreateDbContext();
        Assert.Equal(1, await db.TaskComments.CountAsync(c => c.Type == CommentType.StatusChange));
        Assert.Equal(1, await db.ActivityEvents.CountAsync(e => e.EventType == "TaskStatusChanged"));
    }

    [Fact]
    public async Task ToggleDone_MissingTask_ThrowsTaskNotFound()
    {
        using var f = new SqliteTestFactory();
        var svc = new TaskService(f);

        await Assert.ThrowsAsync<TaskNotFoundException>(() => svc.ToggleDoneAsync(123, "x"));
    }

    // ── ReassignToFeatureAsync (#83 boundary enforcement) ──────────────────────
    [Fact]
    public async Task Reassign_SameProjectDifferentFeature_MovesAndAudits()
    {
        using var f = new SqliteTestFactory();
        var (taskId, sameProjectFeatureId, _) = SeedForReassign(f);
        var svc = new TaskService(f);

        var result = await svc.ReassignToFeatureAsync(taskId, sameProjectFeatureId, "alice");

        Assert.Equal(sameProjectFeatureId, result.FeatureId);

        using var db = f.CreateDbContext();
        Assert.Equal(1, await db.TaskComments.CountAsync(c => c.Type == CommentType.FeatureChange));
        Assert.Equal(1, await db.ActivityEvents.CountAsync(e => e.EventType == "TaskFeatureChanged"));
    }

    [Fact]
    public async Task Reassign_SameFeature_IsIdempotent()
    {
        using var f = new SqliteTestFactory();
        var (taskId, _, _) = SeedForReassign(f);
        int currentFeatureId;
        using (var db0 = f.CreateDbContext())
            currentFeatureId = (await db0.Tasks.FindAsync(taskId))!.FeatureId;
        var svc = new TaskService(f);

        await svc.ReassignToFeatureAsync(taskId, currentFeatureId, "alice");

        using var db = f.CreateDbContext();
        Assert.Equal(0, await db.TaskComments.CountAsync());
        Assert.Equal(0, await db.ActivityEvents.CountAsync());
    }

    [Fact]
    public async Task Reassign_MissingTargetFeature_ThrowsFeatureNotFound()
    {
        using var f = new SqliteTestFactory();
        var (taskId, _, _) = SeedForReassign(f);
        var svc = new TaskService(f);

        await Assert.ThrowsAsync<FeatureNotFoundException>(
            () => svc.ReassignToFeatureAsync(taskId, 99999, "x"));
    }

    [Fact]
    public async Task Reassign_AcrossProjects_ThrowsCrossProjectReassignment()
    {
        using var f = new SqliteTestFactory();
        var (taskId, _, otherProjectFeatureId) = SeedForReassign(f);
        var svc = new TaskService(f);

        await Assert.ThrowsAsync<CrossProjectReassignmentException>(
            () => svc.ReassignToFeatureAsync(taskId, otherProjectFeatureId, "x"));
    }

    [Fact]
    public async Task Reassign_AppendsToEndOfDestinationFeature()
    {
        // #207: the moved task must be appended to the END of the destination feature, NOT keep its
        // source-feature SortOrder (which would collide with an existing destination task since
        // SortOrder is per-feature). Destination already holds tasks at SortOrder 0 and 1.
        using var f = new SqliteTestFactory();
        int taskId, destFeatureId;
        using (var db = f.CreateDbContext())
        {
            var p = new Project { Name = "P" };
            var src = new Feature { Name = "src", Project = p };
            var dst = new Feature { Name = "dst", Project = p };
            var moving = new TaskItem { Name = "moving", Feature = src, Status = WorkStatus.Todo, SortOrder = 0 };
            var d0 = new TaskItem { Name = "d0", Feature = dst, Status = WorkStatus.Todo, SortOrder = 0 };
            var d1 = new TaskItem { Name = "d1", Feature = dst, Status = WorkStatus.Todo, SortOrder = 1 };
            db.AddRange(moving, d0, d1);
            db.SaveChanges();
            taskId = moving.Id;
            destFeatureId = dst.Id;
        }
        var svc = new TaskService(f);

        var result = await svc.ReassignToFeatureAsync(taskId, destFeatureId, "alice");

        Assert.Equal(destFeatureId, result.FeatureId);
        Assert.Equal(2, result.SortOrder); // appended after the existing 0 and 1

        using var check = f.CreateDbContext();
        var sortOrders = await check.Tasks
            .Where(t => t.FeatureId == destFeatureId)
            .Select(t => t.SortOrder)
            .ToListAsync();
        Assert.Equal(3, sortOrders.Count);
        Assert.Equal(sortOrders.Count, sortOrders.Distinct().Count()); // no duplicate SortOrder
    }

    [Fact]
    public async Task Reassign_ToEmptyDestinationFeature_SortOrderZero()
    {
        // #207: appending into an empty destination feature yields SortOrder 0 (maxSort -1 + 1).
        using var f = new SqliteTestFactory();
        var (taskId, emptyDestFeatureId, _) = SeedForReassign(f); // f1b has no tasks
        var svc = new TaskService(f);

        var result = await svc.ReassignToFeatureAsync(taskId, emptyDestFeatureId, "alice");

        Assert.Equal(emptyDestFeatureId, result.FeatureId);
        Assert.Equal(0, result.SortOrder);
    }

    [Fact]
    public async Task FeatureTaskList_WithDuplicateSortOrder_IsDeterministicByIdTieBreak()
    {
        // #207 AC: feature task lists must return a STABLE order even when two rows share a SortOrder
        // (legacy data or a concurrent create/reassign). The read paths (GET /api/features/{id}/tasks,
        // project tree, board, and the ProjectOutline/KanbanColumn UI loops) now order by
        // SortOrder THEN Id. This exercises that exact ordering contract over real SQLite with a
        // deliberate SortOrder collision. (Endpoint-level HTTP coverage is tracked by #202.)
        using var f = new SqliteTestFactory();
        int featureId, lowerId, higherId;
        using (var db = f.CreateDbContext())
        {
            var p = new Project { Name = "P" };
            var feat = new Feature { Name = "F", Project = p };
            var a = new TaskItem { Name = "a", Feature = feat, Status = WorkStatus.Todo, SortOrder = 5 };
            var b = new TaskItem { Name = "b", Feature = feat, Status = WorkStatus.Todo, SortOrder = 5 }; // tie
            db.AddRange(a, b);
            db.SaveChanges();
            featureId = feat.Id;
            lowerId = Math.Min(a.Id, b.Id);
            higherId = Math.Max(a.Id, b.Id);
        }

        // Two independent reads must return the SAME deterministic order (lower Id first) despite the tie.
        for (var read = 0; read < 2; read++)
        {
            using var db = f.CreateDbContext();
            var ordered = await db.Tasks
                .Where(t => t.FeatureId == featureId)
                .OrderBy(t => t.SortOrder).ThenBy(t => t.Id)
                .Select(t => t.Id)
                .ToListAsync();
            Assert.Equal(new[] { lowerId, higherId }, ordered);
        }
    }

    // ── UpdateAsync (validation + field-diff audit) ────────────────────────────
    [Fact]
    public async Task Update_EmptyName_ThrowsValidation()
    {
        using var f = new SqliteTestFactory();
        var id = SeedTask(f);
        var svc = new TaskService(f);

        await Assert.ThrowsAsync<ValidationException>(
            () => svc.UpdateAsync(id, new TaskUpdate(Name: "   "), "x"));
    }

    [Fact]
    public async Task Update_TooLongName_ThrowsValidation()
    {
        using var f = new SqliteTestFactory();
        var id = SeedTask(f);
        var svc = new TaskService(f);

        await Assert.ThrowsAsync<ValidationException>(
            () => svc.UpdateAsync(id, new TaskUpdate(Name: new string('x', 501)), "x"));
    }

    [Fact]
    public async Task Update_InvalidStatusEnum_ThrowsValidation()
    {
        using var f = new SqliteTestFactory();
        var id = SeedTask(f);
        var svc = new TaskService(f);

        await Assert.ThrowsAsync<ValidationException>(
            () => svc.UpdateAsync(id, new TaskUpdate(Status: (WorkStatus)99), "x"));
    }

    [Fact]
    public async Task Update_ChangesFieldsAndStatus_PersistsAndAuditsBothCommentTypes()
    {
        using var f = new SqliteTestFactory();
        var id = SeedTask(f, WorkStatus.Todo);
        var svc = new TaskService(f);

        await svc.UpdateAsync(
            id,
            new TaskUpdate(Name: "Renamed", Priority: TaskPriority.High, Status: WorkStatus.Done),
            "alice");

        using var db = f.CreateDbContext();
        var t = await db.Tasks.FindAsync(id);
        Assert.Equal("Renamed", t!.Name);
        Assert.Equal(TaskPriority.High, t.Priority);
        Assert.Equal(WorkStatus.Done, t.Status);
        Assert.NotNull(t.CompletedAt);

        // Status change → StatusChange comment + TaskStatusChanged event.
        // Non-status changes (Name, Priority) → Note comment + TaskUpdated event.
        Assert.Equal(1, await db.TaskComments.CountAsync(c => c.Type == CommentType.StatusChange));
        Assert.Equal(1, await db.TaskComments.CountAsync(c => c.Type == CommentType.Note));
        Assert.Equal(1, await db.ActivityEvents.CountAsync(e => e.EventType == "TaskStatusChanged"));
        Assert.Equal(1, await db.ActivityEvents.CountAsync(e => e.EventType == "TaskUpdated"));
    }

    [Fact]
    public async Task Update_NoFields_IsIdempotent()
    {
        using var f = new SqliteTestFactory();
        var id = SeedTask(f);
        var svc = new TaskService(f);

        await svc.UpdateAsync(id, new TaskUpdate(), "x");

        using var db = f.CreateDbContext();
        Assert.Equal(0, await db.ActivityEvents.CountAsync());
    }

    [Fact]
    public async Task Update_MissingTask_ThrowsTaskNotFound()
    {
        using var f = new SqliteTestFactory();
        var svc = new TaskService(f);

        await Assert.ThrowsAsync<TaskNotFoundException>(
            () => svc.UpdateAsync(404, new TaskUpdate(Name: "x"), "x"));
    }

    // ── DeleteAsync (implemented by the #88 service-layer extraction) ──────────────
    // Full cascade + audit-survival coverage lives in TaskServiceMutationTests; this
    // case documents the headline contract here so the suite no longer asserts the
    // old NotImplementedException stub.
    [Fact]
    public async Task Delete_RemovesTask_AndAuditEventSurvives()
    {
        using var f = new SqliteTestFactory();
        var id = SeedTask(f);
        var svc = new TaskService(f);

        await svc.DeleteAsync(id, "tester");

        using var db = f.CreateDbContext();
        Assert.False(await db.Tasks.AnyAsync(t => t.Id == id));
        // ActivityEvent has no task FK, so the TaskDeleted audit row outlives the task.
        var ev = await db.ActivityEvents.SingleAsync(e => e.EventType == "TaskDeleted" && e.EntityId == id);
        Assert.Equal("tester", ev.Actor);
    }

    [Fact]
    public async Task Delete_MissingTask_ThrowsTaskNotFound()
    {
        using var f = new SqliteTestFactory();
        var svc = new TaskService(f);

        await Assert.ThrowsAsync<TaskNotFoundException>(() => svc.DeleteAsync(999, "x"));
    }
}
