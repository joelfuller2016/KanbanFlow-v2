using System.Data;
using System.Diagnostics;
using KanbanFlow.Data;
using KanbanFlow.Services;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow;

public static class AdminEndpoints
{
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
        // and no second kanbanflow.db is shadowing the one in use. What it CANNOT know is which
        // database the operator meant — the launcher pins KANBANFLOW_DB_PATH from its own
        // ($HOME-derived) computation, so the app faithfully opens whatever it was handed. That
        // half is completed by the caller comparing db.path against the expected file, which is
        // why the resolved path is reported rather than merely checked (#477 §9 criterion 3).
        app.MapGet("/api/admin/health", async (
            IAppStartupInfo startup,
            DbPathResolution database,
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            var stopwatch = Stopwatch.StartNew();
            int? projects = null;
            string? probeError = null;

            try
            {
                await using var db = await dbFactory.CreateDbContextAsync(ct);

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
                // the caller can only guess at. Message only — never the stack.
                probeError = ex.Message;
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
                    exists = File.Exists(database.Path),
                    source = database.Source,
                    existedAtStartup = database.Existed,
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
