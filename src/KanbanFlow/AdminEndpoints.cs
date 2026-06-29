using System.Diagnostics;
using KanbanFlow.Services;

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

        return app;
    }
}
