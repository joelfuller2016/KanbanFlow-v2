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
    /// </summary>
    [Fact]
    public async Task Delete_WithoutActorQueryParameter_LeavesTheAuditEventActorNull()
    {
        using var f = new SqliteTestFactory();
        var taskId = Seed(f);

        await using var host = await TaskHost.StartAsync(f);
        using var response = await host.Client.DeleteAsync($"/api/tasks/{taskId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var db = f.CreateDbContext();
        var audit = await db.ActivityEvents.SingleAsync(e => e.EntityId == taskId && e.EventType == "TaskDeleted");
        Assert.Null(audit.Actor);
    }

    /// <summary>A whitespace-only actor is not a meaningful attribution. The ROUTE forwards it raw
    /// (no local normalization — same thin pass-through as the sibling test above); this is an
    /// end-to-end guard that the SERVICE layer's own normalization
    /// (<c>TaskService</c>'s shared <c>AddActivityEvent</c>:
    /// <c>string.IsNullOrWhiteSpace(actor) ? null : actor.Trim()</c>) still reduces it to the same
    /// "nothing was said" null as an omitted actor. NOT analogous to the <c>release</c> route's
    /// <c>IsNullOrWhiteSpace</c> guard (<c>POST /api/tasks/{id}/release</c> in <c>ApiEndpoints.cs</c>)
    /// — that route REJECTS a blank actor with 422; it does not null it.</summary>
    [Fact]
    public async Task Delete_WithWhitespaceOnlyActor_NormalizesToNull()
    {
        using var f = new SqliteTestFactory();
        var taskId = Seed(f);

        await using var host = await TaskHost.StartAsync(f);
        using var response = await host.Client.DeleteAsync($"/api/tasks/{taskId}?actor=%20%20");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var db = f.CreateDbContext();
        var audit = await db.ActivityEvents.SingleAsync(e => e.EntityId == taskId && e.EventType == "TaskDeleted");
        Assert.Null(audit.Actor);
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

            // Every MapTaskEndpoints route needs only these two — a scoped AppDbContext sharing
            // the factory's single open in-memory connection (so the route sees what the test
            // seeded), and the same real TaskService the service-level tests use.
            builder.Services.AddScoped(_ => factory.CreateDbContext());
            builder.Services.AddSingleton<ITaskService>(new TaskService(factory));

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
