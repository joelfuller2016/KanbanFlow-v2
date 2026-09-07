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
        board.MarkEstablished(); // "ok" means an ordinary board, not one made moments ago

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
        // Top-level status and db.status are separate fields with separate vocabularies.
        // Top-level: "ok" | "degraded" | "error". db.status: "ok" | "unreachable".
        Assert.Equal("error", body.GetProperty("status").GetString());

        var db = body.GetProperty("db");
        Assert.Equal("unreachable", db.GetProperty("status").GetString());
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
        board.MarkEstablished(); // this case ends by asserting "ok" once the lock is released

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
            // The bound guards the CONFIGURED 2 s command timeout, not merely "less than the 30 s
            // driver default". At 15 s this assertion stayed green against a probe raised to 10 s,
            // so it guarded the default it was not testing rather than the value this change
            // introduced — the non-failable-check shape #498 is about, in the test that names it.
            //
            // 6 s is a HARD LITERAL on purpose, and deliberately NOT "derived from the configured
            // timeout": a bound computed from the endpoint's own constant moves in lockstep with
            // the regression it exists to catch, so raising the timeout to 10 s would raise the
            // bound to 10 s + overhead and stay green. The number encodes the REQUIREMENT — health
            // answers in a few seconds — instead of mirroring the implementation.
            //
            // Why 6: measured 2.0 s on three consecutive runs (the 2 s timeout plus host overhead
            // too small to move the first decimal this message prints). 3x the measurement leaves
            // 4 s of headroom for a loaded machine's scheduling jitter, and sits 5x under the 30 s
            // default so a dropped SetCommandTimeout is caught decisively.
            //
            // STATED LIMIT: a regression to 3-6 s still passes here. That is the accepted residue
            // of any wall-clock bound — tightening further buys CI flakes, not detection.
            Assert.True(
                start.Elapsed < TimeSpan.FromSeconds(6),
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

    // ── The file's own facts: the wrong board a SUCCESSFUL seed restore leaves behind. ──────────
    //
    // #498's reproduction runs through scripts/start-kanbanflow.ps1, which Copy-Items the committed
    // seed into place BEFORE the app starts. So the board exists at resolution time, Existed is
    // true, and the seed holds a project: every check above answers "ok" and the endpoint declared
    // the app healthy in exactly the scenario the issue was filed about. These cases cover the two
    // signals that do fire there. Both read the real file through DbPathResolver, so a resolver that
    // stopped capturing the facts turns them red too.

    [Fact]
    public async Task Health_ReportsDegraded_WhenTheBoardWasLastWrittenBeforeItWasCreated()
    {
        using var board = new TestBoard();
        board.AddProjects("Sample Project"); // the seed is not empty — row counting sees nothing wrong

        // The literal Copy-Item signature: the destination file is made NOW and keeps the source's
        // older last-write time. No board that has been used since it was created can look like this.
        board.SetLastWriteUtc(DateTime.UtcNow.AddHours(-3));

        await using var host = await HealthHost.StartAsync(board, existed: true);
        var (statusCode, body) = await host.GetHealthAsync();

        Assert.Equal(HttpStatusCode.OK, statusCode);
        Assert.Equal("degraded", body.GetProperty("status").GetString());

        var db = body.GetProperty("db");
        Assert.Contains("last-write-precedes-creation", Reasons(db));
        // Every other check is satisfied — which is the point. This is the state the PR previously
        // called undetectable in-app.
        Assert.True(db.GetProperty("existedAtStartup").GetBoolean());
        Assert.Equal(1, db.GetProperty("projects").GetInt32());
        Assert.DoesNotContain("empty-board", Reasons(db));
        Assert.DoesNotContain("created-at-startup", Reasons(db));
        // A reported signal, never a failure: the read worked and the app is serving.
        Assert.Equal("ok", db.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Health_ReportsDegraded_WhenTheBoardWasCreatedMomentsBeforeThisProcessStarted()
    {
        // The same wrong-root start, for a copy whose last-write was NOT preserved: all that is
        // left is that the board came into existence just before the process serving it did.
        using var board = new TestBoard(); // created now, so within the window of this host's start
        board.AddProjects("Sample Project");

        await using var host = await HealthHost.StartAsync(board, existed: true);
        var (statusCode, body) = await host.GetHealthAsync();

        Assert.Equal(HttpStatusCode.OK, statusCode);
        Assert.Equal("degraded", body.GetProperty("status").GetString());

        var db = body.GetProperty("db");
        Assert.Contains("created-moments-before-startup", Reasons(db));
        // Isolated from the sibling check above: EnsureCreated wrote after creating, so this case
        // is carried by the creation time alone.
        Assert.DoesNotContain("last-write-precedes-creation", Reasons(db));
        Assert.True(db.GetProperty("existedAtStartup").GetBoolean());
    }

    /// <summary>
    /// The negative control, and the more important half of the pair: an ordinary board must not be
    /// slandered. A detector that reports every board as suspect is a detector nobody reads.
    /// </summary>
    [Fact]
    public async Task Health_StaysOk_ForABoardCreatedLongAgoAndWrittenSince()
    {
        using var board = new TestBoard();
        board.AddProjects("Alpha");
        board.MarkEstablished();

        await using var host = await HealthHost.StartAsync(board, existed: true);
        var (statusCode, body) = await host.GetHealthAsync();

        Assert.Equal(HttpStatusCode.OK, statusCode);
        Assert.Equal("ok", body.GetProperty("status").GetString());
        Assert.Empty(Reasons(body.GetProperty("db")));
    }

    /// <summary>
    /// The three facts are reported as well as judged, exactly as <c>db.path</c> is (#498 review).
    ///
    /// <para>The most useful comparison — is this board the SIZE of the committed seed? — is the
    /// CALLER's, not the application's: <c>seed/kanbanflow.seed.db</c> is not shipped in the publish
    /// output (verified), so the app has nothing to compare against. Reporting the size lets whoever
    /// does have the seed make the call — a match is consistent with the seed rather than proof of
    /// it, since SQLite reuses freed pages and a board written to can still report the same size.</para>
    /// </summary>
    [Fact]
    public async Task Health_ReportsTheFileFacts_SoTheCallerCanCompareThemAgainstTheSeedItShipped()
    {
        using var board = new TestBoard();
        board.AddProjects("Alpha");
        var onDisk = new FileInfo(board.Path);

        await using var host = await HealthHost.StartAsync(board, existed: true);
        var (_, body) = await host.GetHealthAsync();

        var db = body.GetProperty("db");
        Assert.Equal(onDisk.Length, db.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(onDisk.CreationTimeUtc, db.GetProperty("createdAtUtc").GetDateTime());
        Assert.Equal(onDisk.LastWriteTimeUtc, db.GetProperty("lastWriteUtc").GetDateTime());
    }

    /// <summary>
    /// The probe must never CREATE the database it is asked about (#498 review, 8).
    ///
    /// <para><c>Microsoft.Data.Sqlite</c> opens <c>Mode=ReadWriteCreate</c> by default, so a probe
    /// against a path that is not there makes an empty file, fails on the missing table, and then
    /// reports <c>exists: true</c> — health converting the one condition it exists to detect into a
    /// condition that no longer holds, and handing the next caller a board that was never real.</para>
    /// </summary>
    [Fact]
    public async Task Health_DoesNotCreateTheDatabaseItProbes_WhenTheFileIsMissing()
    {
        // Parent directory present, file absent: the case ReadWriteCreate silently repairs. (The
        // sibling case above uses a missing DIRECTORY, which SQLite refuses either way — so it
        // could never have caught this.)
        using var board = TestBoard.AtMissingFileInAnExistingDirectory();
        Assert.False(File.Exists(board.Path));

        await using var host = await HealthHost.StartAsync(board, existed: false);
        var (statusCode, body) = await host.GetHealthAsync();

        Assert.Equal(HttpStatusCode.OK, statusCode);
        Assert.Equal("error", body.GetProperty("status").GetString());
        Assert.Contains("unreachable", Reasons(body.GetProperty("db")));

        // The assertion that matters: the endpoint left the filesystem alone.
        Assert.False(
            File.Exists(board.Path),
            $"health created '{board.Path}' while probing it — the probe must open read-only");
        Assert.False(body.GetProperty("db").GetProperty("exists").GetBoolean());
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
        private readonly string? _temporaryDirectory;

        public TestBoard()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"kanbanflow-health-{Guid.NewGuid():N}.db");
            using var db = new AppDbContext(Options());
            db.Database.EnsureCreated();
            _created = true;
        }

        private TestBoard(string path, string? temporaryDirectory = null)
        {
            Path = path;
            _temporaryDirectory = temporaryDirectory;
        }

        /// <summary>A path SQLite cannot open: the parent directory does not exist.</summary>
        public static TestBoard AtUnopenablePath() => new(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"kanbanflow-absent-{Guid.NewGuid():N}", "kanbanflow.db"));

        /// <summary>
        /// A directory that EXISTS holding no database — the shape <c>Mode=ReadWriteCreate</c>
        /// quietly repairs by inventing a file, unlike <see cref="AtUnopenablePath"/>.
        /// </summary>
        public static TestBoard AtMissingFileInAnExistingDirectory()
        {
            var directory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"kanbanflow-emptydir-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            return new TestBoard(System.IO.Path.Combine(directory, "kanbanflow.db"), directory);
        }

        public string Path { get; }

        /// <summary>
        /// The file's real facts, stat'd exactly as <c>DbPathResolver</c> stats them at boot — null
        /// when there is no file. The resolver additionally returns null for a file it cannot stat
        /// (#513); this fixture does not reproduce that case and does not need to.
        /// </summary>
        /// <remarks>
        /// Read here rather than by calling <c>DbPathResolver.Resolve</c>, which creates the parent
        /// directory of whatever it is handed and would therefore repair
        /// <see cref="AtUnopenablePath"/> out from under the case that depends on it. That the
        /// RESOLVER captures these facts is bound separately, by
        /// <c>DbPathResolverTests.Resolution_CapturesTheFilesOwnFacts_WhenTheBoardAlreadyExists</c>.
        /// </remarks>
        public DbFileSnapshot? Snapshot
        {
            get
            {
                var info = new FileInfo(Path);
                return info.Exists
                    ? new DbFileSnapshot(info.CreationTimeUtc, info.LastWriteTimeUtc, info.Length)
                    : null;
            }
        }

        /// <summary>Backdate the last write — with creation left alone, the copy signature.</summary>
        public void SetLastWriteUtc(DateTime value) => File.SetLastWriteTimeUtc(Path, value);

        /// <summary>Backdate creation, so the board reads as one that has been around a while.</summary>
        public void SetCreationUtc(DateTime value) => File.SetCreationTimeUtc(Path, value);

        /// <summary>
        /// An ORDINARY board: made a month ago, written yesterday. Cases that assert <c>ok</c> need
        /// this, because a <see cref="TestBoard"/> is otherwise created milliseconds before the host
        /// that serves it — which is genuinely one of the suspicious shapes (#498 review, 3), and
        /// those cases mean "an established board", not "a board that happens to be brand new".
        /// Backdating the fixture is the honest fix; relaxing the assertion would not be.
        /// </summary>
        public void MarkEstablished()
        {
            SetCreationUtc(DateTime.UtcNow.AddDays(-30));
            SetLastWriteUtc(DateTime.UtcNow.AddDays(-1));
        }

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
            // No ClearAllPools(): every connection here is Pooling=False, and that call is
            // process-global -- it would yank idle handles out from under test classes running in
            // parallel (same reason IdeaServiceTests avoids it).
            if (_created)
            {
                try { File.Delete(Path); }
                catch (IOException) { /* a host still holding the handle is not a test failure */ }
                catch (UnauthorizedAccessException) { }
            }

            if (_temporaryDirectory is not null)
            {
                // Recursive: a probe that WRONGLY created the database leaves a file behind, and a
                // cleanup that threw would report as a failure of whichever case ran next.
                try { Directory.Delete(_temporaryDirectory, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
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
            // The file facts come from the board's REAL file, so the reasons derived from them are
            // derived from a real stat rather than from a record hand-built to make them fire.
            builder.Services.AddSingleton(
                new DbPathResolution(
                    board.Path, DbPathResolver.SourceEnvironment, existed, shadowedPaths ?? [], board.Snapshot));
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
            try
            {
                app.MapAdminEndpoints();
                await app.StartAsync();

                var address = app.Urls.First();
                return new HealthHost(
                    app, new HttpClient { BaseAddress = new Uri(address) }, startup.StartedAtUtc);
            }
            catch
            {
                // Everything above runs BEFORE HealthHost exists, and HealthHost owns the only
                // DisposeAsync that would clean this up — so a throw from MapAdminEndpoints,
                // StartAsync or Urls.First() leaves the host with no owner at all.
                // WebApplication.StartAsync does not dispose the host it failed to start, so the DI
                // container and any partially bound Kestrel listener would survive to the end of the
                // run. These tests bind ephemeral ports and run in parallel, so that is one leak per
                // failure rather than a one-off, and it lands on whatever test runs next.
                //
                // Measured, with a disposable sentinel in the container and a forced throw after
                // StartAsync: with this clause the container is disposed; with the DisposeAsync
                // removed it is not. (Scaffolding, not kept — a permanent test here would have to
                // make the host fail to start on purpose.)
                await app.DisposeAsync();
                throw;
            }
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
