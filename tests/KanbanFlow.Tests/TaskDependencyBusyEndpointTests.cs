using System.Net;
using System.Net.Http.Json;
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

public sealed class TaskDependencyBusyEndpointTests
{
    private const int CommandTimeoutSeconds = 1;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DependencyWriteWithWriterAlreadyHeldReturnsRetryable503(bool delete)
    {
        using var factory = new SqliteFileTestFactory(commandTimeoutSeconds: CommandTimeoutSeconds);
        var (blockedId, blockingId, dependencyId) = await SeedAsync(factory, delete);
        await using var app = await StartAsync(factory);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(10) };

        // A separate connection holds the writer slot until after the HTTP request finishes.
        await using var locker = new SqliteConnection(factory.ConnectionString);
        await locker.OpenAsync();
        await using (var writer = locker.BeginTransaction(deferred: false))
        {
            using var busy = await SendWriteAsync(client, blockedId, blockingId, dependencyId);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, busy.StatusCode);
            Assert.Equal(TimeSpan.FromSeconds(1), busy.Headers.RetryAfter?.Delta);
            Assert.Equal("application/json", busy.Content.Headers.ContentType?.MediaType);
            var body = await busy.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("The data store is busy. Retry the request.", body.GetProperty("message").GetString());
            await writer.RollbackAsync();
        }

        await AssertNoChangesAsync(factory, dependencyId);

        using var retry = await SendWriteAsync(client, blockedId, blockingId, dependencyId);
        Assert.Equal(delete ? HttpStatusCode.NoContent : HttpStatusCode.Created, retry.StatusCode);

        using var repeated = await SendWriteAsync(client, blockedId, blockingId, dependencyId);
        Assert.Equal(delete ? HttpStatusCode.NotFound : HttpStatusCode.Conflict, repeated.StatusCode);
        Assert.Null(repeated.Headers.RetryAfter);
        var repeatedBody = await repeated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            delete ? $"Task dependency {dependencyId} not found." : "This task dependency already exists.",
            repeatedBody.GetProperty("message").GetString());

        await using var db = factory.CreateDbContext();
        var activity = Assert.Single(await db.ActivityEvents.ToListAsync());
        Assert.Equal(delete ? "TaskDependencyDeleted" : "TaskDependencyCreated", activity.EventType);
        Assert.Equal("api", activity.Actor);
    }

    [Fact]
    public async Task CreateServicePreservesTheOriginalSqliteBusyError()
    {
        using var factory = new SqliteFileTestFactory(commandTimeoutSeconds: CommandTimeoutSeconds);
        var (blockedId, blockingId, _) = await SeedAsync(factory, seedDependency: false);
        var service = new TaskDependencyService(factory);
        await using var locker = new SqliteConnection(factory.ConnectionString);
        await locker.OpenAsync();
        await using var writer = locker.BeginTransaction(deferred: false);

        var failure = await Assert.ThrowsAsync<StoreBusyException>(() =>
            service.CreateAsync(new TaskDependencyCreate(blockedId, blockingId, TaskDependencyType.DependsOn)));

        Assert.Equal(SQLitePCL.raw.SQLITE_BUSY, Assert.IsType<SqliteException>(failure.InnerException).SqliteErrorCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DependencyWriteWithConstraintFailureRemains500(bool delete)
    {
        using var factory = new SqliteFileTestFactory(commandTimeoutSeconds: CommandTimeoutSeconds);
        var (blockedId, blockingId, dependencyId) = await SeedAsync(factory, delete);
        await using (var db = factory.CreateDbContext())
        {
            var trigger = delete
                ? "CREATE TRIGGER reject_dependency_delete BEFORE DELETE ON TaskDependencies BEGIN SELECT RAISE(ABORT, 'test constraint'); END"
                : "CREATE TRIGGER reject_dependency_insert BEFORE INSERT ON TaskDependencies BEGIN SELECT RAISE(ABORT, 'test constraint'); END";
            await db.Database.ExecuteSqlRawAsync(trigger);
        }

        await using var app = await StartAsync(factory);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(10) };
        using var failure = await SendWriteAsync(client, blockedId, blockingId, dependencyId);

        Assert.Equal(HttpStatusCode.InternalServerError, failure.StatusCode);
        Assert.Null(failure.Headers.RetryAfter);
        await AssertNoChangesAsync(factory, dependencyId);
    }

    private static async Task<(int BlockedId, int BlockingId, int? DependencyId)> SeedAsync(
        SqliteFileTestFactory factory, bool seedDependency)
    {
        await using var db = factory.CreateDbContext();
        var project = new Project { Name = "Project" };
        var feature = new Feature { Name = "Feature", Project = project };
        var blocked = new TaskItem { Name = "Blocked", Feature = feature };
        var blocking = new TaskItem { Name = "Blocking", Feature = feature };
        db.Tasks.AddRange(blocked, blocking);

        TaskDependency? dependency = null;
        if (seedDependency)
        {
            dependency = new TaskDependency { Project = project, TaskItem = blocked, DependsOnTaskItem = blocking };
            db.TaskDependencies.Add(dependency);
        }
        await db.SaveChangesAsync();
        return (blocked.Id, blocking.Id, dependency?.Id);
    }

    private static Task<HttpResponseMessage> SendWriteAsync(HttpClient client, int blockedId, int blockingId, int? dependencyId)
        => dependencyId is int id
            ? client.DeleteAsync($"/api/task-dependencies/{id}")
            : client.PostAsJsonAsync($"/api/tasks/{blockedId}/dependencies", new { dependsOnTaskItemId = blockingId });

    private static async Task AssertNoChangesAsync(SqliteFileTestFactory factory, int? dependencyId)
    {
        await using var db = factory.CreateDbContext();
        var dependencyIds = await db.TaskDependencies.Select(dependency => dependency.Id).ToListAsync();
        Assert.Equal(dependencyId is int id ? [id] : Array.Empty<int>(), dependencyIds);
        Assert.Empty(await db.ActivityEvents.ToListAsync());
    }

    private static async Task<WebApplication> StartAsync(SqliteFileTestFactory factory)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddHttpContextAccessor();
        var connectionString = new SqliteConnectionStringBuilder(factory.ConnectionString) { DefaultTimeout = CommandTimeoutSeconds };
        builder.Services.AddDbContextFactory<AppDbContext>(options => options.UseSqlite(connectionString.ConnectionString));
        builder.Services.AddSingleton(new ClaimTtlSetting(ClaimPolicy.DefaultTtl));
        builder.Services.AddScoped<ITaskService, TaskService>();
        builder.Services.AddScoped<ITaskDependencyService, TaskDependencyService>();
        builder.Services.AddScoped<IIdeaService, IdeaService>();
        builder.Services.AddScoped<IMemoryService, MemoryService>();
        builder.Services.AddScoped<IExternalWorkItemService, ExternalWorkItemService>();
        builder.Services.AddSingleton<IMemorySearch>(_ => throw new NotSupportedException());

        var app = builder.Build();
        try
        {
            app.MapApiEndpoints();
            await app.StartAsync();
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }
}
