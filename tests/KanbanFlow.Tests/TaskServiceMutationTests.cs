using KanbanFlow.Models;
using KanbanFlow.Services;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Tests;

/// <summary>
/// Real-code tests for the task/comment mutation surface added by the service-layer
/// extraction (issue #88): <see cref="TaskService.CreateAsync"/>,
/// <see cref="TaskService.DeleteAsync"/>, <see cref="TaskService.ClaimAsync"/>,
/// <see cref="TaskService.ReleaseAsync"/>, and <see cref="TaskService.AddCommentAsync"/>.
///
/// Same contract as <see cref="TaskServiceTests"/>: no data-layer mocks. Every test
/// seeds real rows against a live SQLite database (<see cref="SqliteTestFactory"/>),
/// runs the real service, and re-queries a fresh context to assert what was actually
/// persisted — including the audit trail (<see cref="TaskComment"/> +
/// <see cref="ActivityEvent"/>) that #88 makes non-negotiable.
/// </summary>
public sealed class TaskServiceMutationTests
{
    // ── seeding helpers ────────────────────────────────────────────────────────

    /// <summary>Seeds Project → Feature and returns the feature id (no task).</summary>
    private static int SeedFeature(SqliteTestFactory f)
    {
        using var db = f.CreateDbContext();
        var feature = new Feature { Name = "Feat", Project = new Project { Name = "Proj" } };
        db.Add(feature);
        db.SaveChanges();
        return feature.Id;
    }

    /// <summary>Seeds Project → Feature → Task and returns both ids.</summary>
    private static (int featureId, int taskId) SeedFeatureWithTask(
        SqliteTestFactory f, string? claimedBy = null)
    {
        using var db = f.CreateDbContext();
        var feature = new Feature { Name = "Feat", Project = new Project { Name = "Proj" } };
        var task = new TaskItem { Name = "Task", Feature = feature, Status = WorkStatus.Todo };
        if (claimedBy is not null)
        {
            task.ClaimedBy = claimedBy;
            task.ClaimedAt = DateTime.UtcNow;
        }
        db.Add(task);
        db.SaveChanges();
        return (feature.Id, task.Id);
    }

    // ── CreateAsync ────────────────────────────────────────────────────────────
    [Fact]
    public async Task Create_PersistsTask_WritesCreatedCommentAndActivityEvent()
    {
        using var f = new SqliteTestFactory();
        var featureId = SeedFeature(f);
        var svc = new TaskService(f);

        var created = await svc.CreateAsync(
            new TaskCreate(featureId, "Service task", Priority: TaskPriority.High), "svc-test");

        Assert.True(created.Id > 0);
        Assert.Equal(featureId, created.FeatureId);
        Assert.Equal(TaskPriority.High, created.Priority);

        using var db = f.CreateDbContext();
        var persisted = await db.Tasks.FindAsync(created.Id);
        Assert.NotNull(persisted);
        Assert.Equal("Service task", persisted!.Name);

        // CommentType.Created audit row authored by the actor.
        var comment = await db.TaskComments.SingleAsync(c => c.TaskItemId == created.Id);
        Assert.Equal(CommentType.Created, comment.Type);
        Assert.Equal("svc-test", comment.Author);

        var ev = await db.ActivityEvents.SingleAsync(e => e.EntityId == created.Id);
        Assert.Equal("TaskCreated", ev.EventType);
        Assert.Equal("Task", ev.EntityType);
        Assert.Equal("svc-test", ev.Actor);
        Assert.NotNull(ev.AfterJson);
    }

    [Fact]
    public async Task Create_DefaultsStatusBacklogPriorityLow_AndComputesSortOrder()
    {
        using var f = new SqliteTestFactory();
        var featureId = SeedFeature(f);
        var svc = new TaskService(f);

        var first = await svc.CreateAsync(new TaskCreate(featureId, "First"), "x");
        var second = await svc.CreateAsync(new TaskCreate(featureId, "Second"), "x");

        Assert.Equal(WorkStatus.Backlog, first.Status);
        Assert.Equal(TaskPriority.Low, first.Priority);
        // SortOrder is computed from existing siblings (max + 1), so the second task
        // must sort strictly after the first.
        Assert.True(second.SortOrder > first.SortOrder);
    }

    [Fact]
    public async Task Create_DoneStatus_SetsCompletedAt()
    {
        using var f = new SqliteTestFactory();
        var featureId = SeedFeature(f);
        var svc = new TaskService(f);

        var created = await svc.CreateAsync(
            new TaskCreate(featureId, "Done task", Status: WorkStatus.Done), "x");

        Assert.NotNull(created.CompletedAt);
    }

    [Fact]
    public async Task Create_EmptyName_ThrowsValidation()
    {
        using var f = new SqliteTestFactory();
        var featureId = SeedFeature(f);
        var svc = new TaskService(f);

        await Assert.ThrowsAsync<ValidationException>(
            () => svc.CreateAsync(new TaskCreate(featureId, "   "), "x"));
    }

    [Fact]
    public async Task Create_UnknownFeature_ThrowsFeatureNotFound()
    {
        using var f = new SqliteTestFactory();
        var svc = new TaskService(f);

        var ex = await Assert.ThrowsAsync<FeatureNotFoundException>(
            () => svc.CreateAsync(new TaskCreate(99999, "orphan"), "x"));
        Assert.Equal(99999, ex.FeatureId);
    }

    // ── DeleteAsync ────────────────────────────────────────────────────────────
    [Fact]
    public async Task Delete_RemovesTaskAndCascadeChildren_AuditSurvives()
    {
        using var f = new SqliteTestFactory();
        var (featureId, taskId) = SeedFeatureWithTask(f);

        // Build a 2-level subtask tree (parent → child) so the leaf-first delete path
        // (Subtask.ChildSubtasks Restrict FK) is actually exercised, plus an acceptance
        // criterion, a comment, and a dependency in both directions to a second task.
        int depTargetId;
        using (var seed = f.CreateDbContext())
        {
            var parent = new Subtask { TaskItemId = taskId, Text = "parent" };
            seed.Add(parent);
            seed.SaveChanges();
            seed.Add(new Subtask { TaskItemId = taskId, Text = "child", ParentSubtaskId = parent.Id });
            seed.Add(new AcceptanceCriterion { TaskItemId = taskId, Text = "AC" });
            seed.Add(new TaskComment { TaskItemId = taskId, Text = "note", Type = CommentType.Note });

            var projectId = seed.Features.Where(x => x.Id == featureId).Select(x => x.ProjectId).Single();
            var other = new TaskItem { Name = "dep target", FeatureId = featureId };
            seed.Add(other);
            seed.SaveChanges();
            depTargetId = other.Id;
            seed.Add(new TaskDependency { ProjectId = projectId, TaskItemId = taskId, DependsOnTaskItemId = depTargetId });
            seed.Add(new TaskDependency { ProjectId = projectId, TaskItemId = depTargetId, DependsOnTaskItemId = taskId });
            seed.SaveChanges();
        }

        var svc = new TaskService(f);
        await svc.DeleteAsync(taskId, "svc-test");

        using var db = f.CreateDbContext();
        Assert.False(await db.Tasks.AnyAsync(t => t.Id == taskId));
        Assert.False(await db.Subtasks.AnyAsync(s => s.TaskItemId == taskId));
        Assert.False(await db.AcceptanceCriteria.AnyAsync(c => c.TaskItemId == taskId));
        Assert.False(await db.TaskComments.AnyAsync(c => c.TaskItemId == taskId));
        Assert.False(await db.TaskDependencies.AnyAsync(
            d => d.TaskItemId == taskId || d.DependsOnTaskItemId == taskId));

        // The second task is untouched; only the deleted task's rows are gone.
        Assert.True(await db.Tasks.AnyAsync(t => t.Id == depTargetId));

        // Audit row must SURVIVE the delete (ActivityEvent has no task FK).
        var audit = await db.ActivityEvents.SingleAsync(
            e => e.EntityId == taskId && e.EventType == "TaskDeleted");
        Assert.Equal("svc-test", audit.Actor);
        Assert.NotNull(audit.BeforeJson);
    }

    [Fact]
    public async Task Delete_MissingTask_ThrowsTaskNotFound()
    {
        using var f = new SqliteTestFactory();
        var svc = new TaskService(f);

        var ex = await Assert.ThrowsAsync<TaskNotFoundException>(
            () => svc.DeleteAsync(99999, "x"));
        Assert.Equal(99999, ex.TaskId);
    }

    // ── ClaimAsync ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task Claim_UnclaimedTask_SetsClaimAndWritesActivityEvent()
    {
        using var f = new SqliteTestFactory();
        var (_, taskId) = SeedFeatureWithTask(f);
        var svc = new TaskService(f);

        var result = await svc.ClaimAsync(taskId, "alice");

        Assert.Equal("alice", result.ClaimedBy);
        Assert.NotNull(result.ClaimedAt);

        using var db = f.CreateDbContext();
        var ev = await db.ActivityEvents.SingleAsync(e => e.EntityId == taskId);
        Assert.Equal("TaskClaimed", ev.EventType);
        Assert.Equal("alice", ev.Actor);
    }

    [Fact]
    public async Task Claim_SameActorReclaim_IsIdempotent_PreservesClaimedAt_NoSecondEvent()
    {
        using var f = new SqliteTestFactory();
        var (_, taskId) = SeedFeatureWithTask(f);
        var svc = new TaskService(f);

        var first = await svc.ClaimAsync(taskId, "alice");
        var firstClaimedAt = first.ClaimedAt;

        var second = await svc.ClaimAsync(taskId, "alice");

        Assert.Equal(firstClaimedAt, second.ClaimedAt);
        using var db = f.CreateDbContext();
        Assert.Equal(1, await db.ActivityEvents.CountAsync(e => e.EventType == "TaskClaimed"));
    }

    [Fact]
    public async Task Claim_DifferentActor_ThrowsClaimConflict()
    {
        using var f = new SqliteTestFactory();
        var (_, taskId) = SeedFeatureWithTask(f, claimedBy: "alice");
        var svc = new TaskService(f);

        var ex = await Assert.ThrowsAsync<TaskClaimConflictException>(
            () => svc.ClaimAsync(taskId, "bob"));
        Assert.Equal(taskId, ex.TaskId);
        Assert.Equal("alice", ex.ClaimedBy);
    }

    [Fact]
    public async Task Claim_BlankActor_ThrowsValidation()
    {
        using var f = new SqliteTestFactory();
        var (_, taskId) = SeedFeatureWithTask(f);
        var svc = new TaskService(f);

        await Assert.ThrowsAsync<ValidationException>(() => svc.ClaimAsync(taskId, "  "));
    }

    [Fact]
    public async Task Claim_MissingTask_ThrowsTaskNotFound()
    {
        using var f = new SqliteTestFactory();
        var svc = new TaskService(f);

        await Assert.ThrowsAsync<TaskNotFoundException>(() => svc.ClaimAsync(99999, "alice"));
    }

    // ── ReleaseAsync ───────────────────────────────────────────────────────────
    [Fact]
    public async Task Release_ByClaimingActor_ClearsClaimAndWritesActivityEvent()
    {
        using var f = new SqliteTestFactory();
        var (_, taskId) = SeedFeatureWithTask(f, claimedBy: "alice");
        var svc = new TaskService(f);

        var result = await svc.ReleaseAsync(taskId, "alice");

        Assert.Null(result.ClaimedBy);
        Assert.Null(result.ClaimedAt);

        using var db = f.CreateDbContext();
        var ev = await db.ActivityEvents.SingleAsync(e => e.EntityId == taskId);
        Assert.Equal("TaskReleased", ev.EventType);
        Assert.Equal("alice", ev.Actor);
    }

    [Fact]
    public async Task Release_ByDifferentActor_ThrowsClaimConflict()
    {
        using var f = new SqliteTestFactory();
        var (_, taskId) = SeedFeatureWithTask(f, claimedBy: "alice");
        var svc = new TaskService(f);

        await Assert.ThrowsAsync<TaskClaimConflictException>(() => svc.ReleaseAsync(taskId, "bob"));
    }

    [Fact]
    public async Task Release_AlreadyUnclaimed_IsIdempotent_NoEvent()
    {
        using var f = new SqliteTestFactory();
        var (_, taskId) = SeedFeatureWithTask(f);
        var svc = new TaskService(f);

        var result = await svc.ReleaseAsync(taskId, "alice");

        Assert.Null(result.ClaimedBy);
        using var db = f.CreateDbContext();
        Assert.Equal(0, await db.ActivityEvents.CountAsync(e => e.EventType == "TaskReleased"));
    }

    [Fact]
    public async Task Release_BlankActor_ThrowsValidation()
    {
        using var f = new SqliteTestFactory();
        var (_, taskId) = SeedFeatureWithTask(f, claimedBy: "alice");
        var svc = new TaskService(f);

        await Assert.ThrowsAsync<ValidationException>(() => svc.ReleaseAsync(taskId, ""));
    }

    // ── AddCommentAsync ────────────────────────────────────────────────────────
    [Fact]
    public async Task AddComment_UserType_PersistsCommentAndWritesActivityEvent()
    {
        using var f = new SqliteTestFactory();
        var (_, taskId) = SeedFeatureWithTask(f);
        var svc = new TaskService(f);

        var comment = await svc.AddCommentAsync(
            taskId, "a real note", author: "svc-test", type: CommentType.Note, actor: "svc-test");

        Assert.True(comment.Id > 0);
        Assert.Equal(CommentType.Note, comment.Type);

        using var db = f.CreateDbContext();
        var persisted = await db.TaskComments.SingleAsync(c => c.Id == comment.Id);
        Assert.Equal(taskId, persisted.TaskItemId);
        Assert.Equal("a real note", persisted.Text);

        var ev = await db.ActivityEvents.SingleAsync(e => e.EntityId == taskId);
        Assert.Equal("TaskCommentAdded", ev.EventType);
        Assert.Equal("svc-test", ev.Actor);
    }

    [Fact]
    public async Task AddComment_SystemType_ThrowsValidation_NoRowsWritten()
    {
        using var f = new SqliteTestFactory();
        var (_, taskId) = SeedFeatureWithTask(f);
        var svc = new TaskService(f);

        await Assert.ThrowsAsync<ValidationException>(
            () => svc.AddCommentAsync(taskId, "nope", "x", CommentType.StatusChange, "x"));

        // A rejected comment must not leak a partial row or stray activity event.
        using var db = f.CreateDbContext();
        Assert.Equal(0, await db.TaskComments.CountAsync(c => c.TaskItemId == taskId));
        Assert.Equal(0, await db.ActivityEvents.CountAsync(e => e.EntityId == taskId));
    }

    [Fact]
    public async Task AddComment_BlankText_ThrowsValidation()
    {
        using var f = new SqliteTestFactory();
        var (_, taskId) = SeedFeatureWithTask(f);
        var svc = new TaskService(f);

        await Assert.ThrowsAsync<ValidationException>(
            () => svc.AddCommentAsync(taskId, "   ", "x", CommentType.Note, "x"));
    }

    [Fact]
    public async Task AddComment_MissingTask_ThrowsTaskNotFound()
    {
        using var f = new SqliteTestFactory();
        var svc = new TaskService(f);

        await Assert.ThrowsAsync<TaskNotFoundException>(
            () => svc.AddCommentAsync(99999, "hi", "x", CommentType.Note, "x"));
    }
}
