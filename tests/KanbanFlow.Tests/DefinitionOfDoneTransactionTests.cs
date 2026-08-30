using System.Data.Common;
using KanbanFlow.Models;
using KanbanFlow.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace KanbanFlow.Tests;

/// <summary>
/// Proves the Definition-of-Done gate's read and the status write are ONE serialized unit (#428).
/// </summary>
/// <remarks>
/// <para>
/// The gate reads <see cref="AcceptanceCriterion"/> rows and the status lands later at
/// <c>SaveChangesAsync</c>. Without a write lock spanning both, a concurrent criterion insert can
/// commit in between and <c>Done</c> persists against criteria the gate never saw — the task is
/// complete with an unmet criterion and nothing records that it happened.
/// </para>
/// <para>
/// These tests assert one specific invariant — both statements execute inside the <em>same open
/// transaction</em> — rather than trying to win a timing race. That choice is deliberate: per #347
/// a race test whose barrier timeout is also its success path cannot discriminate, so it passes
/// against a serialized and an unserialized subject alike. A span probe cannot do that: remove the
/// transaction and <see cref="DbCommand.Transaction"/> is null, which is directly observable.
/// </para>
/// <para>
/// ⚠️ <strong>What this does NOT prove.</strong> A shared transaction is necessary but not
/// sufficient for serialization: a <em>deferred</em> SQLite transaction would satisfy every
/// assertion here while still letting another connection reserve the writer slot after the
/// criteria read, so the subject would fail later on lock upgrade with SQLITE_BUSY. The
/// implementation relies on <c>BeginTransactionAsync()</c> issuing BEGIN IMMEDIATE (the provider
/// only defers when the caller passes <c>deferred: true</c>). Proving the reservation is immediate
/// needs the rendezvous harness in <c>TaskDependencyConcurrencyTests</c>; that is deliberately not
/// duplicated here, and this limitation is stated rather than papered over.
/// </para>
/// <para>
/// Uses <see cref="SqliteFileTestFactory"/>, not <see cref="SqliteTestFactory"/>: the latter holds
/// one shared <c>:memory:</c> connection, and SQLite locking is per-connection, so anything written
/// against it is serialized by the connection rather than by the code under test (#344).
/// </para>
/// </remarks>
public sealed class DefinitionOfDoneTransactionTests
{
    /// <summary>
    /// Records, for every SQL statement touching acceptance criteria or the task row, which
    /// transaction (if any) it executed under.
    /// </summary>
    private sealed class GateSpanProbe : DbCommandInterceptor
    {
        private readonly Lock _lock = new();
        private readonly List<(string Kind, DbTransaction? Tx)> _observations = [];

        public IReadOnlyList<(string Kind, DbTransaction? Tx)> Observations
        {
            get { lock (_lock) return _observations.ToArray(); }
        }

        /// <summary>
        /// Discards everything observed so far. Called after seeding and after any set-up query,
        /// so the assertions see ONLY the method under test — otherwise a test's own preparatory
        /// SELECT (which legitimately runs outside a transaction) is indistinguishable from the
        /// subject failing to open one.
        /// </summary>
        public void Reset() { lock (_lock) _observations.Clear(); }

        private void Observe(DbCommand command)
        {
            var sql = command.CommandText;

            // The gate's read: any SELECT against AcceptanceCriteria.
            if (sql.Contains("AcceptanceCriteria", StringComparison.OrdinalIgnoreCase)
                && sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                lock (_lock) _observations.Add(("CriteriaRead", command.Transaction));
                return;
            }

            // The status write: an UPDATE against Tasks.
            if (sql.Contains("UPDATE \"Tasks\"", StringComparison.OrdinalIgnoreCase))
            {
                lock (_lock) _observations.Add(("TaskUpdate", command.Transaction));
                return;
            }

            // The entity load at the top of the method. This is the discriminator for whether an
            // EXPLICIT transaction was opened: EF wraps SaveChanges in an implicit transaction of
            // its own, so a non-null Transaction on the UPDATE proves nothing. Only a transaction
            // that is already open at this first read can be one the method opened itself.
            if (sql.Contains("FROM \"Tasks\"", StringComparison.OrdinalIgnoreCase)
                && sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
                lock (_lock) _observations.Add(("TaskRead", command.Transaction));
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Observe(command);
            return new ValueTask<InterceptionResult<DbDataReader>>(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Observe(command);
            return new ValueTask<InterceptionResult<int>>(result);
        }
    }

    private static int SeedTaskWithOneMetCriterion(SqliteFileTestFactory f)
    {
        using var db = f.CreateDbContext();
        var project = new Project { Name = "Proj" };
        var feature = new Feature { Name = "Feat", Project = project };
        var task = new TaskItem { Name = "Task", Feature = feature, Status = WorkStatus.InProgress };
        db.Add(task);
        db.SaveChanges();

        // Completed, so the transition SUCCEEDS — a refused transition never reaches the write,
        // and this test is about the read and the write sharing one transaction.
        db.AcceptanceCriteria.Add(new AcceptanceCriterion
        {
            TaskItemId = task.Id,
            Text = "done already",
            Completed = true,
            SortOrder = 0
        });
        db.SaveChanges();
        return task.Id;
    }

    /// <summary>
    /// Asserts the criteria read and the task update both ran, both inside a transaction, and
    /// inside the SAME one.
    /// </summary>
    private static void AssertGateAndWriteShareOneTransaction(GateSpanProbe probe)
    {
        var reads = probe.Observations.Where(o => o.Kind == "CriteriaRead").ToArray();
        var writes = probe.Observations.Where(o => o.Kind == "TaskUpdate").ToArray();

        Assert.NotEmpty(reads);   // guards against the probe silently matching nothing
        Assert.NotEmpty(writes);

        // The whole point: an unserialized implementation runs these with Transaction == null.
        Assert.All(reads, r => Assert.NotNull(r.Tx));
        Assert.All(writes, w => Assert.NotNull(w.Tx));

        // ...and the same transaction object, not merely two separate ones.
        var tx = reads[0].Tx;
        Assert.All(reads, r => Assert.Same(tx, r.Tx));
        Assert.All(writes, w => Assert.Same(tx, w.Tx));

        // The transaction must already be open at the FIRST entity load, not started later at
        // SaveChanges. EF opens an implicit transaction for SaveChanges, so without this the
        // assertions above could be satisfied by EF's own transaction rather than an explicit
        // span the method opened before its reads.
        var taskReads = probe.Observations.Where(o => o.Kind == "TaskRead").ToArray();
        Assert.NotEmpty(taskReads);
        Assert.All(taskReads, r => Assert.Same(tx, r.Tx));
    }

    [Fact]
    public async Task ChangeStatusAsync_ReadsCriteriaAndWritesStatus_InOneTransaction()
    {
        var probe = new GateSpanProbe();
        using var f = new SqliteFileTestFactory(probe);
        var id = SeedTaskWithOneMetCriterion(f);

        probe.Reset();
        await new TaskService(f).ChangeStatusAsync(id, WorkStatus.Done, "tester");

        AssertGateAndWriteShareOneTransaction(probe);
    }

    [Fact]
    public async Task ToggleDoneAsync_ReadsCriteriaAndWritesStatus_InOneTransaction()
    {
        var probe = new GateSpanProbe();
        using var f = new SqliteFileTestFactory(probe);
        var id = SeedTaskWithOneMetCriterion(f);

        probe.Reset();
        await new TaskService(f).ToggleDoneAsync(id, "tester");

        AssertGateAndWriteShareOneTransaction(probe);
    }

    [Fact]
    public async Task UpdateAsync_ReadsCriteriaAndWritesStatus_InOneTransaction()
    {
        var probe = new GateSpanProbe();
        using var f = new SqliteFileTestFactory(probe);
        var id = SeedTaskWithOneMetCriterion(f);

        probe.Reset();
        await new TaskService(f).UpdateAsync(id, new TaskUpdate(Status: WorkStatus.Done), "tester");

        AssertGateAndWriteShareOneTransaction(probe);
    }

    /// <summary>
    /// The write lock is scoped to Done-bound transitions. SQLite permits a single pending writer,
    /// so locking on every status change or every field edit would make unrelated work queue behind
    /// any other writer for a gate that never runs. A non-Done transition must not take it.
    /// </summary>
    [Fact]
    public async Task NonDoneTransition_DoesNotOpenATransaction()
    {
        var probe = new GateSpanProbe();
        using var f = new SqliteFileTestFactory(probe);
        var id = SeedTaskWithOneMetCriterion(f);

        probe.Reset();
        await new TaskService(f).ChangeStatusAsync(id, WorkStatus.Review, "tester");

        // No explicit transaction was open when the entity was loaded.
        var reads = probe.Observations.Where(o => o.Kind == "TaskRead").ToArray();
        Assert.NotEmpty(reads);
        Assert.All(reads, r => Assert.Null(r.Tx));

        // ...and the gate never touched AcceptanceCriteria, so there was nothing to serialize.
        Assert.Empty(probe.Observations.Where(o => o.Kind == "CriteriaRead"));

        // The transition still happened — this is not passing because nothing ran.
        Assert.NotEmpty(probe.Observations.Where(o => o.Kind == "TaskUpdate"));
    }

    /// <summary>A field-only edit must not take the writer lock either — see <see cref="UpdateAsync"/>.</summary>
    [Fact]
    public async Task UpdateAsync_WithoutADoneStatusChange_DoesNotOpenATransaction()
    {
        var probe = new GateSpanProbe();
        using var f = new SqliteFileTestFactory(probe);
        var id = SeedTaskWithOneMetCriterion(f);

        probe.Reset();
        await new TaskService(f).UpdateAsync(id, new TaskUpdate(Name: "renamed"), "tester");

        var reads = probe.Observations.Where(o => o.Kind == "TaskRead").ToArray();
        Assert.NotEmpty(reads);
        Assert.All(reads, r => Assert.Null(r.Tx));
        Assert.Empty(probe.Observations.Where(o => o.Kind == "CriteriaRead"));
        Assert.NotEmpty(probe.Observations.Where(o => o.Kind == "TaskUpdate"));
    }

    [Fact]
    public async Task MoveOnBoardAsync_ReadsCriteriaAndWritesStatus_InOneTransaction()
    {
        var probe = new GateSpanProbe();
        using var f = new SqliteFileTestFactory(probe);
        var id = SeedTaskWithOneMetCriterion(f);

        int projectId;
        await using (var db = await f.CreateDbContextAsync())
            projectId = await db.Tasks.Where(t => t.Id == id).Select(t => t.Feature!.ProjectId).FirstAsync();

        probe.Reset();
        await new TaskService(f).MoveOnBoardAsync(id, WorkStatus.Done, projectId, 0, "tester");

        AssertGateAndWriteShareOneTransaction(probe);
    }
}
