using System.Data.Common;
using KanbanFlow.Data;
using KanbanFlow.Models;
using KanbanFlow.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace KanbanFlow.Tests;

/// <summary>
/// Cross-connection regression coverage for #414. The interceptor proves that the first
/// operation owns SQLite's write reservation before the second attempts its transaction;
/// no timeout ever releases the production operation and turns a broken path green.
/// </summary>
public sealed class TaskDependencyConcurrencyTests
{
    private sealed class ImmediateTransactionRendezvous : DbTransactionInterceptor
    {
        private readonly TaskCompletionSource _firstStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondStarting =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;
        private int _startingCount;
        private int _startedCount;
        private int _reservationProbeAttempted;
        private int _immediateReservationObserved;
        private string? _reservationProbeConnectionString;
        private Exception? _reservationProbeError;
        private readonly Lock _activeLock = new();
        private readonly HashSet<DbTransaction> _activeTransactions =
            new(ReferenceEqualityComparer.Instance);

        public Task FirstTransactionStarted => _firstStarted.Task;
        public int StartingCount => Volatile.Read(ref _startingCount);
        public int StartedCount => Volatile.Read(ref _startedCount);
        public bool ReservationProbeAttempted => Volatile.Read(ref _reservationProbeAttempted) == 1;
        public bool ImmediateReservationObserved => Volatile.Read(ref _immediateReservationObserved) == 1;
        public Exception? ReservationProbeError => _reservationProbeError;

        public void ConfigureReservationProbe(string connectionString) =>
            _reservationProbeConnectionString = connectionString;
        public void Arm() => Volatile.Write(ref _armed, 1);

        public bool IsActive(DbTransaction? transaction)
        {
            if (transaction is null)
                return false;

            lock (_activeLock)
                return _activeTransactions.Contains(transaction);
        }

        public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection,
            TransactionStartingEventData eventData,
            InterceptionResult<DbTransaction> result,
            CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _armed) == 1 &&
                Interlocked.Increment(ref _startingCount) == 2)
            {
                // The first call is still held inside TransactionStartedAsync and therefore owns
                // the immediate write reservation when this second attempt is observed.
                _secondStarting.TrySetResult();
            }

            return new ValueTask<InterceptionResult<DbTransaction>>(result);
        }

        public override async ValueTask<DbTransaction> TransactionStartedAsync(
            DbConnection connection,
            TransactionEndEventData eventData,
            DbTransaction result,
            CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _armed) == 1)
            {
                lock (_activeLock)
                    _activeTransactions.Add(result);

                if (Interlocked.Increment(ref _startedCount) == 1)
                {
                    ProbeImmediateWriteReservation();
                    _firstStarted.TrySetResult();
                    await _secondStarting.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            return result;
        }

        public override Task TransactionCommittedAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            MarkInactive(transaction);
            return Task.CompletedTask;
        }

        public override Task TransactionRolledBackAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            MarkInactive(transaction);
            return Task.CompletedTask;
        }

        public override Task TransactionFailedAsync(
            DbTransaction transaction,
            TransactionErrorEventData eventData,
            CancellationToken cancellationToken = default)
        {
            MarkInactive(transaction);
            return Task.CompletedTask;
        }

        private void MarkInactive(DbTransaction transaction)
        {
            lock (_activeLock)
                _activeTransactions.Remove(transaction);
        }

        private void ProbeImmediateWriteReservation()
        {
            Volatile.Write(ref _reservationProbeAttempted, 1);
            try
            {
                var builder = new SqliteConnectionStringBuilder(_reservationProbeConnectionString)
                {
                    Pooling = false,
                    // Zero means no timeout in Microsoft.Data.Sqlite. One second bounds this
                    // fail-only probe without ever releasing the production operation as a pass.
                    DefaultTimeout = 1
                };
                using var connection = new SqliteConnection(builder.ConnectionString);
                connection.Open();

                try
                {
                    using var competingTransaction = connection.BeginTransaction();
                    competingTransaction.Rollback();
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 5)
                {
                    // SQLITE_BUSY proves the first parameterless transaction already reserved
                    // SQLite's writer slot. A deferred transaction would let this probe begin.
                    Volatile.Write(ref _immediateReservationObserved, 1);
                }
            }
            catch (Exception ex)
            {
                _reservationProbeError = ex;
            }
        }
    }

    private sealed class DependencyTransactionSpanProbe(
        ImmediateTransactionRendezvous rendezvous) : DbCommandInterceptor
    {
        private readonly Lock _observationLock = new();
        private readonly List<CommandObservation> _graphReads = [];
        private readonly List<CommandObservation> _inserts = [];
        private int _armed;
        private int _commandsOutsideActiveTransaction;

        public sealed record CommandObservation(DbContext Context, DbTransaction Transaction);

        public IReadOnlyList<CommandObservation> GraphReads
        {
            get
            {
                lock (_observationLock)
                    return _graphReads.ToArray();
            }
        }

        public IReadOnlyList<CommandObservation> Inserts
        {
            get
            {
                lock (_observationLock)
                    return _inserts.ToArray();
            }
        }

        public int CommandsOutsideActiveTransaction =>
            Volatile.Read(ref _commandsOutsideActiveTransaction);

        public void Arm() => Volatile.Write(ref _armed, 1);
        public void Disarm() => Volatile.Write(ref _armed, 0);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Observe(command, eventData.Context);
            return new ValueTask<InterceptionResult<DbDataReader>>(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Observe(command, eventData.Context);
            return new ValueTask<InterceptionResult<int>>(result);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Observe(command, eventData.Context);
            return new ValueTask<InterceptionResult<object>>(result);
        }

        private void Observe(DbCommand command, DbContext? context)
        {
            if (Volatile.Read(ref _armed) == 0)
                return;

            var transaction = command.Transaction;
            var active = rendezvous.IsActive(transaction);
            if (!active)
                Interlocked.Increment(ref _commandsOutsideActiveTransaction);

            if (context is null || transaction is null)
                return;

            var sql = command.CommandText;
            var graphRead = sql.Contains("FROM \"TaskDependencies\" AS \"t\"", StringComparison.Ordinal) &&
                sql.Contains("\"t\".\"ProjectId\" =", StringComparison.Ordinal);
            var insert = sql.Contains("INSERT INTO \"TaskDependencies\"", StringComparison.Ordinal);
            if (!graphRead && !insert)
                return;

            var observation = new CommandObservation(context, transaction);
            lock (_observationLock)
            {
                if (graphRead)
                    _graphReads.Add(observation);
                if (insert)
                    _inserts.Add(observation);
            }
        }
    }

    private sealed record RaceOutcome(TaskDependencyCreateResult? Created, Exception? Error);

    private static int[] SeedTasks(SqliteFileTestFactory factory, params string[] names)
    {
        using var db = factory.CreateDbContext();
        var feature = new Feature
        {
            Name = "Race feature",
            Project = new Project { Name = $"Race project {Guid.NewGuid():N}" }
        };
        var tasks = names
            .Select(name => new TaskItem { Name = name, Feature = feature })
            .ToArray();

        db.Tasks.AddRange(tasks);
        db.SaveChanges();
        return tasks.Select(t => t.Id).ToArray();
    }

    private static async Task<RaceOutcome> CaptureAsync(Task<TaskDependencyCreateResult> operation)
    {
        try
        {
            return new RaceOutcome(await operation, Error: null);
        }
        catch (Exception ex)
        {
            return new RaceOutcome(Created: null, ex);
        }
    }

    private static async Task<RaceOutcome[]> RaceAsync(
        ImmediateTransactionRendezvous rendezvous,
        DependencyTransactionSpanProbe spanProbe,
        TaskDependencyService service,
        TaskDependencyCreate firstRequest,
        TaskDependencyCreate secondRequest)
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        rendezvous.Arm();
        spanProbe.Arm();

        var first = CaptureAsync(Task.Run(
            () => service.CreateAsync(firstRequest, watchdog.Token),
            watchdog.Token));
        await rendezvous.FirstTransactionStarted.WaitAsync(watchdog.Token);
        var second = CaptureAsync(Task.Run(
            () => service.CreateAsync(secondRequest, watchdog.Token),
            watchdog.Token));

        var outcomes = await Task.WhenAll(first, second).WaitAsync(watchdog.Token);
        spanProbe.Disarm();
        return outcomes;
    }

    private static void AssertTransactionSpan(
        ImmediateTransactionRendezvous rendezvous,
        DependencyTransactionSpanProbe spanProbe,
        int expectedGraphReads)
    {
        Assert.True(rendezvous.ReservationProbeAttempted);
        Assert.Null(rendezvous.ReservationProbeError);
        Assert.True(
            rendezvous.ImmediateReservationObserved,
            "The first transaction did not hold SQLite's immediate write reservation.");
        Assert.Equal(0, spanProbe.CommandsOutsideActiveTransaction);

        var graphReads = spanProbe.GraphReads;
        var insert = Assert.Single(spanProbe.Inserts);
        Assert.Equal(expectedGraphReads, graphReads.Count);
        Assert.Contains(graphReads, graphRead =>
            ReferenceEquals(graphRead.Context, insert.Context) &&
            ReferenceEquals(graphRead.Transaction, insert.Transaction));
    }

    private static void AssertOneCreateAndOneCycle(RaceOutcome[] outcomes)
    {
        Assert.Equal(2, outcomes.Length);
        Assert.Single(outcomes, outcome => outcome.Created is not null);

        var failure = Assert.Single(outcomes, outcome => outcome.Error is not null);
        var validation = Assert.IsType<ValidationException>(failure.Error);
        Assert.Equal("This dependency would create a cycle.", validation.Message);
    }

    private static void AssertOneCreateAndOneDuplicate(RaceOutcome[] outcomes)
    {
        Assert.Equal(2, outcomes.Length);
        Assert.Single(outcomes, outcome => outcome.Created is not null);

        var failure = Assert.Single(outcomes, outcome => outcome.Error is not null);
        var conflict = Assert.IsType<TaskDependencyAlreadyExistsException>(failure.Error);
        Assert.Equal("This task dependency already exists.", conflict.Message);
    }

    private static void AssertAcyclic(IReadOnlyCollection<TaskDependency> dependencies)
    {
        var edges = dependencies
            .GroupBy(d => d.TaskItemId)
            .ToDictionary(group => group.Key, group => group.Select(d => d.DependsOnTaskItemId).ToArray());
        var visiting = new HashSet<int>();
        var visited = new HashSet<int>();

        bool Visit(int taskId)
        {
            if (visiting.Contains(taskId))
                return false;
            if (!visited.Add(taskId))
                return true;

            visiting.Add(taskId);
            if (edges.TryGetValue(taskId, out var blockers))
            {
                foreach (var blocker in blockers)
                {
                    if (!Visit(blocker))
                        return false;
                }
            }

            visiting.Remove(taskId);
            return true;
        }

        var taskIds = dependencies
            .SelectMany(d => new[] { d.TaskItemId, d.DependsOnTaskItemId })
            .Distinct();
        Assert.True(taskIds.All(Visit), "The persisted task-dependency graph contains a cycle.");
    }

    [Fact]
    public async Task ConcurrentOppositeEdges_PersistExactlyOneAcyclicEdge()
    {
        var rendezvous = new ImmediateTransactionRendezvous();
        var spanProbe = new DependencyTransactionSpanProbe(rendezvous);
        using var factory = new SqliteFileTestFactory(
            new IInterceptor[] { rendezvous, spanProbe });
        rendezvous.ConfigureReservationProbe(factory.ConnectionString);
        var ids = SeedTasks(factory, "A", "B");
        var service = new TaskDependencyService(factory);

        var outcomes = await RaceAsync(
            rendezvous,
            spanProbe,
            service,
            new TaskDependencyCreate(ids[0], ids[1], TaskDependencyType.DependsOn),
            new TaskDependencyCreate(ids[1], ids[0], TaskDependencyType.DependsOn));

        AssertOneCreateAndOneCycle(outcomes);
        Assert.Equal(2, rendezvous.StartingCount);
        Assert.Equal(2, rendezvous.StartedCount);
        AssertTransactionSpan(rendezvous, spanProbe, expectedGraphReads: 2);

        using var db = factory.CreateDbContext();
        var persisted = await db.TaskDependencies.AsNoTracking().ToListAsync();
        Assert.Single(persisted);
        AssertAcyclic(persisted);
    }

    [Fact]
    public async Task ConcurrentIdenticalEdges_PersistOneAndReturnDomainConflict()
    {
        var rendezvous = new ImmediateTransactionRendezvous();
        var spanProbe = new DependencyTransactionSpanProbe(rendezvous);
        using var factory = new SqliteFileTestFactory(
            new IInterceptor[] { rendezvous, spanProbe });
        rendezvous.ConfigureReservationProbe(factory.ConnectionString);
        var ids = SeedTasks(factory, "blocked", "blocker");
        var service = new TaskDependencyService(factory);
        var request = new TaskDependencyCreate(
            ids[0],
            ids[1],
            TaskDependencyType.DependsOn);

        var outcomes = await RaceAsync(rendezvous, spanProbe, service, request, request);

        AssertOneCreateAndOneDuplicate(outcomes);
        Assert.Equal(2, rendezvous.StartingCount);
        Assert.Equal(2, rendezvous.StartedCount);
        AssertTransactionSpan(rendezvous, spanProbe, expectedGraphReads: 1);

        using var db = factory.CreateDbContext();
        var persisted = await db.TaskDependencies.AsNoTracking().ToListAsync();
        Assert.Single(persisted);
        AssertAcyclic(persisted);
    }

    [Fact]
    public async Task ConcurrentEdgesClosingTransitiveCycle_PersistOnlyOneCandidate()
    {
        var rendezvous = new ImmediateTransactionRendezvous();
        var spanProbe = new DependencyTransactionSpanProbe(rendezvous);
        using var factory = new SqliteFileTestFactory(
            new IInterceptor[] { rendezvous, spanProbe });
        rendezvous.ConfigureReservationProbe(factory.ConnectionString);
        var ids = SeedTasks(factory, "A", "B", "C");

        using (var seed = factory.CreateDbContext())
        {
            var projectId = seed.Tasks
                .Where(t => t.Id == ids[0])
                .Select(t => t.Feature.ProjectId)
                .Single();
            seed.TaskDependencies.Add(new TaskDependency
            {
                ProjectId = projectId,
                TaskItemId = ids[0],
                DependsOnTaskItemId = ids[1],
                Type = TaskDependencyType.DependsOn
            });
            seed.SaveChanges();
        }

        var service = new TaskDependencyService(factory);
        var outcomes = await RaceAsync(
            rendezvous,
            spanProbe,
            service,
            new TaskDependencyCreate(ids[1], ids[2], TaskDependencyType.DependsOn),
            new TaskDependencyCreate(ids[2], ids[0], TaskDependencyType.DependsOn));

        AssertOneCreateAndOneCycle(outcomes);
        Assert.Equal(2, rendezvous.StartingCount);
        Assert.Equal(2, rendezvous.StartedCount);
        AssertTransactionSpan(rendezvous, spanProbe, expectedGraphReads: 2);

        using var db = factory.CreateDbContext();
        var persisted = await db.TaskDependencies.AsNoTracking().ToListAsync();
        Assert.Equal(2, persisted.Count);
        Assert.Contains(persisted, d =>
            d.TaskItemId == ids[0] && d.DependsOnTaskItemId == ids[1]);
        var persistedBToC = persisted.Any(d =>
            d.TaskItemId == ids[1] && d.DependsOnTaskItemId == ids[2]);
        var persistedCToA = persisted.Any(d =>
            d.TaskItemId == ids[2] && d.DependsOnTaskItemId == ids[0]);
        Assert.NotEqual(persistedBToC, persistedCToA);
        AssertAcyclic(persisted);
    }
}
