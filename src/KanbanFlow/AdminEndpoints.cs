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
        // GET /api/admin/health — quick smoke that the app is up and DI is wired.
        // openApiUrl advertises the OpenAPI document path so agents can discover it.
        app.MapGet("/api/admin/health", (IAppStartupInfo startup) => Results.Ok(new
        {
            status = "ok",
            app = "KanbanFlow",
            startedAtUtc = startup.StartedAtUtc,
            checkedAtUtc = DateTime.UtcNow,
            runtime = $".NET {Environment.Version}",
            openApiUrl = "/openapi.json"
        }));

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
                        await MemoryFts.UpsertAsync(connection, tx, node.Id, node.Content, ct);
                        ftsReindexed++;
                    }

                    await tx.CommitAsync(ct);
                }
                finally
                {
                    if (openedByUs)
                        await connection.CloseAsync();
                }
            }
            catch
            {
                // Best-effort reconciler — never fail the reindex on an FTS rebuild error.
                ftsReindexed = 0;
            }

            return Results.Ok(new { embedded, skipped, failed, ftsReindexed, model = options.Model });
        });

        return app;
    }
}
