using System.Data;
using System.Diagnostics;
using KanbanFlow.Data;
using KanbanFlow.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow;

public static class AdminEndpoints
{
    /// <summary>
    /// How recently the database file must have been created, relative to this process's start, to
    /// count as "this deployment brought the board with it" (#498).
    /// </summary>
    /// <remarks>
    /// Sized to the launcher, not to a stopwatch. <c>scripts/start-kanbanflow.ps1</c> restores the
    /// seed and only then runs <c>dotnet publish</c> before starting the app; a warm publish is a
    /// few seconds, a cold one is minutes. Too small and the check misses the exact scenario it was
    /// written for. Too large and every legitimately new install reports <c>degraded</c> for its
    /// first quarter of an hour — which is a false positive, and the reason this is a reported
    /// reason rather than a failure.
    /// </remarks>
    private static readonly TimeSpan BoardCreatedJustBeforeStartup = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Tolerance for a creation stamp that reads very slightly AFTER the recorded start time.
    /// </summary>
    /// <remarks>
    /// The two values come from different clocks — one from the filesystem, one from
    /// <see cref="IAppStartupInfo"/> — and the filesystem's is the coarser: per MS-FSA, creation is
    /// recorded at 10 ms granularity on FAT and exFAT against NTFS's 100 ns. Without slack, a board
    /// created in the same instant as the process could land microseconds on the wrong side and the
    /// check would silently not fire. Note the DIRECTION: this slack WIDENS the window, so it can
    /// only add reports, never suppress one — which is why the sibling check below deliberately
    /// carries none. This is not a licence for the future: a file created well after startup is a
    /// different event and must not be reported as this one.
    /// </remarks>
    private static readonly TimeSpan ClockSlack = TimeSpan.FromSeconds(5);

    /// <summary>
    /// /api/admin/* — runs in-process diagnostics from inside the live app.
    /// Same surface that the Settings panel uses, also reachable from curl / MCP.
    /// </summary>
    public static WebApplication MapAdminEndpoints(this WebApplication app)
    {
        // GET /api/admin/health — the app is up, DI is wired, AND this process is actually
        // serving a database (#498). openApiUrl advertises the OpenAPI document path so agents
        // can discover it.
        //
        // The probe exists because a wrong-path start is otherwise indistinguishable from a
        // correct one (#354). `status` used to be a hardcoded literal with no database injected,
        // so there was no state of the system in which this endpoint answered anything but "ok" —
        // it carried zero bits, and #477 §9 built a reboot criterion on top of that constant.
        //
        // What this endpoint CAN decide on its own, and does: the file opened, a real read
        // succeeded, the board is not empty, this process did not create the board it is serving,
        // no second kanbanflow.db is shadowing the one in use, and — from the file's own facts,
        // captured at boot — that the board does not carry the signature of a fresh copy. What it
        // CANNOT know is which database the operator meant: the launcher pins KANBANFLOW_DB_PATH
        // from its own ($HOME-derived) computation, so the app faithfully opens whatever it was
        // handed. That half is completed by the caller comparing db.path — and now db.sizeBytes,
        // db.createdAtUtc and db.lastWriteUtc — against what it expects (#477 §9 criterion 3).
        app.MapGet("/api/admin/health", async (
            IAppStartupInfo startup,
            DbPathResolution database,
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            // Read BEFORE the probe, deliberately. Computed after, this answers a question about a
            // file the probe itself may have just made: SQLite's default open mode is
            // ReadWriteCreate, so a probe against a missing file creates an empty one and `exists`
            // then reports true about the endpoint's own side effect. The probe below is opened
            // read-only so it cannot happen at all; this ordering means the answer stays honest
            // even if that ever regresses.
            var exists = File.Exists(database.Path);

            var stopwatch = Stopwatch.StartNew();
            int? projects = null;
            string? probeError = null;

            try
            {
                await using var db = await dbFactory.CreateDbContextAsync(ct);

                // Health must never CREATE the thing it is inspecting (#498 review, 8). The
                // application's own connection string carries Microsoft.Data.Sqlite's default
                // Mode=ReadWriteCreate, which invents an empty zero-table database when the file is
                // absent — so the one case this endpoint exists for, a path that is not there,
                // would be silently converted into a path that is, and the next caller would find a
                // board. Read-only makes "the file is missing" stay missing and report itself.
                // (Windows happens to mask this behind a sharing violation while the app holds the
                // file; Linux and macOS do not. The mode is the fix, not the platform.)
                //
                // This does open a second pool, keyed on the read-only connection string. That is
                // one extra SQLite handle for a polled endpoint and is deliberate — the branch
                // already removed a process-global pool flush, so nothing here reaches across to
                // the application's own connections.
                db.Database.SetConnectionString(
                    new SqliteConnectionStringBuilder(db.Database.GetConnectionString())
                    {
                        Mode = SqliteOpenMode.ReadOnly
                    }.ConnectionString);

                // Bound the busy-wait. Measured: nothing in this app sets WAL and the committed
                // seed reports journal_mode=delete, so a reader is blocked while a writer commits
                // and Microsoft.Data.Sqlite would retry for its 30s default. A health check that
                // stalls for 30s under load is its own bug — scripts/ui-e2e.mjs abandons the probe
                // after 5s and reports the app as never having come up. Two seconds turns write
                // contention into a fast, honest answer the caller can retry instead of a hang,
                // and is never paid on the uncontended path (measured in single-digit ms).
                db.Database.SetCommandTimeout(TimeSpan.FromSeconds(2));

                // The cheapest read that proves the file opened AND carries this app's schema.
                // Projects is single-digit rows; health is polled, so this must never grow into a
                // query that scans the board.
                projects = await db.Projects.CountAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // the caller hung up — that is not a verdict about the database
            }
            catch (Exception ex)
            {
                // Never throw. The unreachable database is precisely the case the caller needs an
                // answer for, so it is reported at HTTP 200 with the reason rather than as a 500
                // the caller can only guess at. Message only in the PAYLOAD — never the stack.
                probeError = ex.Message;

                // ...and the stack goes to the log, because HTTP 200 is right for the caller and
                // tells the OPERATOR nothing. Same shape as the fail-soft path in the memory
                // reindex below, which already logs what it swallowed.
                //
                // BE ACCURATE ABOUT WHAT THIS ADDS, because it is not "a trace where there was
                // none". Measured: EF Core already logs two Error lines here — "An exception
                // occurred while iterating over the results of a query for context type
                // 'AppDbContext'" and "An error occurred using the connection to database 'main' on
                // server '<path>'" — and appsettings.json leaves them on (Default: Information).
                // What they do NOT say is which QUESTION was being asked: a failed health probe is
                // indistinguishable in the log from any other query against the same context. This
                // line names the probe and the resolved file, which is the whole #498 question.
                //
                // Warning, not Error, and deliberately NOT rate-limited: the process is up and
                // serving, so this is not the app failing; and health is polled, so an unreachable
                // board logs once per poll — the correct volume for a database this process cannot
                // read. Staying quiet would need state, to suppress the exact condition #498 exists
                // to stop being quiet about.
                app.Logger.LogWarning(ex, "Health database probe failed for {DbPath}.", database.Path);
            }

            stopwatch.Stop();

            // Every reason that applies, not just the first: a caller told only half the story
            // fixes half the problem. Empty and created-at-startup routinely co-occur.
            var reasons = new List<string>();
            if (probeError is not null)
                reasons.Add("unreachable");
            else if (projects == 0)
                reasons.Add("empty-board");

            // The one "am I serving a board that existed before me" signal that survives seeding:
            // SeedData.Initialize inserts a sample project at boot whenever Projects is empty, and
            // the launcher restores a committed seed that already holds one, so projects > 0 alone
            // cannot tell a real board from a board this start invented. Existed is captured by
            // DbPathResolver BEFORE any of that runs.
            if (!database.Existed)
                reasons.Add("created-at-startup");

            // ── The file's own facts (#498 review, 2). ────────────────────────────────────────────
            //
            // Existed alone is NOT enough, and the measurement that proves it is the issue's own
            // reproduction: run through scripts/start-kanbanflow.ps1 against a wrong root and the
            // launcher Copy-Items the committed seed into place BEFORE the app starts. The file is
            // therefore already on disk when the path is resolved, Existed is true, the seed holds a
            // project — and this endpoint answered a confident, wrong "ok". These two checks are
            // what make the documented reproduction flip to degraded.
            //
            // WHAT THEY ARE WORTH, honestly, because a detector oversold is worse than none:
            //   * They hold only until real data is written to the wrong board. After the first
            //     write, LastWriteUtc moves past CreatedAtUtc and NOTHING inside this application
            //     can tell the board apart from the right one. That includes an app upgrade whose
            //     schema patchers write at boot, which costs the signal with no user data at all.
            //   * NTFS file tunnelling can preserve the ORIGINAL creation time when a file of the
            //     same name is deleted and recreated within roughly 15 seconds, which hides the copy.
            //   * A user restoring their own board from a backup produces exactly this shape and is
            //     reported the same way. That is a false positive, and it is why these are `degraded`
            //     reasons carrying their evidence — never `error`, and never a refusal to start.
            if (database.Snapshot is { } snapshot)
            {
                // A board written before it was created is not something a used board can be; it is
                // the literal signature of a copy that preserved the source's timestamp, which is
                // what Copy-Item, robocopy, an unzip and most sync clients all do.
                //
                // Deliberately NO ClockSlack, and the asymmetry with the sibling below is the point
                // rather than an oversight. That check compares a filesystem stamp against a process
                // clock, so its slack WIDENS the window and can only add reports. These two stamps
                // come from ONE FileInfo — one clock, one stat — so there is no skew to absorb, and
                // the same constant here would NARROW the window instead: it would suppress the
                // durable signal, in a design where a false positive costs an operator one glance at
                // db.path and a false negative is #498 itself. That direction argument is the whole
                // of the reason; the margin below neither strengthens nor weakens it.
                //
                // What the choice leaves unhandled, conceded rather than papered over: per MS-FSA,
                // FAT records creation at 10 ms but last write at 2 s, so on a FAT volume an
                // ordinary board could in principle read as written before it was created. The
                // natural margin really is that thin — measured on NTFS against this repo's own
                // seed/kanbanflow.seed.db, a legitimate board's create-to-last-write gap is 3.7 ms,
                // which a 2 s quantisation would cross easily. Whether the rounding actually goes
                // that way is not in the specification and is untested here; NTFS, ReFS and exFAT
                // record both stamps at equal granularity and cannot produce it. So the residue is
                // a false POSITIVE, on a filesystem this app is not deployed to, and the cure for
                // it would buy false negatives everywhere else.
                if (snapshot.LastWriteUtc < snapshot.CreatedAtUtc)
                    reasons.Add("last-write-precedes-creation");

                // And the short-window catch for a copy whose last-write was NOT preserved: a board
                // that came into existence just before this process did. Only meaningful when the
                // file already existed — otherwise created-at-startup above already says it, louder.
                //
                // The window is minutes rather than seconds on purpose: the launcher runs a
                // dotnet publish between restoring the seed and starting the app, and a cold publish
                // is far slower than the ~5 s a warm one takes.
                //
                // Note this is measured against the RECORDED START TIME, not the clock, so it does
                // not tick away underneath a running process: a start inside the window keeps
                // reporting for that process's whole life, and the signal is lost at the first
                // restart more than the window after the file was created. That is the right frame
                // for a criterion evaluated across reboots (#477 §9) — but it does expire, and the
                // durable signal is last-write-precedes-creation above.
                if (database.Existed &&
                    startup.StartedAtUtc - snapshot.CreatedAtUtc is { } age &&
                    age < BoardCreatedJustBeforeStartup &&
                    age > -ClockSlack)
                {
                    reasons.Add("created-moments-before-startup");
                }
            }

            // Two real databases exist and only one is open — the #354 presentation itself.
            if (database.ShadowedPaths.Count > 0)
                reasons.Add("shadowed-by-another-database");

            var status = probeError is not null
                ? "error"
                : reasons.Count > 0 ? "degraded" : "ok";

            return Results.Ok(new
            {
                status,
                app = "KanbanFlow",
                startedAtUtc = startup.StartedAtUtc,
                checkedAtUtc = DateTime.UtcNow,
                runtime = $".NET {Environment.Version}",
                openApiUrl = "/openapi.json",
                db = new
                {
                    path = database.Path,
                    exists,
                    source = database.Source,
                    existedAtStartup = database.Existed,
                    // The file's own facts as the app FOUND them, reported for the same reason
                    // db.path is: the comparison that finishes the diagnosis belongs to the caller,
                    // which knows things this process does not. The most useful of those is the
                    // size of the committed seed — a restored copy is byte-identical to it, and the
                    // application does not ship seed/kanbanflow.seed.db, so it cannot make that
                    // comparison itself. Null together when there was no file to stat.
                    createdAtUtc = database.Snapshot?.CreatedAtUtc,
                    lastWriteUtc = database.Snapshot?.LastWriteUtc,
                    sizeBytes = database.Snapshot?.SizeBytes,
                    shadowedPaths = database.ShadowedPaths,
                    projects,
                    status = probeError is null ? "ok" : "error",
                    error = probeError,
                    reasons,
                    elapsedMs = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1)
                }
            });
        });

        // POST /api/admin/run-tests?suite=api|ui|service|all (default: all)
        app.MapPost("/api/admin/run-tests", async (string? suite, ITestRunnerService runner, CancellationToken ct) =>
        {
            var which = (suite ?? "all").ToLowerInvariant();
            var result = which switch
            {
                "api" => await runner.RunApiTestsAsync(ct),
                "ui" => await runner.RunUiTestsAsync(ct),
                "service" => await runner.RunServiceTestsAsync(ct),
                "all" => await runner.RunAllTestsAsync(ct),
                _ => null
            };

            if (result is null)
                return Results.UnprocessableEntity(new { message = $"Unknown suite '{suite}'. Valid: api, ui, service, all." });

            // Return either 200 OK or 422 depending on whether the suite passed; this lets CI fail
            // fast on `curl --fail` without parsing the body.
            return result.AllPassed
                ? Results.Ok(result)
                : Results.UnprocessableEntity(result);
        });

        // GET /api/admin/llm-ping — user-runnable self-test of the LLM wiring (#218). Sends a
        // trivial prompt through ILlmClient and reports the outcome. ALWAYS returns 200: when
        // ZeroRouter is down/misconfigured it reports { ok: false, error } (fail-soft) instead of
        // a 500, so the probe itself proves the no-crash contract. url + model are surfaced for
        // diagnostics (open-access: nothing is secret, but the key is simply not echoed here).
        app.MapGet("/api/admin/llm-ping", async (ILlmClient llm, LlmOptions options, CancellationToken ct) =>
        {
            var stopwatch = Stopwatch.StartNew();
            var result = await llm.CompleteAsync(
                "You are a health check. Reply with the single word: pong",
                "ping",
                ct);
            stopwatch.Stop();

            return Results.Ok(new
            {
                ok = result.Ok,
                model = result.Model ?? options.Model,
                url = options.BaseUrl,
                latencyMs = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1),
                reply = result.Text,
                error = result.Error
            });
        });

        // GET /api/admin/embed-ping — user-runnable self-test of the embeddings wiring (#219). Embeds
        // a trivial (per-call unique) string through IEmbeddingService and reports the outcome +
        // resulting vector length. A unique string is used deliberately so the probe always exercises
        // the live provider — a cached constant would report a stale ok:true even after ZeroRouter
        // went down. ALWAYS returns 200: when ZeroRouter is down/misconfigured it reports
        // { ok: false, error } (fail-soft), so the probe itself proves the no-crash contract. url +
        // model are surfaced for diagnostics (open-access: nothing is secret; the key is not echoed here).
        app.MapGet("/api/admin/embed-ping", async (IEmbeddingService embeddings, EmbeddingOptions options, CancellationToken ct) =>
        {
            var stopwatch = Stopwatch.StartNew();
            var result = await embeddings.EmbedAsync($"embed-ping {Guid.NewGuid():N}", ct);
            stopwatch.Stop();

            return Results.Ok(new
            {
                ok = result.Ok,
                model = result.Model,
                url = options.BaseUrl,
                dimensions = result.Dimensions,
                latencyMs = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1),
                error = result.Error
            });
        });

        // POST /api/admin/memory/reindex[?force=true] — (re)embed current memory nodes (#222). By
        // default it embeds only nodes still missing a vector (EmbeddingBlob == null); force=true
        // re-embeds every current node (e.g. after an embed-model change). Each node's content is
        // embedded via IEmbeddingService and persisted through MemoryService.SetEmbeddingAsync (one
        // MemoryEmbedded audit event per node, #88). Fail-soft like embed-ping: when ZeroRouter is
        // down each embed returns ok:false and is counted as failed — the batch never crashes. A node
        // superseded/deleted mid-run is counted as skipped. No auth by charter (open-access). Returns
        // a per-run tally { embedded, skipped, failed, ftsReindexed, model }. After re-embedding it
        // also rebuilds the FTS5 keyword index (#223) from the current nodes — the reconciler that
        // self-heals any missed dual-write — fail-soft so an FTS hiccup never fails the reindex.
        app.MapPost("/api/admin/memory/reindex", async (
            bool? force, IEmbeddingService embeddings, IMemoryService memory, EmbeddingOptions options,
            IDbContextFactory<AppDbContext> dbFactory, CancellationToken ct) =>
        {
            var reembedAll = force ?? false;

            // Snapshot the current nodes up front (ListAsync already excludes superseded versions);
            // pick those still needing a vector unless a full re-embed was requested. The corpus is
            // small (single-user local tool), so a snapshot + per-node write is fine.
            var current = await memory.ListAsync(includeSuperseded: false, ct: ct);
            var pending = reembedAll
                ? current
                : current.Where(node => node.EmbeddingBlob is null).ToList();

            int embedded = 0, skipped = 0, failed = 0;
            foreach (var node in pending)
            {
                var result = await embeddings.EmbedAsync(node.Content, ct);
                if (!result.Ok || result.Vector is null || result.Vector.Length == 0)
                {
                    // ZeroRouter down/erroring, or a provider that reports Ok with a null/empty
                    // vector: fail-soft (never thrown) — count and keep going (Gemini review, PR #256).
                    failed++;
                    continue;
                }

                try
                {
                    await memory.SetEmbeddingAsync(node.Id, result.Vector, "reindex", ct);
                    embedded++;
                }
                catch (MemoryNodeNotFoundException)
                {
                    skipped++; // hard-deleted between the snapshot and the write
                }
                catch (ValidationException)
                {
                    skipped++; // superseded/retired between the snapshot and the write
                }
            }

            // FTS reconciler (#223): rebuild fts_memory from the current nodes (DELETE all, re-INSERT
            // each), so a dual-write that was ever missed self-heals. Best-effort: a rebuild failure is
            // swallowed and reported as 0 reindexed rather than failing the whole reindex.
            var ftsReindexed = 0;
            try
            {
                var nodesForFts = await memory.ListAsync(includeSuperseded: false, ct: ct);
                await using var db = await dbFactory.CreateDbContextAsync(ct);

                // Load all observations in one batch so the rebuild matches the dual-write's
                // combined searchable text (Content + observations), not just Content alone.
                var allNodeIds = nodesForFts.Select(n => n.Id).ToList();
                var observationsByNode = (await db.Observations
                    .Where(o => allNodeIds.Contains(o.MemoryNodeId))
                    .OrderBy(o => o.Id)
                    .Select(o => new { o.MemoryNodeId, o.Content })
                    .ToListAsync(ct))
                    .GroupBy(o => o.MemoryNodeId)
                    .ToDictionary(g => g.Key, g => g.Select(o => o.Content).ToList());

                var connection = db.Database.GetDbConnection();
                var openedByUs = connection.State != ConnectionState.Open;
                if (openedByUs)
                    await connection.OpenAsync(ct);
                try
                {
                    await using var tx = await connection.BeginTransactionAsync(ct);
                    await MemoryFts.DeleteAllAsync(connection, tx, ct);
                    foreach (var node in nodesForFts)
                    {
                        var searchable = observationsByNode.TryGetValue(node.Id, out var obs) && obs.Count > 0
                            ? node.Content + " " + string.Join(" ", obs)
                            : node.Content;
                        await MemoryFts.UpsertAsync(connection, tx, node.Id, searchable, ct);
                        ftsReindexed++;
                    }

                    await tx.CommitAsync(ct);
                }
                finally
                {
                    // Best-effort close so a cleanup failure can't mask the FTS-rebuild error
                    // (Gemini review, PR #257). CloseAsync() is parameterless — no CancellationToken.
                    if (openedByUs)
                    {
                        try { await connection.CloseAsync(); }
                        catch { /* connection cleanup is best-effort */ }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Best-effort reconciler — never fail the reindex on an FTS rebuild error.
                app.Logger.LogWarning(ex, "FTS rebuild failed during memory reindex.");
                ftsReindexed = 0;
            }

            return Results.Ok(new { embedded, skipped, failed, ftsReindexed, model = options.Model });
        });

        return app;
    }
}
