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

        return app;
    }
}
