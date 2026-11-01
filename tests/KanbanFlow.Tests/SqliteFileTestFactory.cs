using KanbanFlow.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace KanbanFlow.Tests;

/// <summary>
/// A file-backed <see cref="IDbContextFactory{TContext}"/> where every context gets its OWN
/// connection — exactly how <c>Program.cs</c> wires <c>AddDbContextFactory(UseSqlite("Data
/// Source=..."))</c> in production.
///
/// <para>This exists because <see cref="SqliteTestFactory"/> cannot express concurrency: it holds a
/// single shared <c>:memory:</c> connection, and SQLite locking is per-connection, so two "parallel"
/// operations against it are trivially serialized by the one connection they share. Any race
/// regression test written against that factory would pass whether or not the code under test is
/// serialized, i.e. it would be a test that cannot fail. Cross-connection locking needs a real
/// database file.</para>
///
/// <para><c>Pooling=False</c> keeps each context on a genuinely distinct connection instead of one
/// recycled from the pool, so the lock contention under test is real.</para>
/// </summary>
public sealed class SqliteFileTestFactory : IDbContextFactory<AppDbContext>, IDisposable
{
    /// <summary>
    /// SQLite's busy timeout for every factory that doesn't override <c>commandTimeoutSeconds</c>.
    /// Any test that races two writers against one factory and bounds the race with its own watchdog
    /// must derive that watchdog from this constant instead of an independent number, so the two can
    /// never drift out of the order that makes a genuine stall diagnosable as SQLITE_BUSY rather than
    /// an ambiguous watchdog cancellation.
    /// </summary>
    public const int DefaultBusyTimeoutSeconds = 30;

    private readonly string _path;
    private readonly DbContextOptions<AppDbContext> _options;

    /// <param name="interceptor">Optional interceptor used to make an interleaving deterministic.</param>
    /// <param name="commandTimeoutSeconds">
    /// Maps to SQLite's busy timeout. Must exceed however long a test deliberately holds the write
    /// lock, otherwise the blocked writer fails with SQLITE_BUSY instead of serializing behind it.
    /// </param>
    public SqliteFileTestFactory(IInterceptor? interceptor = null, int commandTimeoutSeconds = DefaultBusyTimeoutSeconds)
        : this(
            interceptor is null ? Array.Empty<IInterceptor>() : new[] { interceptor },
            commandTimeoutSeconds)
    {
    }

    /// <summary>Creates a factory with multiple cooperating interceptors.</summary>
    /// <param name="interceptors">Interceptors attached after schema creation.</param>
    /// <param name="commandTimeoutSeconds">
    /// Maps to SQLite's busy timeout. Must exceed however long a test deliberately holds the write
    /// lock, otherwise the blocked writer fails with SQLITE_BUSY instead of serializing behind it.
    /// </param>
    public SqliteFileTestFactory(
        IReadOnlyCollection<IInterceptor> interceptors,
        int commandTimeoutSeconds = DefaultBusyTimeoutSeconds)
    {
        ArgumentNullException.ThrowIfNull(interceptors);

        _path = Path.Combine(Path.GetTempPath(), $"kanbanflow-test-{Guid.NewGuid():N}.db");

        var builder = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_path};Pooling=False;Default Timeout={commandTimeoutSeconds}");

        // Build the schema BEFORE attaching the interceptor so schema creation cannot trip a
        // rendezvous meant only for the operations under test.
        using (var ctx = new AppDbContext(builder.Options))
        {
            ctx.Database.EnsureCreated();
        }

        if (interceptors.Count > 0)
            builder = builder.AddInterceptors(interceptors);

        _options = builder.Options;
    }

    public AppDbContext CreateDbContext() => new(_options);

    /// <summary>Connection string for raw cross-connection locking probes.</summary>
    public string ConnectionString => $"Data Source={_path};Pooling=False";

    public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new AppDbContext(_options));

    public void Dispose()
    {
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
        catch (IOException)
        {
            // A test host that still holds the file is not a test failure; the temp file is disposable.
        }
    }
}
