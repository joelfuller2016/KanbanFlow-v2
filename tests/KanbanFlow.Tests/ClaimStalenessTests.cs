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

    /// <summary>The staleness boundary is exact: strictly past the TTL is stale; at or before is fresh.</summary>
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

    /// <summary>An unclaimed task is never stale regardless of age.</summary>
    [Fact]
    public void IsStale_UnclaimedTask_IsNeverStale()
    {
        var now = DateTime.UtcNow;
        Assert.False(ClaimPolicy.IsStale(null, null, now, Ttl));
        Assert.False(ClaimPolicy.IsStale("   ", now.AddYears(-1), now, Ttl));
    }

    /// <summary>A claimed task with no ClaimedAt timestamp is treated as stale, not as a permanent claim.</summary>
    [Fact]
    public void IsStale_ClaimedButUndated_IsStale_NotPermanent()
    {
        // The combination ClaimAsync never writes, but a legacy row or a direct DB edit could.
        // Treating it as fresh would make that claim permanent — the exact failure #429 removes.
        Assert.True(ClaimPolicy.IsStale("alice", claimedAt: null, DateTime.UtcNow, Ttl));
    }

    // ── ranking: a stale claim stops sinking the task ──────────────────────────

    /// <summary>A stale claim by another actor does not de-prioritise the task in ranking; the stale task rises above the fresh-claimed one.</summary>
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

    /// <summary>A fresh claim held by another actor still throws <see cref="TaskClaimConflictException"/>.</summary>
    [Fact]
    public async Task ClaimAsync_FreshClaimByAnother_StillConflicts()
    {
        using var f = new SqliteTestFactory();
        var id = SeedClaimedTask(f, "bob", DateTime.UtcNow.AddMinutes(-5));
        var svc = new TaskService(f, Ttl);

        await Assert.ThrowsAsync<TaskClaimConflictException>(() => svc.ClaimAsync(id, "alice"));
        Assert.Equal("bob", (await Reload(f, id)).ClaimedBy);
    }

    /// <summary>A stale claim is silently taken over, the new holder is persisted, and a <c>TaskClaimStolen</c> event is audited with the prior holder's name and claim age.</summary>
    [Fact]
    public async Task ClaimAsync_StaleClaim_IsTakenOver_AndAuditedAsStolen()
    {
        using var f = new SqliteTestFactory();
        var fixedNow = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);
        var seededAt = fixedNow.AddHours(-9);
        var id = SeedClaimedTask(f, "bob", seededAt);
        var svc = new TaskService(f, Ttl, clock: () => fixedNow);

        var expectedAge = $"{(fixedNow - seededAt).TotalHours:0.##}";
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
        Assert.Contains($"{expectedAge}h old", stolen.Message);     // claim age is included, not just the TTL
        Assert.Equal("alice", stolen.Actor);
        Assert.DoesNotContain(events, e => e.EventType == "TaskClaimed");
    }

    /// <summary>A task claimed with no <c>ClaimedAt</c> (counted as stale) is takeable by another actor, and the steal is audited as <c>TaskClaimStolen</c> with a no-timestamp message.</summary>
    [Fact]
    public async Task ClaimAsync_ClaimedButUndated_IsTakeable()
    {
        using var f = new SqliteTestFactory();
        var id = SeedClaimedTask(f, "bob", claimedAt: null);
        var svc = new TaskService(f, Ttl);

        await svc.ClaimAsync(id, "alice");

        Assert.Equal("alice", (await Reload(f, id)).ClaimedBy);

        await using var db = await f.CreateDbContextAsync();
        var events = await db.ActivityEvents
            .Where(e => e.EntityType == "Task" && e.EntityId == id)
            .ToListAsync();
        var stolen = Assert.Single(events, e => e.EventType == "TaskClaimStolen");
        Assert.Contains("bob", stolen.Message);
        Assert.Contains("alice", stolen.Message);
        Assert.Contains("no timestamp", stolen.Message);
    }

    /// <summary>
    /// Reclaiming your OWN already-expired claim recovers it (#443): the lease is renewed rather
    /// than left to be stolen by the next caller. It is audited as <c>TaskClaimRenewed</c> -- never
    /// as <c>TaskClaimStolen</c>, because nothing was taken from anybody.
    /// </summary>
    [Fact]
    public async Task ClaimAsync_OwnStaleClaim_IsRenewed_AndAuditedAsRenewed_NotStolen()
    {
        using var f = new SqliteTestFactory();
        var fixedNow = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);
        var seededAt = fixedNow.AddHours(-9);            // 9h old against a 1h TTL: stale
        var id = SeedClaimedTask(f, "alice", seededAt);
        var svc = new TaskService(f, Ttl, clock: () => fixedNow);

        var beforeReclaim = await Reload(f, id);
        await svc.ClaimAsync(id, "alice");

        var afterReclaim = await Reload(f, id);
        Assert.Equal("alice", afterReclaim.ClaimedBy);
        // Before #443 both of these asserted equality -- the holder could not renew its own lease.
        Assert.Equal(fixedNow, afterReclaim.ClaimedAt);
        Assert.NotEqual(beforeReclaim.ClaimedAt, afterReclaim.ClaimedAt);
        // Recovering an EXPIRED claim is a real state change, so UpdatedAt does move here.
        Assert.Equal(fixedNow, afterReclaim.UpdatedAt);

        await using var db = await f.CreateDbContextAsync();
        Assert.Empty(await db.ActivityEvents
            .Where(e => e.EntityId == id && e.EventType == "TaskClaimStolen").ToListAsync());
        var renewed = Assert.Single(await db.ActivityEvents
            .Where(e => e.EntityId == id && e.EventType == "TaskClaimRenewed").ToListAsync());
        Assert.Contains("alice", renewed.Message);
    }

    /// <summary>
    /// THE #443 HEADLINE. An agent that heartbeats inside the TTL is never stolen from, however long
    /// it works. This is the test the issue's own plan would NOT have made pass: under a stale-only
    /// renewal every heartbeat lands in the fresh no-op branch, ClaimedAt never moves, and bob wins
    /// the moment the original claim ages past the TTL.
    /// </summary>
    [Fact]
    public async Task ClaimAsync_HeartbeatInsideTtl_KeepsTheLeaseAlive_FarBeyondTheTtl()
    {
        using var f = new SqliteTestFactory();
        var now = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);
        var id = SeedClaimedTask(f, claimedBy: null, claimedAt: null);
        var svc = new TaskService(f, Ttl, clock: () => now);

        await svc.ClaimAsync(id, "alice");

        // Beat at half the TTL until FOUR TTLs of wall time have passed.
        var beat = Ttl / 2;
        for (var elapsed = TimeSpan.Zero; elapsed < Ttl * 4; elapsed += beat)
        {
            now += beat;
            await svc.ClaimAsync(id, "alice");
        }

        // alice is demonstrably alive, so bob must still be refused.
        await Assert.ThrowsAsync<TaskClaimConflictException>(() => svc.ClaimAsync(id, "bob"));
        Assert.Equal("alice", (await Reload(f, id)).ClaimedBy);

        // And the heartbeat must not have flooded the timeline: one TaskClaimed, no renewals
        // (every beat was fresh), no steals.
        await using var db = await f.CreateDbContextAsync();
        Assert.Equal(1, await db.ActivityEvents
            .CountAsync(e => e.EntityId == id && e.EventType == "TaskClaimed"));
        Assert.Equal(0, await db.ActivityEvents
            .CountAsync(e => e.EntityId == id && e.EventType == "TaskClaimRenewed"));
        Assert.Equal(0, await db.ActivityEvents
            .CountAsync(e => e.EntityId == id && e.EventType == "TaskClaimStolen"));
    }

    /// <summary>Claiming an unclaimed task still emits <c>TaskClaimed</c>, not <c>TaskClaimStolen</c>.</summary>
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

    /// <summary>A task with a whitespace-only <c>ClaimedBy</c> (legacy/direct-DB row) is treated as unclaimed: another actor can claim it and the event is <c>TaskClaimed</c>, not <c>TaskClaimStolen</c>.</summary>
    [Fact]
    public async Task ClaimAsync_WhitespaceHolder_IsTreatedAsUnclaimed()
    {
        // ClaimPolicy.IsStale returns false for IsNullOrWhiteSpace holders, so such a task
        // ranks and behaves as unclaimed. The conditional UPDATE predicate must match the
        // exact pre-read value (t.ClaimedBy == " "), not only NULL, or the UPDATE matches
        // zero rows and throws TaskClaimConflictException on a task that should be claimable.
        using var f = new SqliteTestFactory();
        var id = SeedClaimedTask(f, " ", claimedAt: null);
        var svc = new TaskService(f, Ttl);

        await svc.ClaimAsync(id, "alice");

        Assert.Equal("alice", (await Reload(f, id)).ClaimedBy);
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
