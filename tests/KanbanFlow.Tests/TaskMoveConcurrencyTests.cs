using System.Data.Common;
using KanbanFlow.Data;
using KanbanFlow.Models;
using KanbanFlow.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace KanbanFlow.Tests;

/// <summary>
/// Concurrency coverage for the identity-anchored move paths (#307, PR #325 review).
///
/// <para>A move is a read-modify-write: it reads the destination scope, derives a rank from the
/// neighbours around the anchor, then writes that rank. If nothing holds a lock across those steps,
/// two moves aimed at the same gap both read the same neighbours, both compute the same midpoint,
/// and both persist it. Neither write fails — the rows are simply left with identical
/// <c>SortOrder</c>, which <see cref="TaskOrdering"/> can only separate by Id, so one user's drop
/// silently lands somewhere they did not choose.</para>
///
/// <para>These tests run against <see cref="SqliteFileTestFactory"/> (one connection per context)
/// rather than <see cref="SqliteTestFactory"/> (one shared connection), because a shared connection
/// serializes everything by itself and would make the guard unfailable.</para>
/// </summary>
public sealed class TaskMoveConcurrencyTests
{
    /// <summary>
    /// Holds every context at its k-th read until all <paramref name="parties"/> contexts have
    /// completed their own k-th read — or until <paramref name="timeout"/> elapses.
    ///
    /// <para>This pins the interleaving instead of hoping for one. Unserialized, both movers advance
    /// in lockstep through every query, so they are guaranteed to hold the same stale view of the
    /// destination scope when they compute a rank. Serialized, the second mover is still blocked
    /// acquiring the write lock and never arrives, so the first waits out the timeout at each
    /// barrier and commits — correct behaviour, which is why a timeout must be a pass, not a
    /// failure.</para>
    ///
    /// <para>Barriering on the read <em>ordinal</em> rather than on a recognised SQL string keeps
    /// this independent of how many queries a given move path happens to issue. An earlier version
    /// synchronised only the first read; the outline path issues an extra lookup before its scope
    /// read, which let the two movers drift apart and made the outline guard pass even against a
    /// deliberately unserialized subject.</para>
    /// </summary>
    private sealed class ReadOrdinalBarrier(int parties, TimeSpan timeout) : DbCommandInterceptor
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<DbContext, int> _readsPerContext = [];
        private readonly Dictionary<int, BarrierSlot> _barriers = [];

        private sealed class BarrierSlot
        {
            public int Arrived;
            public readonly TaskCompletionSource Reached =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context is null)
                return result;

            BarrierSlot slot;
            lock (_lock)
            {
                _readsPerContext.TryGetValue(eventData.Context, out var ordinal);
                ordinal++;
                _readsPerContext[eventData.Context] = ordinal;

                if (!_barriers.TryGetValue(ordinal, out var existing))
                {
                    existing = new BarrierSlot();
                    _barriers[ordinal] = existing;
                }

                slot = existing;
                slot.Arrived++;
                if (slot.Arrived >= parties)
                    slot.Reached.TrySetResult();
            }

            await Task.WhenAny(slot.Reached.Task, Task.Delay(timeout, cancellationToken));
            return result;
        }
    }

    private sealed record RaceSeed(int ProjectId, int FeatureId, int LowerId, int UpperId, int FirstMoverId, int SecondMoverId);

    /// <summary>
    /// Todo column: lower(0) … upper(10), with both movers parked after it. Both then target the
    /// gap immediately before <c>upper</c>, so an unserialized pair computes the same midpoint (5).
    /// </summary>
    private static RaceSeed SeedContestedGap(SqliteFileTestFactory factory)
    {
        using var db = factory.CreateDbContext();
        var project = new Project { Name = "Race project" };
        db.Projects.Add(project);
        db.SaveChanges();

        var feature = new Feature { ProjectId = project.Id, Name = "Race feature" };
        db.Features.Add(feature);
        db.SaveChanges();

        var lower = new TaskItem { FeatureId = feature.Id, Name = "lower", Status = WorkStatus.Todo, SortOrder = 0 };
        var upper = new TaskItem { FeatureId = feature.Id, Name = "upper", Status = WorkStatus.Todo, SortOrder = 10 };
        var firstMover = new TaskItem { FeatureId = feature.Id, Name = "first mover", Status = WorkStatus.Todo, SortOrder = 20 };
        var secondMover = new TaskItem { FeatureId = feature.Id, Name = "second mover", Status = WorkStatus.Todo, SortOrder = 30 };
        db.Tasks.AddRange(lower, upper, firstMover, secondMover);
        db.SaveChanges();

        return new RaceSeed(project.Id, feature.Id, lower.Id, upper.Id, firstMover.Id, secondMover.Id);
    }

    private static async Task<Dictionary<int, double>> Ranks(SqliteFileTestFactory factory, params int[] ids)
    {
        await using var db = factory.CreateDbContext();
        return await db.Tasks.AsNoTracking()
            .Where(t => ids.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.SortOrder);
    }

    [Fact]
    public async Task ConcurrentBoardMovesIntoTheSameGap_DoNotAllocateTheSameRank()
    {
        var barrier = new ReadOrdinalBarrier(parties: 2, timeout: TimeSpan.FromSeconds(1.5));
        using var factory = new SqliteFileTestFactory(barrier);
        var seed = SeedContestedGap(factory);
        var service = new TaskService(factory);

        var first = service.MoveOnBoardAsync(
            seed.FirstMoverId, WorkStatus.Todo, seed.ProjectId, seed.UpperId, "racer-a");
        var second = service.MoveOnBoardAsync(
            seed.SecondMoverId, WorkStatus.Todo, seed.ProjectId, seed.UpperId, "racer-b");

        await Task.WhenAll(first, second);

        var ranks = await Ranks(factory, seed.FirstMoverId, seed.SecondMoverId, seed.LowerId, seed.UpperId);

        Assert.NotEqual(ranks[seed.FirstMoverId], ranks[seed.SecondMoverId]);

        // Bystanders keep their exact persisted ranks (#307's core invariant).
        Assert.Equal(0d, ranks[seed.LowerId]);
        Assert.Equal(10d, ranks[seed.UpperId]);

        // Both movers honoured the anchor: each landed strictly between lower and upper.
        foreach (var moverId in new[] { seed.FirstMoverId, seed.SecondMoverId })
        {
            Assert.True(ranks[moverId] > 0d && ranks[moverId] < 10d,
                $"mover {moverId} rank {ranks[moverId]} is outside the requested gap (0, 10)");
        }

        // The resulting board order is total and unambiguous.
        await using var db = factory.CreateDbContext();
        var ordered = TaskOrdering
            .Order(await db.Tasks.AsNoTracking()
                .Where(t => t.Feature.ProjectId == seed.ProjectId && t.Status == WorkStatus.Todo)
                .ToListAsync())
            .Select(t => t.Id)
            .ToArray();
        Assert.Equal(seed.LowerId, ordered[0]);
        Assert.Equal(seed.UpperId, ordered[^1]);
    }

    [Fact]
    public async Task ConcurrentOutlineMovesIntoTheSameGap_DoNotAllocateTheSameRank()
    {
        var barrier = new ReadOrdinalBarrier(parties: 2, timeout: TimeSpan.FromSeconds(1.5));
        using var factory = new SqliteFileTestFactory(barrier);
        var seed = SeedContestedGap(factory);
        var service = new TaskService(factory);

        var first = service.MoveInOutlineAsync(
            seed.FirstMoverId, seed.FeatureId, seed.UpperId, "racer-a");
        var second = service.MoveInOutlineAsync(
            seed.SecondMoverId, seed.FeatureId, seed.UpperId, "racer-b");

        await Task.WhenAll(first, second);

        var ranks = await Ranks(factory, seed.FirstMoverId, seed.SecondMoverId, seed.LowerId, seed.UpperId);

        Assert.NotEqual(ranks[seed.FirstMoverId], ranks[seed.SecondMoverId]);
        Assert.Equal(0d, ranks[seed.LowerId]);
        Assert.Equal(10d, ranks[seed.UpperId]);
    }
}
