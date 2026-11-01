using System.Net;
using KanbanFlow.Data;
using KanbanFlow.Models;
using KanbanFlow.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KanbanFlow.Tests;

/// <summary>
/// <c>DELETE /api/tasks/{id}</c> over REAL HTTP against a REAL SQLite database (F3 of the
/// wrapper/native MCP parity review of master @ 1589e43, filed as #537): the
/// route hardcoded <c>actor: null</c> with no way for a caller to attribute the deletion, while
/// <see cref="TaskServiceMutationTests.Delete_RemovesTaskAndCascadeChildren_AuditSurvives"/>
/// already proves the SERVICE layer threads a given actor through correctly. The defect lived in
/// the ROUTE, not the service — so this test drives the mapped endpoint over the wire (query
/// string binding included) rather than calling <see cref="TaskService.DeleteAsync"/> directly,
/// which would not exercise the binding bug at all.
///
/// <para>Mirrors the real-Kestrel-host pattern in <see cref="AdminHealthEndpointTests"/>, but maps
/// only <see cref="ApiEndpoints.MapTaskEndpoints"/> (made <c>internal</c> for this) instead of the
/// full <see cref="ApiEndpoints.MapApiEndpoints"/> surface: every task route needs only
/// <see cref="AppDbContext"/> and <see cref="ITaskService"/>, so there is no LLM/embedding stub
/// list to maintain the way the admin-health host needs (five routes, six stub types).</para>
/// </summary>
public sealed class TaskDeleteActorEndpointTests
{
    [Fact]
    public async Task Delete_WithActorQueryParameter_AttributesTheAuditEvent()
    {
        using var f = new SqliteTestFactory();
        var taskId = Seed(f);

        await using var host = await TaskHost.StartAsync(f);
        using var response = await host.Client.DeleteAsync($"/api/tasks/{taskId}?actor=regression-actor");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var db = f.CreateDbContext();
        var audit = await db.ActivityEvents.SingleAsync(e => e.EntityId == taskId && e.EventType == "TaskDeleted");
        Assert.Equal("regression-actor", audit.Actor);
    }

    /// <summary>
    /// REST stays a thin pass-through here, same as the other raw-forwarding task routes in
    /// <c>ApiEndpoints.cs</c> (e.g. PATCH .../status forwards <c>dto.Actor</c> raw, unnormalized). Defaulting an omitted actor
    /// to "mcp-agent" is each CALLER's job — the wrapper's <c>delete_task</c> case handler, or a
    /// native tool method — not the REST route's; proven for the wrapper by
    /// scripts/mcp-parity.test.mjs's "stdio delete_task defaults actor to mcp-agent when omitted".
    /// Missing/blank attribution falls back through the shared audit boundary to the request's
    /// <c>X-KanbanFlow-Actor</c> header, then <c>api</c>, as it does for other saved board changes.
    /// </summary>
    [Theory]
    [InlineData(null, null, "api")]
    [InlineData("  ", null, "api")]
    [InlineData(null, "header-actor", "header-actor")]
    [InlineData("  ", "header-actor", "header-actor")]
    [InlineData("  query-actor  ", "header-actor", "query-actor")]
    public async Task Delete_ActorSelection_UsesQueryThenRequestAuditDefault(string? actor, string? header, string expected)
    {
        using var f = new SqliteTestFactory();
        var taskId = Seed(f);

        await using var host = await TaskHost.StartAsync(f);
        var query = actor is null ? "" : "?actor=" + Uri.EscapeDataString(actor);
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/tasks/{taskId}{query}");
        if (header is not null) request.Headers.Add("X-KanbanFlow-Actor", header);
        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var db = f.CreateDbContext();
        var audit = await db.ActivityEvents.SingleAsync(e => e.EntityId == taskId && e.EventType == "TaskDeleted");
        Assert.Equal(expected, audit.Actor);
    }

    private static int Seed(SqliteTestFactory f)
    {
        using var db = f.CreateDbContext();
        var feature = new Feature { Name = "Feat", Project = new Project { Name = "Proj" } };
        var task = new TaskItem { Name = "Task", Feature = feature, Status = WorkStatus.Todo };
        db.Add(task);
        db.SaveChanges();
        return task.Id;
    }

    /// <summary>A real Kestrel host on an ephemeral loopback port with only the task routes mapped.</summary>
    private sealed class TaskHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private TaskHost(WebApplication app, HttpClient client)
        {
            _app = app;
            Client = client;
        }

        public HttpClient Client { get; }

        public static async Task<TaskHost> StartAsync(SqliteTestFactory factory)
        {
            var builder = WebApplication.CreateBuilder(
                new WebApplicationOptions { EnvironmentName = "Development" });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0"); // ephemeral port: tests can run in parallel

            // Share the seeded connection while retaining Program.cs's HTTP-aware context
            // construction, so the real audit boundary sees request headers and its API default.
            using var shared = factory.CreateDbContext();
            var connection = shared.Database.GetDbConnection();
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddDbContextFactory<AppDbContext>(options => options.UseSqlite(connection));
            builder.Services.AddScoped<ITaskService>(sp => new TaskService(sp.GetRequiredService<IDbContextFactory<AppDbContext>>()));

            var app = builder.Build();
            try
            {
                app.MapTaskEndpoints();
                await app.StartAsync();

                var address = app.Urls.First();
                return new TaskHost(app, new HttpClient { BaseAddress = new Uri(address) });
            }
            catch
            {
                // See AdminHealthEndpointTests.HealthHost.StartAsync for why this catch exists:
                // WebApplication.StartAsync does not dispose a host it failed to start.
                await app.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
