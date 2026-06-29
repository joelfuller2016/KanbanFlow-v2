using KanbanFlow.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Tests;

/// <summary>
/// An <see cref="IDbContextFactory{TContext}"/> backed by a single open in-memory
/// SQLite connection. This is REAL SQLite with the REAL EF Core schema from
/// <see cref="AppDbContext"/> — every test exercises the actual provider,
/// relationships, and cascade rules, not an in-memory fake provider.
///
/// The connection is opened once and held for the lifetime of the instance so the
/// in-memory database survives across the multiple short-lived contexts that
/// <see cref="KanbanFlow.Services.TaskService"/> creates per call. Each instance is
/// an isolated database; <see cref="Dispose"/> drops it.
/// </summary>
public sealed class SqliteTestFactory : IDbContextFactory<AppDbContext>, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public SqliteTestFactory()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var ctx = new AppDbContext(_options);
        ctx.Database.EnsureCreated();

        // EnsureCreated() builds the EF-modeled tables, but the FTS5 keyword index (#223) is a virtual
        // table outside the EF model — create it on the shared open connection (the same code path
        // Program.cs runs at startup) so MemoryService's dual-write and MemorySearch have a table to
        // hit. Use the DbConnection overload directly on the already-open connection (the AppDbContext
        // overload would open/close via EF, and closing this in-memory connection would drop the DB).
        SchemaPatcher.EnsureMemoryFtsTable(_connection);
    }

    public AppDbContext CreateDbContext() => new(_options);

    public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new AppDbContext(_options));

    /// <summary>The shared options (same in-memory connection) — lets advanced tests build a
    /// custom <see cref="AppDbContext"/> subclass against the same database.</summary>
    public DbContextOptions<AppDbContext> Options => _options;

    public void Dispose() => _connection.Dispose();
}
