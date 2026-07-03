using KanbanFlow.Data;
using KanbanFlow.Mcp;
using KanbanFlow.Models;
using KanbanFlow.Services;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Tests;

/// <summary>
/// Tests for the shared <see cref="TaskRanking"/> ranker that single-sources the "next tasks"
/// recommendation for BOTH the REST endpoint (<c>GET /api/projects/{id}/next-tasks</c>) and the
/// MCP <c>get_next_tasks</c> tool (#151, #188).
///
/// The first block exercises the pure ranker directly (no DB) — it is a pure function, so the
/// ordering rules are asserted against in-memory inputs. The final block is the #188 parity
/// guarantee: over a REAL SQLite database it asserts the REST projection path and the REAL MCP
/// tool produce the identical id order, because both now consume <see cref="TaskRanking"/>.
/// No fakes — the parity test runs the real MCP tool and the real ranker over the real EF schema.
/// </summary>
public sealed class TaskRankingTests
{
    // ── pure-ranker helpers ──────────────────────────────────────────────────────────
    private static TaskItem Task(
        int id,
        TaskPriority priority = TaskPriority.Medium,
        WorkStatus status = WorkStatus.Todo,
        string? claimedBy = null) =>
        new() { Id = id, Name = $"T{id}", Priority = priority, Status = status, ClaimedBy = claimedBy };

    private static Dictionary<int, WorkStatus> StatusById(params (int id, WorkStatus status)[] rows) =>
        rows.ToDictionary(r => r.id, r => r.status);

    private static ILookup<int, int> Deps(params (int taskId, int dependsOnId)[] edges) =>
        edges.ToLookup(e => e.taskId, e => e.dependsOnId);

    private static readonly ILookup<int, int> NoDeps = Deps();

    // ── ordering / filtering rules ──────────────────────────────────────────────────
    [Fact]
    public void RankNextTasks_ExcludesTaskWithUnfinishedBlocker_KeepsTaskWhoseBlockerIsDone()
    {
        var t1 = Task(1, TaskPriority.High);
        var open = new[] { t1 };
        var deps = Deps((1, 2)); // T1 depends on T2

        var blocked = TaskRanking.RankNextTasks(
            open, StatusById((1, WorkStatus.Todo), (2, WorkStatus.Todo)), deps, actor: null, limit: 5);
        Assert.Empty(blocked); // blocker T2 not Done → T1 excluded

        var ready = TaskRanking.RankNextTasks(
            open, StatusById((1, WorkStatus.Todo), (2, WorkStatus.Done)), deps, actor: null, limit: 5);
        Assert.Equal([1], ready.Select(r => r.Task.Id)); // blocker Done → T1 recommended
    }

    [Fact]
    public void RankNextTasks_TreatsMissingDependencyTargetAsUnfinishedBlocker()
    {
        var open = new[] { Task(1) };
        var deps = Deps((1, 999)); // depends on a task absent from statusById

        var result = TaskRanking.RankNextTasks(
            open, StatusById((1, WorkStatus.Todo)), deps, actor: null, limit: 5);

        Assert.Empty(result);
    }

    [Fact]
    public void RankNextTasks_OrdersHigherPriorityFirst()
    {
        var open = new[]
        {
            Task(1, TaskPriority.Low),
            Task(2, TaskPriority.Critical),
            Task(3, TaskPriority.Medium),
        };

        var result = TaskRanking.RankNextTasks(
            open, StatusById((1, WorkStatus.Todo), (2, WorkStatus.Todo), (3, WorkStatus.Todo)),
            NoDeps, actor: null, limit: 5);

        Assert.Equal([2, 3, 1], result.Select(r => r.Task.Id)); // Critical → Medium → Low
    }

    [Fact]
    public void RankNextTasks_PrefersInProgressAmongSamePriority()
    {
        var open = new[]
        {
            Task(1, TaskPriority.Medium, WorkStatus.Todo),
            Task(2, TaskPriority.Medium, WorkStatus.InProgress),
        };

        var result = TaskRanking.RankNextTasks(
            open, StatusById((1, WorkStatus.Todo), (2, WorkStatus.InProgress)),
            NoDeps, actor: null, limit: 5);

        Assert.Equal([2, 1], result.Select(r => r.Task.Id)); // in-progress ahead of same-priority Todo
    }

    [Fact]
    public void RankNextTasks_EqualPriorityStatusClaim_TieBreaksByAscendingId()
    {
        var open = new[]
        {
            Task(30, TaskPriority.Medium, WorkStatus.Todo),
            Task(10, TaskPriority.Medium, WorkStatus.Todo),
            Task(20, TaskPriority.Medium, WorkStatus.Todo),
        };

        var result = TaskRanking.RankNextTasks(
            open, StatusById((10, WorkStatus.Todo), (20, WorkStatus.Todo), (30, WorkStatus.Todo)),
            NoDeps, actor: null, limit: 5);

        Assert.Equal([10, 20, 30], result.Select(r => r.Task.Id)); // deterministic (#188)
    }

    // ── actor de-prioritisation (#151) ────────────────────────────────────────────────
    [Fact]
    public void RankNextTasks_WithActor_SinksTaskClaimedByAnotherActor_BelowOwnAndUnclaimed()
    {
        var open = new[]
        {
            Task(2, TaskPriority.High, claimedBy: "bob"),   // claimed by another → sinks
            Task(1, TaskPriority.High, claimedBy: "alice"), // own claim → not de-prioritised
            Task(3, TaskPriority.High),                     // unclaimed
        };

        var result = TaskRanking.RankNextTasks(
            open, StatusById((1, WorkStatus.Todo), (2, WorkStatus.Todo), (3, WorkStatus.Todo)),
            actor: "alice", limit: 5, dependsOnByTask: NoDeps);

        // Own + unclaimed stay ahead (tie-break Id: 1 before 3); bob's claim last.
        Assert.Equal([1, 3, 2], result.Select(r => r.Task.Id));
        Assert.True(result.Single(r => r.Task.Id == 2).ClaimedByOther);
        Assert.False(result.Single(r => r.Task.Id == 1).ClaimedByOther);
        Assert.False(result.Single(r => r.Task.Id == 3).ClaimedByOther);
    }

    [Fact]
    public void RankNextTasks_WithActorMatchingClaim_DoesNotDeprioritiseOwnClaim()
    {
        var open = new[]
        {
            Task(2, TaskPriority.High, claimedBy: "bob"),
            Task(3, TaskPriority.High),
        };

        var result = TaskRanking.RankNextTasks(
            open, StatusById((2, WorkStatus.Todo), (3, WorkStatus.Todo)),
            actor: "bob", limit: 5, dependsOnByTask: NoDeps);

        // For actor=bob, bob's own claim is NOT de-prioritised; tie-break by Id → 2 before 3.
        Assert.Equal([2, 3], result.Select(r => r.Task.Id));
        Assert.False(result.Single(r => r.Task.Id == 2).ClaimedByOther);
    }

    [Fact]
    public void RankNextTasks_WithNullActor_StillDeprioritisesAnyClaimedTask_Consistently()
    {
        // Canonical MCP behaviour preserved (#151): with no actor a claimed task counts as
        // "claimed by someone other than <nobody>" and sinks, so REST (no ?actor) and the MCP
        // tool agree. This mirrors KanbanMcpTools.GetNextTasks called without an actor.
        var open = new[]
        {
            Task(1, TaskPriority.High, claimedBy: "bob"),
            Task(2, TaskPriority.High),
        };

        var result = TaskRanking.RankNextTasks(
            open, StatusById((1, WorkStatus.Todo), (2, WorkStatus.Todo)),
            actor: null, limit: 5, dependsOnByTask: NoDeps);

        Assert.Equal([2, 1], result.Select(r => r.Task.Id));
        Assert.True(result.Single(r => r.Task.Id == 1).ClaimedByOther);
    }

    // ── limit clamp + reason ──────────────────────────────────────────────────────────
    [Fact]
    public void RankNextTasks_ClampsLimitBetween1And50()
    {
        var open = Enumerable.Range(1, 60)
            .Select(i => Task(i, TaskPriority.Medium, WorkStatus.Todo))
            .ToArray();
        var statusById = open.ToDictionary(t => t.Id, t => t.Status);

        Assert.Equal(50, TaskRanking.RankNextTasks(open, statusById, NoDeps, null, limit: 1000).Count); // high → 50
        Assert.Single(TaskRanking.RankNextTasks(open, statusById, NoDeps, null, limit: 0));              // low → 1
        Assert.Equal(5, TaskRanking.RankNextTasks(open, statusById, NoDeps, null, limit: 5).Count);      // within range
    }

    [Fact]
    public void RankNextTasks_ReasonMentionsPriority_InProgress_AndClaimState()
    {
        var open = new[]
        {
            Task(1, TaskPriority.Critical, WorkStatus.InProgress, claimedBy: "bob"),
            Task(2, TaskPriority.Low, WorkStatus.Todo),
        };

        var result = TaskRanking.RankNextTasks(
            open, StatusById((1, WorkStatus.InProgress), (2, WorkStatus.Todo)),
            actor: "alice", limit: 5, dependsOnByTask: NoDeps);

        var claimed = result.Single(r => r.Task.Id == 1).Reason;
        Assert.Contains($"{TaskPriority.Critical} priority", claimed);
        Assert.Contains("already in progress", claimed);
        Assert.Contains("no open blockers", claimed);
        Assert.Contains("claimed by bob (deprioritised)", claimed);

        var unclaimed = result.Single(r => r.Task.Id == 2).Reason;
        Assert.Contains($"{TaskPriority.Low} priority", unclaimed);
        Assert.Contains("unclaimed", unclaimed);
    }

    // ── #188 parity: REST projection path == real MCP tool, over real SQLite ───────────
    [Fact]
    public async Task RestAndMcp_ConsumeSharedRanker_ProduceIdenticalIdOrder()
    {
        using var f = new SqliteTestFactory();
        var seeded = SeedRankingScenario(f);

        foreach (var actor in new string?[] { null, "alice", "bob" })
        {
            var rest = await RestRankedIds(f, seeded.ProjectId, actor, limit: 50);
            var mcp = await McpRankedIds(f, seeded.ProjectId, actor, limit: 50);

            // The two surfaces now share TaskRanking, so identical inputs → identical order.
            Assert.Equal(mcp, rest);

            // Not vacuous: the canonical ordering + blocker/Done filtering actually took effect.
            Assert.NotEmpty(rest);
            Assert.Equal(seeded.InProgressCriticalId, rest[0]);   // Critical + in-progress ranks first
            Assert.DoesNotContain(seeded.BlockedId, rest);        // open dependency → excluded
            Assert.DoesNotContain(seeded.DoneId, rest);           // Done → excluded
            Assert.Contains(seeded.UnblockedWithDoneDepId, rest); // dependency satisfied (Done) → included
        }
    }

    [Fact]
    public async Task McpGetNextTasks_RanksBobsClaimLaterForAlice_ThanForBob()
    {
        // Issue #151 test plan, end-to-end through the REAL MCP tool.
        using var f = new SqliteTestFactory();
        var seeded = SeedRankingScenario(f);

        var forAlice = (await KanbanMcpTools.GetNextTasks(f, seeded.ProjectId, "alice", 50)).Select(r => r.Id).ToList();
        var forBob = (await KanbanMcpTools.GetNextTasks(f, seeded.ProjectId, "bob", 50)).Select(r => r.Id).ToList();

        var alicePos = forAlice.IndexOf(seeded.ClaimedByBobId);
        var bobPos = forBob.IndexOf(seeded.ClaimedByBobId);

        Assert.True(alicePos > bobPos,
            $"bob's claimed task should rank later for alice (index {alicePos}) than for bob (index {bobPos}).");
    }

    // ── parity-test infrastructure (real DB) ───────────────────────────────────────────
    private sealed record Seeded(
        int ProjectId,
        int InProgressCriticalId,
        int BlockedId,
        int UnblockedWithDoneDepId,
        int ClaimedByBobId,
        int DoneId);

    /// <summary>
    /// Seeds one project + feature with a deliberately varied task set (mixed priority, an
    /// in-progress task, a task claimed by "bob", a Done task, a task blocked by an open
    /// dependency, and a task whose dependency is already Done) so the ranking rules and
    /// filtering are all exercised and the resulting order is non-trivial.
    /// </summary>
    private static Seeded SeedRankingScenario(SqliteTestFactory f)
    {
        using var db = f.CreateDbContext();
        var project = new Project { Name = "Ranker" };
        var feature = new Feature { Name = "F", Project = project };

        var critTodo = new TaskItem { Name = "crit-todo", Feature = feature, Priority = TaskPriority.Critical, Status = WorkStatus.Todo };
        var critInProgress = new TaskItem { Name = "crit-inprogress", Feature = feature, Priority = TaskPriority.Critical, Status = WorkStatus.InProgress };
        var highClaimedBob = new TaskItem { Name = "high-claimed-bob", Feature = feature, Priority = TaskPriority.High, Status = WorkStatus.Todo, ClaimedBy = "bob", ClaimedAt = DateTime.UtcNow };
        var highUnclaimed = new TaskItem { Name = "high-unclaimed", Feature = feature, Priority = TaskPriority.High, Status = WorkStatus.Todo };
        var medBlocked = new TaskItem { Name = "med-blocked", Feature = feature, Priority = TaskPriority.Medium, Status = WorkStatus.Todo };
        var medUnblocked = new TaskItem { Name = "med-dep-done", Feature = feature, Priority = TaskPriority.Medium, Status = WorkStatus.Todo };
        var lowOpenBlocker = new TaskItem { Name = "low-open-blocker", Feature = feature, Priority = TaskPriority.Low, Status = WorkStatus.Todo };
        var lowDoneBlocker = new TaskItem { Name = "low-done-blocker", Feature = feature, Priority = TaskPriority.Low, Status = WorkStatus.Done, CompletedAt = DateTime.UtcNow };
        var doneHigh = new TaskItem { Name = "done-high", Feature = feature, Priority = TaskPriority.High, Status = WorkStatus.Done, CompletedAt = DateTime.UtcNow };

        db.AddRange(critTodo, critInProgress, highClaimedBob, highUnclaimed,
            medBlocked, medUnblocked, lowOpenBlocker, lowDoneBlocker, doneHigh);
        db.SaveChanges();

        // medBlocked depends on lowOpenBlocker (Todo) → blocked/excluded;
        // medUnblocked depends on lowDoneBlocker (Done) → dependency satisfied → included.
        db.TaskDependencies.AddRange(
            new TaskDependency { ProjectId = project.Id, TaskItemId = medBlocked.Id, DependsOnTaskItemId = lowOpenBlocker.Id },
            new TaskDependency { ProjectId = project.Id, TaskItemId = medUnblocked.Id, DependsOnTaskItemId = lowDoneBlocker.Id });
        db.SaveChanges();

        return new Seeded(
            project.Id, critInProgress.Id, medBlocked.Id, medUnblocked.Id, highClaimedBob.Id, doneHigh.Id);
    }

    /// <summary>
    /// Runs the SAME data-loading the REST <c>/next-tasks</c> endpoint performs and feeds it to the
    /// shared ranker, then returns the id order the endpoint would emit (its projection is a
    /// 1-based <c>Rank</c> over exactly this sequence). Exercises the real ranker + real SQLite.
    /// </summary>
    private static async Task<List<int>> RestRankedIds(SqliteTestFactory f, int projectId, string? actor, int limit)
    {
        await using var db = await f.CreateDbContextAsync();

        var projectTasks = await db.Tasks
            .Include(t => t.Feature)
            .Where(t => t.Feature.ProjectId == projectId)
            .AsNoTracking()
            .ToListAsync();
        var taskIds = projectTasks.Select(t => t.Id).ToList();
        var dependencies = await db.TaskDependencies
            .Where(d => d.ProjectId == projectId && taskIds.Contains(d.TaskItemId) && taskIds.Contains(d.DependsOnTaskItemId))
            .AsNoTracking()
            .ToListAsync();

        var openTasks = projectTasks.Where(t => t.Status != WorkStatus.Done).ToList();
        var statusById = projectTasks.ToDictionary(t => t.Id, t => t.Status);
        var dependsOnByTask = dependencies.ToLookup(d => d.TaskItemId, d => d.DependsOnTaskItemId);

        return TaskRanking.RankNextTasks(openTasks, statusById, dependsOnByTask, actor, limit)
            .Select(ranked => ranked.Task.Id)
            .ToList();
    }

    private static async Task<List<int>> McpRankedIds(SqliteTestFactory f, int projectId, string? actor, int limit)
    {
        var results = await KanbanMcpTools.GetNextTasks(f, projectId, actor, limit);
        return results.Select(r => r.Id).ToList();
    }
}
