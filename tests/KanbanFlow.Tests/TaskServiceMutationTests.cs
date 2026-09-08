using KanbanFlow.Data;
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
        SqliteTestFactory f, string? claimedBy = null, DateTime? claimedAt = null)
    {
        using var db = f.CreateDbContext();
        var feature = new Feature { Name = "Feat", Project = new Project { Name = "Proj" } };
        var task = new TaskItem { Name = "Task", Feature = feature, Status = WorkStatus.Todo };
        if (claimedBy is not null)
        {
            task.ClaimedBy = claimedBy;
            task.ClaimedAt = claimedAt ?? DateTime.UtcNow;
        }
        db.Add(task);
        db.SaveChanges();
        return (feature.Id, task.Id);
    }

    /// <summary>
    /// Seeds Project → Feature → Task with an explicit Name and SortOrder and a deliberately
    /// stale UpdatedAt (10 minutes ago) so any real save's UpdatedAt bump is unambiguous even at
    /// coarse clock resolution. Returns the task id and its persisted UpdatedAt, re-read from a
    /// fresh context so later comparisons are DB-value vs DB-value (no in-memory vs round-tripped gap).
    /// </summary>
    private static (int taskId, DateTime seededUpdatedAt) SeedTaskForUpdate(
        SqliteTestFactory f, string name = "Original", int sortOrder = 5)
    {
        int taskId;
        using (var db = f.CreateDbContext())
        {
            var task = new TaskItem
            {
                Name = name,
                Feature = new Feature { Name = "Feat", Project = new Project { Name = "Proj" } },
                Status = WorkStatus.Todo,
                SortOrder = sortOrder,
                UpdatedAt = DateTime.UtcNow.AddMinutes(-10)
            };
            db.Add(task);
            db.SaveChanges();
            taskId = task.Id;
        }
        using var read = f.CreateDbContext();
        var persisted = read.Tasks.AsNoTracking().Single(t => t.Id == taskId);
        return (taskId, persisted.UpdatedAt);
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

    // #156: the claim UPDATE and its TaskClaimed audit event must commit atomically. If the
    // audit write fails, the claim must roll back — no claimed-but-unaudited task.
    [Fact]
    public async Task Claim_AuditWriteFailure_RollsBackTheClaim()
    {
        using var f = new SqliteTestFactory();
        var (_, taskId) = SeedFeatureWithTask(f);
        // The service runs against contexts whose audit SaveChanges throws (after the claim UPDATE).
        var svc = new TaskService(new ThrowOnSaveFactory(f));

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ClaimAsync(taskId, "alice"));

        // Re-query a real context: the conditional claim UPDATE must have rolled back with the
        // failed audit write — otherwise the task would be claimed with no TaskClaimed event.
        using var db = f.CreateDbContext();
        var task = await db.Tasks.FindAsync(taskId);
        Assert.Null(task!.ClaimedBy);
        Assert.Null(task.ClaimedAt);
        Assert.Equal(0, await db.ActivityEvents.CountAsync(e => e.EventType == "TaskClaimed"));
    }

    // #156: symmetric to the claim case — a failed audit write must roll back the release UPDATE.
    [Fact]
    public async Task Release_AuditWriteFailure_RollsBackTheRelease()
    {
        using var f = new SqliteTestFactory();
        var (_, taskId) = SeedFeatureWithTask(f, claimedBy: "alice");
        var svc = new TaskService(new ThrowOnSaveFactory(f));

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ReleaseAsync(taskId, "alice"));

        // The release UPDATE must have rolled back with the failed audit write — still claimed.
        using var db = f.CreateDbContext();
        var task = await db.Tasks.FindAsync(taskId);
        Assert.Equal("alice", task!.ClaimedBy);
        Assert.Equal(0, await db.ActivityEvents.CountAsync(e => e.EventType == "TaskReleased"));
    }

    [Fact]
    public async Task Claim_StaleSameActorRenewal_AuditWriteFailure_RollsBackBothTimestamps()
    {
        using var f = new SqliteTestFactory();
        var originalClaimedAt = new DateTime(2026, 8, 30, 8, 0, 0, DateTimeKind.Utc);
        var (_, taskId) = SeedFeatureWithTask(f, claimedBy: "alice", claimedAt: originalClaimedAt);
        DateTime originalUpdatedAt;
        using (var before = f.CreateDbContext())
            originalUpdatedAt = (await before.Tasks.FindAsync(taskId))!.UpdatedAt;
        var now = originalClaimedAt.AddHours(25);
        var svc = new TaskService(new ThrowOnSaveFactory(f), clock: () => now);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ClaimAsync(taskId, "alice"));

        using var db = f.CreateDbContext();
        var task = await db.Tasks.FindAsync(taskId);
        Assert.Equal(originalClaimedAt, task!.ClaimedAt);
        Assert.Equal(originalUpdatedAt, task.UpdatedAt);
        Assert.Equal(0, await db.ActivityEvents.CountAsync(e => e.EventType == "TaskClaimRenewed"));
    }

    // A factory whose contexts share the test DB but throw on SaveChangesAsync — simulating an
    // audit-write failure so the surrounding transaction (claim/release + event) must roll back (#156).
    private sealed class ThrowOnSaveFactory(SqliteTestFactory inner) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new ThrowOnSaveContext(inner.Options);
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult<AppDbContext>(new ThrowOnSaveContext(inner.Options));
    }

    private sealed class ThrowOnSaveContext(DbContextOptions<AppDbContext> options) : AppDbContext(options)
    {
        public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("simulated audit-write failure");
    }

    /// <summary>
    /// A same-actor re-claim inside the TTL is the heartbeat (#443): it RENEWS ClaimedAt, because a
    /// holder that cannot move its own lease expires while still working. "Idempotent" survives
    /// where it was actually load-bearing -- no second activity event, and no UpdatedAt churn -- so
    /// a 5-minute cadence cannot flood the timeline or fake task activity on Home.
    /// </summary>
    [Fact]
    public async Task Claim_SameActorReclaim_LogsHeartbeatWithoutUpdatedAtChurn()
    {
        using var f = new SqliteTestFactory();
        var (_, taskId) = SeedFeatureWithTask(f);
        var t0 = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);
        var now = t0;
        var svc = new TaskService(f, clock: () => now);

        var first = await svc.ClaimAsync(taskId, "alice");
        var firstClaimedAt = first.ClaimedAt;
        var firstUpdatedAt = first.UpdatedAt;

        now = t0.AddMinutes(5);                 // a heartbeat, well inside the default TTL
        var second = await svc.ClaimAsync(taskId, "alice");

        // The fix: the lease moves.
        Assert.Equal(now, second.ClaimedAt);
        Assert.NotEqual(firstClaimedAt, second.ClaimedAt);
        // Renewals retain the original work-activity timestamp and log the lease separately.
        Assert.Equal(firstUpdatedAt, second.UpdatedAt);
        using var db = f.CreateDbContext();
        Assert.Equal(1, await db.ActivityEvents.CountAsync(e => e.EventType == "TaskClaimed"));
        Assert.Equal(0, await db.ActivityEvents.CountAsync(e => e.EventType == "TaskClaimRenewed"));
        var heartbeat = await db.ActivityEvents.SingleAsync(e => e.EventType == "TaskClaimHeartbeat");
        Assert.Equal("alice", heartbeat.Actor);
        Assert.Contains("claimedAt", heartbeat.BeforeJson!);
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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n\u2003")]
    public async Task AddComment_BlankText_ThrowsValidation(string? text)
    {
        using var f = new SqliteTestFactory();
        var (_, taskId) = SeedFeatureWithTask(f);
        var svc = new TaskService(f);

        await Assert.ThrowsAsync<ValidationException>(
            () => svc.AddCommentAsync(taskId, text!, "x", CommentType.Note, "x"));

        using var persisted = f.CreateDbContext();
        Assert.Empty(await persisted.TaskComments.ToListAsync());
        Assert.Empty(await persisted.ActivityEvents.ToListAsync());
    }

    [Fact]
    public async Task AddComment_MissingTask_ThrowsTaskNotFound()
    {
        using var f = new SqliteTestFactory();
        var svc = new TaskService(f);

        await Assert.ThrowsAsync<TaskNotFoundException>(
            () => svc.AddCommentAsync(99999, "hi", "x", CommentType.Note, "x"));
    }

    // ── UpdateAsync — atomic SortOrder (#190) ───────────────────────────────────
    // #190: PUT /api/tasks/{id} used to persist the audited fields (TaskService.UpdateAsync,
    // one context) and SortOrder (endpoint, a SECOND context) in two separate transactions —
    // a partial-write window. SortOrder now folds into the service's single SaveChanges so the
    // whole update is atomic, while staying NON-audited (the #88 boundary): a SortOrder change
    // adds no TaskUpdated event and no "Updated: SortOrder" comment.

    // The defect-proving test: with a single fused save, a save failure rolls BOTH the audited
    // field AND SortOrder back together. (The old two-context design could commit the field
    // save and then fail the separate SortOrder save, leaving a partial write.)
    [Fact]
    public async Task Update_SaveFailure_RollsBackFieldAndSortOrderTogether()
    {
        using var f = new SqliteTestFactory();
        var (taskId, _) = SeedTaskForUpdate(f, name: "Original", sortOrder: 5);
        // Real service, but every SaveChangesAsync throws — simulating the fused field+SortOrder
        // persist failing. Both mutations must roll back together.
        var svc = new TaskService(new ThrowOnSaveFactory(f));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.UpdateAsync(taskId, new TaskUpdate(Name: "Changed", SortOrder: 9), "svc-test"));
        // Prove the throw came from the (fused) save, not the feature-null guard — so the
        // rollback assertions below are meaningful.
        Assert.Contains("simulated", ex.Message);

        // Re-query a fresh, normal context: NEITHER Name NOR SortOrder may have persisted.
        using var db = f.CreateDbContext();
        var task = await db.Tasks.FindAsync(taskId);
        Assert.Equal("Original", task!.Name);
        Assert.Equal(5, task.SortOrder);
    }

    [Fact]
    public async Task Update_ChangesFieldAndSortOrder_PersistsBothInOneTransaction()
    {
        using var f = new SqliteTestFactory();
        var (taskId, _) = SeedTaskForUpdate(f, name: "Original", sortOrder: 5);
        var svc = new TaskService(f);

        var result = await svc.UpdateAsync(taskId, new TaskUpdate(Name: "Changed", SortOrder: 9), "svc-test");

        Assert.Equal("Changed", result.Name);
        Assert.Equal(9, result.SortOrder);

        using var db = f.CreateDbContext();
        var task = await db.Tasks.FindAsync(taskId);
        Assert.Equal("Changed", task!.Name);
        Assert.Equal(9, task.SortOrder);

        // Exactly ONE field-change audit trail: one TaskUpdated event + one Note comment for
        // the Name change. SortOrder rides along silently — it adds neither, and it must not
        // leak into the note text (#88).
        Assert.Equal(1, await db.ActivityEvents.CountAsync(e => e.EntityId == taskId && e.EventType == "TaskUpdated"));
        var note = await db.TaskComments.SingleAsync(c => c.TaskItemId == taskId && c.Type == CommentType.Note);
        Assert.Equal("Updated: Name", note.Text);
        Assert.DoesNotContain("SortOrder", note.Text);
    }

    // #88 guard: a SortOrder-only update saves the row + bumps UpdatedAt but writes ZERO audit.
    [Fact]
    public async Task Update_SortOrderOnlyChange_PersistsWithReorderAudit()
    {
        using var f = new SqliteTestFactory();
        var (taskId, seededUpdatedAt) = SeedTaskForUpdate(f, name: "Original", sortOrder: 5);
        var svc = new TaskService(f);

        // Only SortOrder changes; every audited field is null (unchanged).
        var result = await svc.UpdateAsync(taskId, new TaskUpdate(SortOrder: 9), "svc-test");

        Assert.Equal(9, result.SortOrder);

        using var db = f.CreateDbContext();
        var task = await db.Tasks.FindAsync(taskId);
        Assert.Equal(9, task!.SortOrder);
        Assert.Equal("Original", task.Name);          // audited fields untouched
        Assert.True(task.UpdatedAt > seededUpdatedAt); // row really was saved
        // A bare reorder records its actual rank change without adding a discussion comment.
        var reordered = await db.ActivityEvents.SingleAsync(e => e.EntityId == taskId);
        Assert.Equal("TaskReordered", reordered.EventType);
        Assert.Equal("svc-test", reordered.Actor);
        Assert.Contains("sortOrder", reordered.BeforeJson!);
        Assert.Equal(0, await db.TaskComments.CountAsync(c => c.TaskItemId == taskId));
    }

    // Regression: an all-null / no-op update must still short-circuit — no SaveChanges, no
    // UpdatedAt churn, no audit rows.
    [Fact]
    public async Task Update_NoOp_AllNull_PerformsNoSaveNoAudit()
    {
        using var f = new SqliteTestFactory();
        var (taskId, seededUpdatedAt) = SeedTaskForUpdate(f, name: "Original", sortOrder: 5);
        var svc = new TaskService(f);

        var result = await svc.UpdateAsync(taskId, new TaskUpdate(), "svc-test");

        Assert.Equal("Original", result.Name);
        Assert.Equal(5, result.SortOrder);

        using var db = f.CreateDbContext();
        var task = await db.Tasks.FindAsync(taskId);
        Assert.Equal(seededUpdatedAt, task!.UpdatedAt); // no UpdatedAt churn
        Assert.Equal(0, await db.ActivityEvents.CountAsync(e => e.EntityId == taskId));
        Assert.Equal(0, await db.TaskComments.CountAsync(c => c.TaskItemId == taskId));
    }

    // Regression: a PUT carrying a SortOrder value EQUAL to the current one is a no-op — the
    // widened idempotent guard treats it as unchanged, so there is no save and no audit.
    [Fact]
    public async Task Update_UnchangedSortOrder_AddsNoAuditRowsNoChurn()
    {
        using var f = new SqliteTestFactory();
        var (taskId, seededUpdatedAt) = SeedTaskForUpdate(f, name: "Original", sortOrder: 5);
        var svc = new TaskService(f);

        // SortOrder carried but identical to the current value; everything else null.
        var result = await svc.UpdateAsync(taskId, new TaskUpdate(SortOrder: 5), "svc-test");

        Assert.Equal(5, result.SortOrder);

        using var db = f.CreateDbContext();
        var task = await db.Tasks.FindAsync(taskId);
        Assert.Equal(seededUpdatedAt, task!.UpdatedAt); // unchanged SortOrder = no churn
        Assert.Equal(0, await db.ActivityEvents.CountAsync(e => e.EntityId == taskId));
        Assert.Equal(0, await db.TaskComments.CountAsync(c => c.TaskItemId == taskId));
    }
}
