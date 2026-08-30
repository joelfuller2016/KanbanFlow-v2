using KanbanFlow.Models;
using KanbanFlow.Services;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Tests;

/// <summary>
/// Tests for claim expiry (#429): a claim past its TTL is abandoned, stops de-prioritising its
/// task, and may be taken over by another actor — audited as a distinct event.
/// </summary>
/// <remarks>
/// Staleness is reached by <strong>back-dating <c>ClaimedAt</c></strong> and by injecting a short
/// TTL, never by sleeping. A test that waits out a real TTL is slow and, worse, becomes a timing
/// test whose pass can depend on machine load.
/// </remarks>
public sealed class ClaimStalenessTests
{
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(1);

    private static int SeedClaimedTask(SqliteTestFactory f, string? claimedBy, DateTime? claimedAt)
    {
        using var db = f.CreateDbContext();
        var project = new Project { Name = "P" };
        var feature = new Feature { Name = "F", Project = project };
        var task = new TaskItem
        {
            Name = "T",
            Feature = feature,
            Status = WorkStatus.Todo,
            ClaimedBy = claimedBy,
            ClaimedAt = claimedAt
        };
        db.Add(task);
        db.SaveChanges();
        return task.Id;
    }

    private static async Task<TaskItem> Reload(SqliteTestFactory f, int id)
    {
        await using var db = await f.CreateDbContextAsync();
        return await db.Tasks.AsNoTracking().FirstAsync(t => t.Id == id);
    }

    // ── ClaimPolicy: the boundary, asserted on BOTH sides ──────────────────────

    [Theory]
    // age relative to a 1h TTL          expected stale?
    [InlineData(-3599, false)]   // 59m59s old — fresh
    [InlineData(-3600, false)]   // exactly at the TTL — still fresh (favours the holder)
    [InlineData(-3601, true)]    // one second past — stale
    [InlineData(-86400, true)]   // a day old — stale
    public void IsStale_BoundaryIsExact(int ageSeconds, bool expected)
    {
        var now = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);
        var claimedAt = now.AddSeconds(ageSeconds);

        // Both edges asserted, not merely the ordering — confirming a rule fires "eventually"
        // is not confirming WHERE it fires.
        Assert.Equal(expected, ClaimPolicy.IsStale("alice", claimedAt, now, Ttl));
    }

    [Fact]
    public void IsStale_UnclaimedTask_IsNeverStale()
    {
        var now = DateTime.UtcNow;
        Assert.False(ClaimPolicy.IsStale(null, null, now, Ttl));
        Assert.False(ClaimPolicy.IsStale("   ", now.AddYears(-1), now, Ttl));
    }

    [Fact]
    public void IsStale_ClaimedButUndated_IsStale_NotPermanent()
    {
        // The combination ClaimAsync never writes, but a legacy row or a direct DB edit could.
        // Treating it as fresh would make that claim permanent — the exact failure #429 removes.
        Assert.True(ClaimPolicy.IsStale("alice", claimedAt: null, DateTime.UtcNow, Ttl));
    }

    // ── ranking: a stale claim stops sinking the task ──────────────────────────

    [Fact]
    public void Ranking_StaleClaimByAnother_IsNotDeprioritised()
    {
        var now = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);
        var fresh = new TaskItem { Id = 1, Name = "fresh", Status = WorkStatus.Todo, ClaimedBy = "bob", ClaimedAt = now.AddMinutes(-5) };
        var stale = new TaskItem { Id = 2, Name = "stale", Status = WorkStatus.Todo, ClaimedBy = "bob", ClaimedAt = now.AddHours(-9) };

        var ranked = TaskRanking.RankNextTasks(
            [fresh, stale],
            new Dictionary<int, WorkStatus> { [1] = WorkStatus.Todo, [2] = WorkStatus.Todo },
            Array.Empty<(int, int)>().ToLookup(x => x.Item1, x => x.Item2),
            actor: "alice", limit: 10, now: now, claimTtl: Ttl);

        Assert.True(ranked.Single(r => r.Task.Id == 1).ClaimedByOther);    // fresh → still sinks
        Assert.False(ranked.Single(r => r.Task.Id == 2).ClaimedByOther);   // stale → ranks as free

        // ...and the stale one therefore comes FIRST, which is the behaviour that matters.
        Assert.Equal(2, ranked[0].Task.Id);
    }

    // ── takeover ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ClaimAsync_FreshClaimByAnother_StillConflicts()
    {
        using var f = new SqliteTestFactory();
        var id = SeedClaimedTask(f, "bob", DateTime.UtcNow.AddMinutes(-5));
        var svc = new TaskService(f, Ttl);

        await Assert.ThrowsAsync<TaskClaimConflictException>(() => svc.ClaimAsync(id, "alice"));
        Assert.Equal("bob", (await Reload(f, id)).ClaimedBy);
    }

    [Fact]
    public async Task ClaimAsync_StaleClaim_IsTakenOver_AndAuditedAsStolen()
    {
        using var f = new SqliteTestFactory();
        var seededAt = DateTime.UtcNow.AddHours(-9);
        var id = SeedClaimedTask(f, "bob", seededAt);
        var svc = new TaskService(f, Ttl);

        await svc.ClaimAsync(id, "alice");

        var reloaded = await Reload(f, id);
        Assert.Equal("alice", reloaded.ClaimedBy);
        Assert.NotNull(reloaded.ClaimedAt);

        await using var db = await f.CreateDbContextAsync();
        var events = await db.ActivityEvents
            .Where(e => e.EntityType == "Task" && e.EntityId == id)
            .ToListAsync();

        // A steal must be its OWN event type — a plain TaskClaimed here would erase the fact
        // that the claim was taken from a named holder.
        var stolen = Assert.Single(events, e => e.EventType == "TaskClaimStolen");
        Assert.Contains("bob", stolen.Message);       // the dispossessed holder is named
        Assert.Contains("alice", stolen.Message);
        var expectedAge = $"{(DateTime.UtcNow - seededAt).TotalHours:0.##}";
        Assert.Contains($"{expectedAge}h old", stolen.Message);     // claim age is included, not just the TTL
        Assert.Equal("alice", stolen.Actor);
        Assert.DoesNotContain(events, e => e.EventType == "TaskClaimed");
    }

    [Fact]
    public async Task ClaimAsync_ClaimedButUndated_IsTakeable()
    {
        using var f = new SqliteTestFactory();
        var id = SeedClaimedTask(f, "bob", claimedAt: null);
        var svc = new TaskService(f, Ttl);

        await svc.ClaimAsync(id, "alice");

        Assert.Equal("alice", (await Reload(f, id)).ClaimedBy);
    }

    [Fact]
    public async Task ClaimAsync_OwnStaleClaim_IsIdempotent_AndEmitsNoStealEvent()
    {
        using var f = new SqliteTestFactory();
        var id = SeedClaimedTask(f, "alice", DateTime.UtcNow.AddHours(-9));
        var svc = new TaskService(f, Ttl);

        await svc.ClaimAsync(id, "alice");

        Assert.Equal("alice", (await Reload(f, id)).ClaimedBy);
        await using var db = await f.CreateDbContextAsync();
        Assert.Empty(await db.ActivityEvents
            .Where(e => e.EntityId == id && e.EventType == "TaskClaimStolen").ToListAsync());
    }

    [Fact]
    public async Task ClaimAsync_UnclaimedTask_StillEmitsTaskClaimed_NotStolen()
    {
        using var f = new SqliteTestFactory();
        var id = SeedClaimedTask(f, claimedBy: null, claimedAt: null);
        var svc = new TaskService(f, Ttl);

        await svc.ClaimAsync(id, "alice");

        await using var db = await f.CreateDbContextAsync();
        var events = await db.ActivityEvents.Where(e => e.EntityId == id).ToListAsync();
        Assert.Single(events, e => e.EventType == "TaskClaimed");
        Assert.DoesNotContain(events, e => e.EventType == "TaskClaimStolen");
    }

    // ── the race ───────────────────────────────────────────────────────────────

    /// <summary>
    /// End-to-end: two actors both attempting to take over the SAME expired claim leave exactly
    /// one holder and exactly one audited steal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <strong>This does NOT prove the conditional UPDATE's race safety, and the name says so
    /// deliberately.</strong> Measured, not assumed: replacing the widened <c>WHERE</c> with a
    /// bare <c>t.Id == taskId</c> — no guard at all, so both stealers' UPDATEs match — leaves this
    /// test <em>green</em>. The single winner comes from the pre-check instead: each
    /// <c>ClaimAsync</c> opens a transaction, so the two calls serialize, and the loser re-reads a
    /// now-fresh claim and throws <see cref="TaskClaimConflictException"/> before reaching the
    /// UPDATE.
    /// </para>
    /// <para>
    /// So this asserts the observable outcome (one holder, one steal event, no double-audit),
    /// which is worth guarding — but proving the UPDATE predicate itself discriminates needs a
    /// forced interleaving, i.e. the rendezvous harness in <c>TaskDependencyConcurrencyTests</c>
    /// that pauses one writer mid-transaction. That is recorded rather than implied, because a
    /// concurrency test whose pass does not depend on the guard it names is fake coverage (#347).
    /// </para>
    /// <para>
    /// Uses <see cref="SqliteFileTestFactory"/> regardless: <see cref="SqliteTestFactory"/> holds
    /// one shared <c>:memory:</c> connection and SQLite locking is per-connection, so a race
    /// written against it is serialized by the connection itself (#344).
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ConcurrentSteals_LeaveExactlyOneHolder_AndOneAuditedSteal()
    {
        using var f = new SqliteFileTestFactory();

        int id;
        await using (var db = await f.CreateDbContextAsync())
        {
            var project = new Project { Name = "P" };
            var feature = new Feature { Name = "F", Project = project };
            var task = new TaskItem
            {
                Name = "contested",
                Feature = feature,
                Status = WorkStatus.Todo,
                ClaimedBy = "crashed-agent",
                ClaimedAt = DateTime.UtcNow.AddHours(-9)
            };
            db.Add(task);
            await db.SaveChangesAsync();
            id = task.Id;
        }

        var svc = new TaskService(f, Ttl);

        var results = await Task.WhenAll(
            Attempt(() => svc.ClaimAsync(id, "alice")),
            Attempt(() => svc.ClaimAsync(id, "bob")));

        // Exactly one succeeded; the other saw a conflict. See the remarks: this outcome is
        // produced by the pre-check under serialization, NOT by the UPDATE predicate.
        Assert.Equal(1, results.Count(r => r));

        await using var verify = await f.CreateDbContextAsync();
        var holder = await verify.Tasks.AsNoTracking().Where(t => t.Id == id).Select(t => t.ClaimedBy).FirstAsync();
        Assert.Contains(holder, new[] { "alice", "bob" });

        // ...and exactly one steal was audited, not two.
        Assert.Single(await verify.ActivityEvents
            .Where(e => e.EntityId == id && e.EventType == "TaskClaimStolen").ToListAsync());

        static async Task<bool> Attempt(Func<Task<TaskItem>> act)
        {
            try { await act(); return true; }
            catch (TaskClaimConflictException) { return false; }
        }
    }
}
