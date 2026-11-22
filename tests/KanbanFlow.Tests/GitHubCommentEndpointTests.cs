using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KanbanFlow.Data;
using KanbanFlow.Models;
using KanbanFlow.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KanbanFlow.Tests;

public sealed class GitHubCommentEndpointTests
{
    private const string NodeId = "IC_review_123";
    private const string SourceHeader = "<!-- gh:IC_review_123 -->";
    private const string CompleteMarker = "<!-- kanbanflow-comment:complete -->";
    private const string TruncationFooter = "\u2026(truncated \u2014 read it on GitHub)";
    private const string SourceBody = "_[issue comment](https://github.com/example/repo/issues/7#issuecomment-123)_\n\n  Source Markdown.  \n\n";
    private const string FullText = SourceHeader + "\n" + CompleteMarker + "\n" + SourceBody;

    [Fact]
    public async Task ConcurrentImportsOnOneTaskSaveOneCommentAndReturnItAgainAfterRestart()
    {
        using var factory = new SqliteFileTestFactory();
        var taskId = await SeedTaskAsync(factory);
        var race = new ImportTransactionRendezvous();
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(SqliteFileTestFactory.DefaultBusyTimeoutSeconds + 5));
        int existingId;
        await using (var firstApp = await StartAsync(factory, race))
        await using (var secondApp = await StartAsync(factory, race))
        {
            using var first = CreateClient(firstApp);
            using var second = CreateClient(secondApp);
            var firstImport = ImportAsync(first, taskId, ct: watchdog.Token);
            await race.FirstStarted.Task.WaitAsync(watchdog.Token);
            var completed = await Task.WhenAll(firstImport, ImportAsync(second, taskId, ct: watchdog.Token));
            using var firstResponse = completed[0];
            using var secondResponse = completed[1];

            await using var db = factory.CreateDbContext();
            var comment = Assert.Single(await db.TaskComments.ToListAsync());
            existingId = comment.Id;
            Assert.Equal(FullText, comment.Text);
            var audit = Assert.Single(await db.ActivityEvents.Where(a => a.EventType == "TaskCommentAdded").ToListAsync());
            Assert.Equal(taskId, audit.EntityId);
            Assert.Equal("source-author", audit.Actor);
            using var auditSnapshot = JsonDocument.Parse(audit.AfterJson!);
            Assert.Equal(FullText, auditSnapshot.RootElement.GetProperty("text").GetString());

            Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.Created },
                completed.Select(r => r.StatusCode).Order());
            foreach (var response in completed)
            {
                var content = await response.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal(existingId, content.GetProperty("id").GetInt32());
                Assert.Equal(taskId, content.GetProperty("taskItemId").GetInt32());
                Assert.Equal(FullText, content.GetProperty("text").GetString());
                Assert.Equal("source-author", content.GetProperty("author").GetString());
                Assert.Equal("Note", content.GetProperty("type").GetString());
                Assert.True(content.GetProperty("createdAt").TryGetDateTime(out _));
                Assert.Equal(response.StatusCode == HttpStatusCode.Created, content.GetProperty("created").GetBoolean());
            }
            Assert.Equal($"/api/tasks/{taskId}/comments/{existingId}",
                completed.Single(r => r.StatusCode == HttpStatusCode.Created).Headers.Location?.OriginalString);
        }

        await using var restartedApp = await StartAsync(factory);
        using var restarted = CreateClient(restartedApp);
        using var repeated = await ImportAsync(restarted, taskId, text: FullText + "GitHub edit");
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        var persisted = await repeated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(existingId, persisted.GetProperty("id").GetInt32());
        Assert.Equal(FullText, persisted.GetProperty("text").GetString());
        Assert.False(persisted.GetProperty("created").GetBoolean());

        await using var check = factory.CreateDbContext();
        Assert.Equal(1, await check.TaskComments.CountAsync());
        Assert.Equal(1, await check.ActivityEvents.CountAsync(a => a.EventType == "TaskCommentAdded"));
    }

    [Theory]
    [InlineData("An earlier complete body.  \n", false)]
    [InlineData("A clipped body.\n\n" + TruncationFooter + "  \n", true)]
    [InlineData(SourceBody + TruncationFooter, false)]
    [InlineData(CompleteMarker + "\r\nA full body ending with " + TruncationFooter, false)]
    public async Task ImportRetainsHistoricalTextAndRepairsOnlyClippedImports(string historyBody, bool shouldCreate)
    {
        using var factory = new SqliteFileTestFactory();
        var taskId = await SeedTaskAsync(factory);
        var oldText = SourceHeader + "\n" + historyBody;
        await using (var db = factory.CreateDbContext())
        {
            db.TaskComments.Add(new TaskComment { TaskItemId = taskId, Text = oldText });
            await db.SaveChangesAsync();
        }

        await using var app = await StartAsync(factory);
        using var client = CreateClient(app);
        using var response = await ImportAsync(client, taskId,
            text: historyBody == SourceBody + TruncationFooter ? FullText + TruncationFooter : FullText);

        Assert.Equal(shouldCreate ? HttpStatusCode.Created : HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(shouldCreate, result.GetProperty("created").GetBoolean());
        Assert.Equal(shouldCreate ? FullText : oldText, result.GetProperty("text").GetString());
        await using var check = factory.CreateDbContext();
        var saved = await check.TaskComments.OrderBy(c => c.Id).Select(c => c.Text).ToListAsync();
        Assert.Equal(shouldCreate ? new[] { oldText, FullText } : [oldText], saved);
        Assert.Equal(shouldCreate ? 1 : 0, await check.ActivityEvents.CountAsync(a => a.EventType == "TaskCommentAdded"));
    }

    [Theory]
    [InlineData(null, FullText)]
    [InlineData("", FullText)]
    [InlineData("IC_other", FullText)]
    [InlineData("IC_bad -->", "<!-- gh:IC_bad --> -->\n" + CompleteMarker + "\n" + SourceBody)]
    [InlineData(NodeId, SourceHeader + "\n" + SourceBody)]
    [InlineData(NodeId, SourceHeader + "\n" + CompleteMarker)]
    [InlineData(NodeId, "A message quoting the import lines.\n" + FullText)]
    public async Task ImportRejectsAnInvalidLeadingSourceMarker(string? id, string text)
    {
        using var factory = new SqliteFileTestFactory();
        var taskId = await SeedTaskAsync(factory);
        await using var app = await StartAsync(factory);
        using var client = CreateClient(app);
        using var response = await ImportAsync(client, taskId, id, text);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await using var db = factory.CreateDbContext();
        Assert.Equal(0, await db.TaskComments.CountAsync());
        Assert.Equal(0, await db.ActivityEvents.CountAsync(a => a.EventType == "TaskCommentAdded"));
    }

    [Fact]
    public async Task ImportRequiresExplicitNodeIdEvenWhenTextContainsTheMarker()
    {
        using var factory = new SqliteFileTestFactory();
        var taskId = await SeedTaskAsync(factory);
        await using var app = await StartAsync(factory);
        using var client = CreateClient(app);
        using var response = await client.PostAsJsonAsync(ImportRoute(taskId), new { text = FullText, type = "Note" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await using var db = factory.CreateDbContext();
        Assert.Equal(0, await db.TaskComments.CountAsync());
        Assert.Equal(0, await db.ActivityEvents.CountAsync(a => a.EventType == "TaskCommentAdded"));
    }

    [Fact]
    public async Task ImportIdentityIsScopedToExactNodeAndTask()
    {
        using var factory = new SqliteFileTestFactory();
        var taskId = await SeedTaskAsync(factory);
        var otherTaskId = await SeedTaskAsync(factory);
        await using var app = await StartAsync(factory);
        using var client = CreateClient(app);
        using var first = await ImportAsync(client, taskId);
        using var otherTask = await ImportAsync(client, otherTaskId);
        using var otherNode = await ImportAsync(client, taskId, NodeId + "4",
            FullText.Replace(NodeId, NodeId + "4", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, otherTask.StatusCode);
        Assert.Equal(HttpStatusCode.Created, otherNode.StatusCode);
        foreach (var response in new[] { first, otherTask, otherNode })
        {
            var saved = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(saved.GetProperty("created").GetBoolean());
        }
        await using var db = factory.CreateDbContext();
        Assert.Equal(3, await db.TaskComments.CountAsync());
    }

    [Fact]
    public async Task FailedAuditLeavesNoImportedCommentAndRetryCanCreateIt()
    {
        using var factory = new SqliteFileTestFactory();
        var taskId = await SeedTaskAsync(factory);
        await using var db = factory.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER reject_comment_audit BEFORE INSERT ON ActivityEvents
            WHEN NEW.EventType = 'TaskCommentAdded'
            BEGIN SELECT RAISE(ABORT, 'audit write rejected'); END;
            """);
        await using var app = await StartAsync(factory);
        using var client = CreateClient(app);
        using var failed = await ImportAsync(client, taskId);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal(0, await db.TaskComments.CountAsync());
        Assert.Equal(0, await db.ActivityEvents.CountAsync(a => a.EventType == "TaskCommentAdded"));

        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_comment_audit");
        using var retried = await ImportAsync(client, taskId);
        Assert.Equal(HttpStatusCode.Created, retried.StatusCode);
        var result = await retried.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.GetProperty("created").GetBoolean());
        Assert.Equal(1, await db.TaskComments.CountAsync());
        Assert.Equal(1, await db.ActivityEvents.CountAsync(a => a.EventType == "TaskCommentAdded"));
    }

    [Fact]
    public async Task NormalPostsKeepTheLegacyResponseAndAppendDuplicateText()
    {
        using var factory = new SqliteFileTestFactory();
        var taskId = await SeedTaskAsync(factory);
        var route = $"/api/tasks/{taskId}/comments";
        await using var app = await StartAsync(factory);
        using var client = CreateClient(app);

        var ids = new List<int>();
        for (var i = 0; i < 2; i++)
        {
            // The ordinary endpoint accepts unknown properties and appends a new comment.
            // A GitHub ID field must never switch this route to the import operation.
            object payload = i == 0
                ? new { text = FullText, author = " ordinary author " }
                : new { text = FullText, author = " ordinary author ", githubCommentId = NodeId };
            using var response = await client.PostAsJsonAsync(route, payload);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var comment = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(new[] { "id", "taskItemId", "text", "author", "type", "createdAt" },
                comment.EnumerateObject().Select(p => p.Name));
            var id = comment.GetProperty("id").GetInt32();
            Assert.Equal($"{route}/{id}", response.Headers.Location?.OriginalString);
            Assert.Equal("ordinary author", comment.GetProperty("author").GetString());
            Assert.Equal("Note", comment.GetProperty("type").GetString());
            Assert.Equal(FullText, comment.GetProperty("text").GetString());
            ids.Add(id);
        }
        Assert.NotEqual(ids[0], ids[1]);
        await using var db = factory.CreateDbContext();
        Assert.Equal(2, await db.TaskComments.CountAsync());
        Assert.Equal(2, await db.ActivityEvents.CountAsync(a => a.EventType == "TaskCommentAdded"));
    }

    private static async Task<int> SeedTaskAsync(SqliteFileTestFactory factory)
    {
        await using var db = factory.CreateDbContext();
        var task = new TaskItem { Name = "Task", Feature = new Feature { Name = "Feature", Project = new Project { Name = "Project" } } };
        db.Tasks.Add(task);
        await db.SaveChangesAsync();
        return task.Id;
    }

    private static Task<HttpResponseMessage> ImportAsync(
        HttpClient client, int taskId, string? id = NodeId, string text = FullText, CancellationToken ct = default)
        => client.PostAsJsonAsync(ImportRoute(taskId), new { text, author = "source-author", type = "Note", githubCommentId = id }, ct);

    private static string ImportRoute(int taskId) => $"/api/tasks/{taskId}/comment-imports/github";

    private static HttpClient CreateClient(WebApplication app) => new() { BaseAddress = new Uri(app.Urls.Single()) };

    private static async Task<WebApplication> StartAsync(SqliteFileTestFactory factory, IInterceptor? interceptor = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddDbContextFactory<AppDbContext>(options =>
        {
            options.UseSqlite(factory.ConnectionString);
            if (interceptor is not null)
                options.AddInterceptors(interceptor);
        });
        builder.Services.AddSingleton(new ClaimTtlSetting(ClaimPolicy.DefaultTtl));
        builder.Services.AddScoped<ITaskService, TaskService>();
        builder.Services.AddScoped<ITaskDependencyService, TaskDependencyService>();
        builder.Services.AddScoped<IIdeaService, IdeaService>();
        builder.Services.AddScoped<IMemoryService, MemoryService>();
        builder.Services.AddScoped<IExternalWorkItemService, ExternalWorkItemService>();
        // Memory search is registered for API endpoint binding and is unrelated to these requests.
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

    // Hold the first transaction open until the second request attempts its own transaction.
    // If the lookup moves before transaction start, both requests will already have read the
    // empty table at this rendezvous and the race will produce a duplicate.
    private sealed class ImportTransactionRendezvous : DbTransactionInterceptor
    {
        private readonly TaskCompletionSource _secondStarting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _attempts;

        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection, TransactionStartingEventData data, InterceptionResult<DbTransaction> result, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _attempts) == 2)
                _secondStarting.TrySetResult();
            return ValueTask.FromResult(result);
        }

        public override async ValueTask<DbTransaction> TransactionStartedAsync(
            DbConnection connection, TransactionEndEventData data, DbTransaction result, CancellationToken ct = default)
        {
            if (FirstStarted.TrySetResult())
                await _secondStarting.Task.WaitAsync(ct);
            return result;
        }
    }
}
