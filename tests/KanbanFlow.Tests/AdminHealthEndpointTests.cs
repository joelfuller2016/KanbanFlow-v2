using System.Net;
using System.Text.Json;
using KanbanFlow.Data;
using KanbanFlow.Models;
using KanbanFlow.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KanbanFlow.Tests;

/// <summary>
/// <c>GET /api/admin/health</c> over REAL HTTP against a REAL SQLite file (#498).
///
/// <para>These run the mapped endpoint, not a helper next to it. That is deliberate: the defect
/// being fixed was that the route returned a constant, so a test that exercised an extracted probe
/// while the route still answered <c>status = "ok"</c> would be green against the bug itself. Each
/// test therefore boots a Kestrel host on <c>127.0.0.1:0</c>, calls <see cref="AdminEndpoints.MapAdminEndpoints"/>,
/// and asserts on the bytes that come back over the wire.</para>
///
/// <para><see cref="DbPathResolution"/> is injected per-test rather than resolved from the process,
/// which is what makes the interesting states reachable at all: a database this start created, a
/// second board shadowing the one opened, and a path that cannot be opened.</para>
/// </summary>
public sealed class AdminHealthEndpointTests
{
    /// <summary>Every field name that shipped before the database probe existed (#498 must be additive).</summary>
    private static readonly string[] LegacyFields =
        ["status", "app", "startedAtUtc", "checkedAtUtc", "runtime", "openApiUrl"];

    [Fact]
    public async Task Health_ReportsTheResolvedDatabasePathAndARealProjectCount()
    {
        using var board = new TestBoard();
        board.AddProjects("Alpha", "Beta");

        await using var host = await HealthHost.StartAsync(board, existed: true);
        var (statusCode, body) = await host.GetHealthAsync();

        Assert.Equal(HttpStatusCode.OK, statusCode);
        Assert.Equal("ok", body.GetProperty("status").GetString());

        var db = body.GetProperty("db");
        // The count is READ, not asserted from the setup constant alone: two rows in, two rows out.
        Assert.Equal(2, db.GetProperty("projects").GetInt32());
        Assert.True(db.GetProperty("exists").GetBoolean());
        Assert.Equal("ok", db.GetProperty("status").GetString());
        Assert.Empty(db.GetProperty("reasons").EnumerateArray());

        // The reported path must be the file the app actually opened, not a second computation of
        // the same rule — a resolver that disagrees with the open connection is the exact defect
        // class this endpoint exists to detect (#354).
        Assert.Equal(board.Path, db.GetProperty("path").GetString());
        Assert.Equal(board.ConnectionDataSource, db.GetProperty("path").GetString());
    }

    [Fact]
    public async Task Health_ReportsDegradedAndStill200_WhenTheBoardIsEmpty()
    {
        using var board = new TestBoard(); // schema created, zero projects

        await using var host = await HealthHost.StartAsync(board, existed: true);
        var (statusCode, body) = await host.GetHealthAsync();

        Assert.Equal(HttpStatusCode.OK, statusCode);
        Assert.Equal("degraded", body.GetProperty("status").GetString());

        var db = body.GetProperty("db");
        Assert.Equal(0, db.GetProperty("projects").GetInt32());
        Assert.Equal("ok", db.GetProperty("status").GetString()); // the READ worked; the board is empty
        Assert.Contains("empty-board", Reasons(db));
    }

    /// <summary>
    /// The case the issue exists for, and the one <c>projects &gt; 0</c> alone cannot see.
    ///
    /// <para>A launcher that resolves the database against a mis-resolved <c>$HOME</c> finds
    /// nothing, so the app creates the file it then serves — and <c>SeedData.Initialize</c> puts a
    /// sample project in it at boot, so the board is NOT empty. Counting rows would report a
    /// perfectly healthy 1. <see cref="DbPathResolution.Existed"/> is captured before any of that
    /// runs, so it is the one signal that survives seeding.</para>
    /// </summary>
    [Fact]
    public async Task Health_ReportsDegraded_WhenThisProcessCreatedTheBoardItIsServing()
    {
        using var board = new TestBoard();
        board.AddProjects("Sample Project"); // exactly what SeedData.Initialize leaves behind

        await using var host = await HealthHost.StartAsync(board, existed: false);
        var (statusCode, body) = await host.GetHealthAsync();

        Assert.Equal(HttpStatusCode.OK, statusCode);
        Assert.Equal("degraded", body.GetProperty("status").GetString());

        var db = body.GetProperty("db");
        Assert.False(db.GetProperty("existedAtStartup").GetBoolean());
        Assert.Contains("created-at-startup", Reasons(db));
        // A non-empty board must not be enough to earn "ok" — that is the whole point.
        Assert.Equal(1, db.GetProperty("projects").GetInt32());
        Assert.DoesNotContain("empty-board", Reasons(db));
    }

    [Fact]
    public async Task Health_ReportsDegradedAndNamesTheOtherFile_WhenASecondBoardShadowsThisOne()
    {
        using var board = new TestBoard();
        board.AddProjects("Alpha");
        var shadowed = Path.Combine(Path.GetTempPath(), $"kanbanflow-shadow-{Guid.NewGuid():N}.db");

        await using var host = await HealthHost.StartAsync(board, existed: true, shadowedPaths: [shadowed]);
        var (statusCode, body) = await host.GetHealthAsync();

        Assert.Equal(HttpStatusCode.OK, statusCode);
        Assert.Equal("degraded", body.GetProperty("status").GetString());

        var db = body.GetProperty("db");
        Assert.Contains("shadowed-by-another-database", Reasons(db));
        Assert.Equal(
            [shadowed],
            db.GetProperty("shadowedPaths").EnumerateArray().Select(p => p.GetString()!).ToArray());
    }

    [Fact]
    public async Task Health_ReportsErrorAndStill200_WhenTheDatabaseCannotBeOpened()
    {
        // A directory that does not exist: SQLite cannot create the file, so the read throws.
        // This is the "pointed at a path that is not there" case from the issue's goals.
        using var board = TestBoard.AtUnopenablePath();

        await using var host = await HealthHost.StartAsync(board, existed: false);
        var (statusCode, body) = await host.GetHealthAsync();

        // 200, not 500: the caller must be able to READ the reason. A health endpoint that throws
        // when the database is unreachable is useless in exactly the case it is needed.
        Assert.Equal(HttpStatusCode.OK, statusCode);
        Assert.Equal("error", body.GetProperty("status").GetString());

        var db = body.GetProperty("db");
        Assert.Equal("error", db.GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(db.GetProperty("error").GetString()));
        Assert.Equal(JsonValueKind.Null, db.GetProperty("projects").ValueKind);
        Assert.False(db.GetProperty("exists").GetBoolean());
        Assert.Contains("unreachable", Reasons(db));
        // It must name the file it tried, or the operator cannot tell "missing" from "wrong place".
        Assert.Equal(board.Path, db.GetProperty("path").GetString());
    }

    /// <summary>
    /// Under a competing writer the probe must answer FAST and must not throw. SQLite here runs in
    /// rollback-journal mode (nothing sets WAL), so a writer holding the file's EXCLUSIVE lock
    /// blocks readers outright and Microsoft.Data.Sqlite's default command timeout would retry for
    /// 30 s — six times longer than <c>scripts/ui-e2e.mjs</c> waits before declaring the app dead.
    ///
    /// <para>The contention is created with <c>PRAGMA locking_mode=EXCLUSIVE</c> followed by a real
    /// write, because the obvious approaches do not actually block anything: <c>BEGIN IMMEDIATE</c>
    /// takes only RESERVED, which readers sail straight past, and <c>BEGIN EXCLUSIVE</c> never
    /// touches the file until the transaction writes — measured, health answered <c>"ok"</c>
    /// straight through it, i.e. the test would have passed while testing nothing. Under
    /// <c>locking_mode=EXCLUSIVE</c> the first write takes the EXCLUSIVE lock and holds it until
    /// the connection closes.</para>
    /// </summary>
    [Fact]
    public async Task Health_StaysFastAndDoesNotThrow_WhileAnotherConnectionHoldsTheWriteLock()
    {
        using var board = new TestBoard();
        board.AddProjects("Alpha");

        await using var host = await HealthHost.StartAsync(board, existed: true);
        await host.GetHealthAsync(); // warm up EF model building + JIT so the timings below mean something

        await using (var writer = new SqliteConnection(board.ConnectionString))
        {
            await writer.OpenAsync();
            await using var takeLock = writer.CreateCommand();
            // user_version, not a row: a header write is enough to take the lock and leaves the
            // board's contents exactly as the assertions below expect.
            takeLock.CommandText = "PRAGMA locking_mode=EXCLUSIVE; PRAGMA user_version = 1;";
            await takeLock.ExecuteNonQueryAsync();

            var start = System.Diagnostics.Stopwatch.StartNew();
            var (lockedStatusCode, lockedBody) = await host.GetHealthAsync();
            start.Stop();

            Assert.Equal(HttpStatusCode.OK, lockedStatusCode);
            Assert.Equal("error", lockedBody.GetProperty("status").GetString());
            Assert.False(string.IsNullOrWhiteSpace(lockedBody.GetProperty("db").GetProperty("error").GetString()));
            // The bound is what matters: the default 30 s busy retry must NOT be paid.
            Assert.True(
                start.Elapsed < TimeSpan.FromSeconds(15),
                $"health blocked for {start.Elapsed.TotalSeconds:F1}s behind a write lock");
        }

        // Lock released: the endpoint recovers on its own, so the failure above was the contention
        // and not a permanently broken probe.
        var (recoveredStatusCode, recoveredBody) = await host.GetHealthAsync();
        Assert.Equal(HttpStatusCode.OK, recoveredStatusCode);
        Assert.Equal("ok", recoveredBody.GetProperty("status").GetString());
        // And the busy-wait bound is not charged when nothing is contending.
        Assert.True(
            recoveredBody.GetProperty("db").GetProperty("elapsedMs").GetDouble() < 1000,
            "the uncontended probe should not pay the busy-wait timeout");
    }

    /// <summary>
    /// Guard, not a reproduction: this one passes against the pre-#498 handler by construction. It
    /// is here because the Settings panel and <c>scripts/smoke-api.ps1</c> read these six names, and
    /// the issue requires the change to be purely additive.
    /// </summary>
    [Fact]
    public async Task Health_StillReturnsEveryFieldItReturnedBefore()
    {
        using var board = new TestBoard();
        board.AddProjects("Alpha");

        await using var host = await HealthHost.StartAsync(board, existed: true);
        var (_, body) = await host.GetHealthAsync();

        foreach (var field in LegacyFields)
            Assert.True(body.TryGetProperty(field, out _), $"health lost the '{field}' field");

        Assert.Equal("KanbanFlow", body.GetProperty("app").GetString());
        Assert.Equal("/openapi.json", body.GetProperty("openApiUrl").GetString());
        Assert.StartsWith(".NET ", body.GetProperty("runtime").GetString());
        Assert.Equal(host.StartedAtUtc, body.GetProperty("startedAtUtc").GetDateTime());
        Assert.True(body.GetProperty("checkedAtUtc").GetDateTime() >= host.StartedAtUtc);
    }

    private static string[] Reasons(JsonElement db) =>
        db.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()!).ToArray();

    /// <summary>A real SQLite file with the real EF schema, deleted on dispose.</summary>
    private sealed class TestBoard : IDisposable
    {
        private readonly bool _created;

        public TestBoard()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"kanbanflow-health-{Guid.NewGuid():N}.db");
            using var db = new AppDbContext(Options());
            db.Database.EnsureCreated();
            _created = true;
        }

        private TestBoard(string path) => Path = path;

        /// <summary>A path SQLite cannot open: the parent directory does not exist.</summary>
        public static TestBoard AtUnopenablePath() => new(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"kanbanflow-absent-{Guid.NewGuid():N}", "kanbanflow.db"));

        public string Path { get; }

        // Pooling=False so no recycled handle keeps the file alive past Dispose.
        public string ConnectionString => $"Data Source={Path};Pooling=False";

        /// <summary>What the open connection itself says it is pointed at.</summary>
        public string ConnectionDataSource => new SqliteConnectionStringBuilder(ConnectionString).DataSource;

        public DbContextOptions<AppDbContext> Options() =>
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(ConnectionString).Options;

        public void AddProjects(params string[] names)
        {
            using var db = new AppDbContext(Options());
            foreach (var name in names)
                db.Projects.Add(new Project { Name = name });
            db.SaveChanges();
        }

        public void Dispose()
        {
            if (!_created) return;
            SqliteConnection.ClearAllPools();
            try { File.Delete(Path); }
            catch (IOException) { /* a host still holding the handle is not a test failure */ }
        }
    }

    /// <summary>A real Kestrel host on an ephemeral loopback port with the health route mapped.</summary>
    private sealed class HealthHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly HttpClient _client;

        private HealthHost(WebApplication app, HttpClient client, DateTime startedAtUtc)
        {
            _app = app;
            _client = client;
            StartedAtUtc = startedAtUtc;
        }

        public DateTime StartedAtUtc { get; }

        public static async Task<HealthHost> StartAsync(
            TestBoard board, bool existed, IReadOnlyList<string>? shadowedPaths = null)
        {
            // Development so a server-side exception comes back in the response body: a host that
            // answers a bare 500 makes every failure look identical.
            var builder = WebApplication.CreateBuilder(
                new WebApplicationOptions { EnvironmentName = "Development" });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0"); // ephemeral port: tests can run in parallel

            var startup = new AppStartupInfo();
            builder.Services.AddSingleton<IAppStartupInfo>(startup);
            builder.Services.AddSingleton(
                new DbPathResolution(board.Path, DbPathResolver.SourceEnvironment, existed, shadowedPaths ?? []));
            builder.Services.AddDbContextFactory<AppDbContext>(o => o.UseSqlite(board.ConnectionString));

            // MapAdminEndpoints maps five routes and ASP.NET Core builds the WHOLE endpoint table
            // on the first request. A handler parameter that is not a registered service is
            // inferred as a request body, which /api/admin/llm-ping and /api/admin/embed-ping
            // reject outright because they are GETs -- and the resulting 500 lands on the health
            // request even though health's own wiring is fine (measured while writing this).
            // Register those routes' dependencies as unusable stubs so the table builds; straying
            // onto one of them then says so instead of producing a mystery 500.
            foreach (var offRoute in new[]
                     {
                         typeof(ITestRunnerService), typeof(ILlmClient), typeof(LlmOptions),
                         typeof(IEmbeddingService), typeof(EmbeddingOptions), typeof(IMemoryService)
                     })
            {
                builder.Services.AddSingleton(offRoute, (Func<IServiceProvider, object>)(_ =>
                    throw new NotSupportedException("this host maps /api/admin/health only")));
            }

            var app = builder.Build();
            app.MapAdminEndpoints();
            await app.StartAsync();

            var address = app.Urls.First();
            return new HealthHost(app, new HttpClient { BaseAddress = new Uri(address) }, startup.StartedAtUtc);
        }

        public async Task<(HttpStatusCode StatusCode, JsonElement Body)> GetHealthAsync()
        {
            using var response = await _client.GetAsync("/api/admin/health");
            var json = await response.Content.ReadAsStringAsync();

            // Report the wire response verbatim when it is not JSON. A bare parse failure here
            // hides the two answers that matter most — a 404 (route never mapped) and a 500 (the
            // handler threw) — behind an identical "no JSON tokens" message.
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(json);
            }
            catch (JsonException ex)
            {
                throw new Xunit.Sdk.XunitException(
                    $"/api/admin/health answered HTTP {(int)response.StatusCode} with a non-JSON body " +
                    $"({ex.Message}). Body: <{json}>");
            }

            using (document)
                return (response.StatusCode, document.RootElement.Clone());
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
