using KanbanFlow.Models;
using KanbanFlow.Services;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Tests;

/// <summary>
/// Real-code tests for the Definition-of-Done gate (#428) against a live SQLite database.
/// </summary>
/// <remarks>
/// <para>
/// The gate refuses a transition <em>into</em> <see cref="WorkStatus.Done"/> while the task has
/// unmet acceptance criteria. These tests cover all four service paths that can reach Done —
/// <c>ChangeStatusAsync</c>, <c>MoveOnBoardAsync</c>, <c>ToggleDoneAsync</c> and
/// <c>UpdateAsync</c> — because gating only the obvious one would leave the other three as
/// unguarded ways around it.
/// </para>
/// <para>
/// Every refusal test re-queries a fresh context and asserts the task's persisted status is
/// <em>unchanged</em>. Asserting only that the call threw would pass against an implementation
/// that mutated the row and then threw.
/// </para>
/// </remarks>
public sealed class DefinitionOfDoneGateTests
{
    /// <summary>Seeds one task with <paramref name="criteria"/> acceptance criteria, of which
    /// the first <paramref name="completed"/> are ticked.</summary>
    private static int SeedTaskWithCriteria(
        SqliteTestFactory f, int criteria, int completed, WorkStatus status = WorkStatus.InProgress)
    {
        using var db = f.CreateDbContext();
        var project = new Project { Name = "Proj" };
        var feature = new Feature { Name = "Feat", Project = project };
        var task = new TaskItem { Name = "Task", Feature = feature, Status = status };
        db.Add(task);
        db.SaveChanges();

        for (var i = 0; i < criteria; i++)
        {
            db.AcceptanceCriteria.Add(new AcceptanceCriterion
            {
                TaskItemId = task.Id,
                Text = $"criterion-{i}",
                Completed = i < completed,
                SortOrder = i
            });
        }
        db.SaveChanges();
        return task.Id;
    }

    private static async Task<TaskItem> Reload(SqliteTestFactory f, int id)
    {
        await using var db = await f.CreateDbContextAsync();
        return await db.Tasks.AsNoTracking().FirstAsync(t => t.Id == id);
    }

    // ── the refusal ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ChangeStatus_ToDone_WithUnmetCriteria_Throws_AndLeavesStatusUnchanged()
    {
        using var f = new SqliteTestFactory();
        var id = SeedTaskWithCriteria(f, criteria: 3, completed: 0);
        var svc = new TaskService(f);

        var ex = await Assert.ThrowsAsync<DefinitionOfDoneNotMetException>(
            () => svc.ChangeStatusAsync(id, WorkStatus.Done, "tester"));

        Assert.Equal(id, ex.TaskId);
        Assert.Equal(3, ex.UnmetCount);

        // The mutation must not have happened — a throw after a write would still be a bug.
        var reloaded = await Reload(f, id);
        Assert.Equal(WorkStatus.InProgress, reloaded.Status);
        Assert.Null(reloaded.CompletedAt);
    }

    [Fact]
    public async Task ChangeStatus_ToDone_WithAllCriteriaMet_Succeeds()
    {
        using var f = new SqliteTestFactory();
        var id = SeedTaskWithCriteria(f, criteria: 3, completed: 3);
        var svc = new TaskService(f);

        await svc.ChangeStatusAsync(id, WorkStatus.Done, "tester");

        var reloaded = await Reload(f, id);
        Assert.Equal(WorkStatus.Done, reloaded.Status);
        Assert.NotNull(reloaded.CompletedAt);
    }

    [Fact]
    public async Task ChangeStatus_ToDone_WithPartiallyMetCriteria_ReportsOnlyTheUnmetOnes()
    {
        using var f = new SqliteTestFactory();
        var id = SeedTaskWithCriteria(f, criteria: 3, completed: 2);
        var svc = new TaskService(f);

        var ex = await Assert.ThrowsAsync<DefinitionOfDoneNotMetException>(
            () => svc.ChangeStatusAsync(id, WorkStatus.Done, "tester"));

        // Exactly the remaining one, named — not just a count.
        Assert.Equal(1, ex.UnmetCount);
        Assert.Equal(["criterion-2"], ex.Unmet);
    }

    [Fact]
    public async Task ChangeStatus_ToDone_WithZeroCriteria_IsUnaffected()
    {
        using var f = new SqliteTestFactory();
        var id = SeedTaskWithCriteria(f, criteria: 0, completed: 0);
        var svc = new TaskService(f);

        await svc.ChangeStatusAsync(id, WorkStatus.Done, "tester");

        Assert.Equal(WorkStatus.Done, (await Reload(f, id)).Status);
    }

    // ── the audited override ───────────────────────────────────────────────────

    [Fact]
    public async Task ChangeStatus_ToDone_WithOverrideReason_SucceedsAndAuditsBothRecords()
    {
        using var f = new SqliteTestFactory();
        var id = SeedTaskWithCriteria(f, criteria: 3, completed: 1);
        var svc = new TaskService(f);

        await svc.ChangeStatusAsync(id, WorkStatus.Done, "tester", default, "criteria were written wrong");

        Assert.Equal(WorkStatus.Done, (await Reload(f, id)).Status);

        await using var db = await f.CreateDbContextAsync();

        var verifications = await db.TaskComments
            .Where(c => c.TaskItemId == id && c.Type == CommentType.Verification)
            .ToListAsync();
        Assert.Single(verifications);
        Assert.Contains("criteria were written wrong", verifications[0].Text);
        Assert.Contains("criterion-1", verifications[0].Text);   // the unmet ones are named

        var overrides = await db.ActivityEvents
            .Where(e => e.EntityType == "Task" && e.EntityId == id && e.EventType == "TaskDoneOverridden")
            .ToListAsync();
        Assert.Single(overrides);
        Assert.Contains("criteria were written wrong", overrides[0].Message);
    }

    [Fact]
    public async Task ChangeStatus_ToDone_WithWhitespaceOverrideReason_IsStillRefused()
    {
        using var f = new SqliteTestFactory();
        var id = SeedTaskWithCriteria(f, criteria: 2, completed: 0);
        var svc = new TaskService(f);

        // Whitespace must not defeat the gate — otherwise " " is a silent bypass.
        await Assert.ThrowsAsync<DefinitionOfDoneNotMetException>(
            () => svc.ChangeStatusAsync(id, WorkStatus.Done, "tester", default, "   "));

        Assert.Equal(WorkStatus.InProgress, (await Reload(f, id)).Status);
    }

    [Fact]
    public async Task Override_IsNotWritten_WhenTheTransitionWasCleanAnyway()
    {
        using var f = new SqliteTestFactory();
        var id = SeedTaskWithCriteria(f, criteria: 2, completed: 2);
        var svc = new TaskService(f);

        // A reason supplied on a transition that did not need one must not fabricate an override.
        await svc.ChangeStatusAsync(id, WorkStatus.Done, "tester", default, "not actually needed");

        await using var db = await f.CreateDbContextAsync();
        Assert.Empty(await db.ActivityEvents
            .Where(e => e.EntityId == id && e.EventType == "TaskDoneOverridden").ToListAsync());
    }

    // ── the gate's boundaries ──────────────────────────────────────────────────

    [Fact]
    public async Task DoneToDone_RemainsIdempotent_AndDoesNotTripTheGate()
    {
        using var f = new SqliteTestFactory();
        var id = SeedTaskWithCriteria(f, criteria: 3, completed: 0, status: WorkStatus.Done);
        var svc = new TaskService(f);

        // Already Done with unmet criteria: re-asserting Done is a no-op, not a refusal.
        await svc.ChangeStatusAsync(id, WorkStatus.Done, "tester");

        Assert.Equal(WorkStatus.Done, (await Reload(f, id)).Status);
    }

    [Fact]
    public async Task TransitionToNonDoneStatus_WithUnmetCriteria_IsUnaffected()
    {
        using var f = new SqliteTestFactory();
        var id = SeedTaskWithCriteria(f, criteria: 3, completed: 0);
        var svc = new TaskService(f);

        await svc.ChangeStatusAsync(id, WorkStatus.Review, "tester");

        Assert.Equal(WorkStatus.Review, (await Reload(f, id)).Status);
    }

    [Fact]
    public async Task LeavingDone_WithUnmetCriteria_IsUnaffected()
    {
        using var f = new SqliteTestFactory();
        var id = SeedTaskWithCriteria(f, criteria: 3, completed: 0, status: WorkStatus.Done);
        var svc = new TaskService(f);

        // The gate is on the destination, never a trap that locks a task inside Done.
        await svc.ChangeStatusAsync(id, WorkStatus.InProgress, "tester");

        Assert.Equal(WorkStatus.InProgress, (await Reload(f, id)).Status);
    }

    // ── the other three paths to Done (guard the class, not the exemplar) ──────

    [Fact]
    public async Task ToggleDone_WithUnmetCriteria_IsRefused()
    {
        using var f = new SqliteTestFactory();
        var id = SeedTaskWithCriteria(f, criteria: 2, completed: 0, status: WorkStatus.Todo);
        var svc = new TaskService(f);

        await Assert.ThrowsAsync<DefinitionOfDoneNotMetException>(
            () => svc.ToggleDoneAsync(id, "tester"));

        Assert.Equal(WorkStatus.Todo, (await Reload(f, id)).Status);
    }

    [Fact]
    public async Task ToggleDone_OffDone_WithUnmetCriteria_IsAllowed()
    {
        using var f = new SqliteTestFactory();
        var id = SeedTaskWithCriteria(f, criteria: 2, completed: 0, status: WorkStatus.Done);
        var svc = new TaskService(f);

        await svc.ToggleDoneAsync(id, "tester");

        Assert.Equal(WorkStatus.Todo, (await Reload(f, id)).Status);
    }

    [Fact]
    public async Task UpdateAsync_SettingStatusDone_WithUnmetCriteria_IsRefused()
    {
        using var f = new SqliteTestFactory();
        var id = SeedTaskWithCriteria(f, criteria: 2, completed: 0);
        var svc = new TaskService(f);

        await Assert.ThrowsAsync<DefinitionOfDoneNotMetException>(
            () => svc.UpdateAsync(id, new TaskUpdate(Status: WorkStatus.Done), "tester"));

        var reloaded = await Reload(f, id);
        Assert.Equal(WorkStatus.InProgress, reloaded.Status);
        Assert.Null(reloaded.CompletedAt);
    }

    [Fact]
    public async Task UpdateAsync_ChangingOtherFields_IsUnaffectedByTheGate()
    {
        using var f = new SqliteTestFactory();
        var id = SeedTaskWithCriteria(f, criteria: 2, completed: 0);
        var svc = new TaskService(f);

        await svc.UpdateAsync(id, new TaskUpdate(Name: "renamed"), "tester");

        Assert.Equal("renamed", (await Reload(f, id)).Name);
    }

    [Fact]
    public async Task MoveOnBoard_ToDoneColumn_WithUnmetCriteria_IsRefused()
    {
        using var f = new SqliteTestFactory();
        var id = SeedTaskWithCriteria(f, criteria: 2, completed: 0);
        int projectId;
        await using (var db = await f.CreateDbContextAsync())
        {
            projectId = await db.Tasks.Where(t => t.Id == id)
                .Select(t => t.Feature!.ProjectId).FirstAsync();
        }
        var svc = new TaskService(f);

        // Drag-to-Done is the primary human path and must be gated too.
        await Assert.ThrowsAsync<DefinitionOfDoneNotMetException>(
            () => svc.MoveOnBoardAsync(id, WorkStatus.Done, projectId, 0, "tester"));

        Assert.Equal(WorkStatus.InProgress, (await Reload(f, id)).Status);
    }

    [Fact]
    public async Task MoveOnBoard_ToNonDoneColumn_WithUnmetCriteria_IsAllowed()
    {
        using var f = new SqliteTestFactory();
        var id = SeedTaskWithCriteria(f, criteria: 2, completed: 0);
        int projectId;
        await using (var db = await f.CreateDbContextAsync())
        {
            projectId = await db.Tasks.Where(t => t.Id == id)
                .Select(t => t.Feature!.ProjectId).FirstAsync();
        }
        var svc = new TaskService(f);

        await svc.MoveOnBoardAsync(id, WorkStatus.Review, projectId, 0, "tester");

        Assert.Equal(WorkStatus.Review, (await Reload(f, id)).Status);
    }
}
