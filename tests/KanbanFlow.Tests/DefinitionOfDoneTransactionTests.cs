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
/// These tests assert the <em>invariant</em> — both statements execute inside the same open
/// transaction — rather than trying to win a timing race. That choice is deliberate: per #347 a
/// race test whose barrier timeout is also its success path cannot discriminate, so it passes
/// against a serialized and an unserialized subject alike. A span probe cannot do that: remove
/// the transaction and <see cref="DbCommand.Transaction"/> is null, which is directly observable.
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
                lock (_lock) _observations.Add(("TaskUpdate", command.Transaction));
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
    }

    [Fact]
    public async Task ChangeStatusAsync_ReadsCriteriaAndWritesStatus_InOneTransaction()
    {
        var probe = new GateSpanProbe();
        using var f = new SqliteFileTestFactory(probe);
        var id = SeedTaskWithOneMetCriterion(f);

        await new TaskService(f).ChangeStatusAsync(id, WorkStatus.Done, "tester");

        AssertGateAndWriteShareOneTransaction(probe);
    }

    [Fact]
    public async Task ToggleDoneAsync_ReadsCriteriaAndWritesStatus_InOneTransaction()
    {
        var probe = new GateSpanProbe();
        using var f = new SqliteFileTestFactory(probe);
        var id = SeedTaskWithOneMetCriterion(f);

        await new TaskService(f).ToggleDoneAsync(id, "tester");

        AssertGateAndWriteShareOneTransaction(probe);
    }

    [Fact]
    public async Task UpdateAsync_ReadsCriteriaAndWritesStatus_InOneTransaction()
    {
        var probe = new GateSpanProbe();
        using var f = new SqliteFileTestFactory(probe);
        var id = SeedTaskWithOneMetCriterion(f);

        await new TaskService(f).UpdateAsync(id, new TaskUpdate(Status: WorkStatus.Done), "tester");

        AssertGateAndWriteShareOneTransaction(probe);
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

        await new TaskService(f).MoveOnBoardAsync(id, WorkStatus.Done, projectId, 0, "tester");

        AssertGateAndWriteShareOneTransaction(probe);
    }
}
