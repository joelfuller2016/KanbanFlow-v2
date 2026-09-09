using System.Data.Common;
using System.Text.Json;
using KanbanFlow.Data;
using KanbanFlow.Mcp;
using KanbanFlow.Models;
using KanbanFlow.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace KanbanFlow.Tests;

public sealed class ExternalWorkItemProjectionTests
{
    private const string Description = "\r\n    Indented source Ω\r\n\r\n```text\r\n  tail  \r\n```\r\n\r\n";

    [Fact]
    public async Task ConcurrentProjection_OneTaskLinkAndAuditPair_BothCallersConverge()
    {
        var pause = new PauseFirstTaskSave();
        var transactions = new TransactionAttempts();
        using var factory = new SqliteFileTestFactory([pause, transactions]);
        var feature = await SeedFeature(factory);
        var service = new ExternalWorkItemService(factory);
        var request = Request(feature);
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        pause.Enabled = true;
        transactions.Enabled = true;
        var first = Task.Run(() => service.ProjectInboundGitHubIssueAsync(request, "importer", watchdog.Token));
        Task<ExternalWorkItemProjectionResult>? second = null;
        try
        {
            await pause.TaskConstructed.Task.WaitAsync(watchdog.Token);
            second = Task.Run(() => service.ProjectInboundGitHubIssueAsync(request, "importer", watchdog.Token));
            await transactions.SecondAttempt.Task.WaitAsync(watchdog.Token);
        }
        finally { pause.Release.TrySetResult(); }
        var results = await Task.WhenAll(first, second!).WaitAsync(watchdog.Token);
        Assert.Single(results, r => r.Created);
        Assert.Single(results, r => !r.Created);
        Assert.Equal(results[0].Task.Id, results[1].Task.Id);
        Assert.Equal(results[0].Link.Id, results[1].Link.Id);
        await AssertOneProjection(factory, results[0].Task.Id);
    }

    [Fact]
    public async Task SameProjectReplay_PreservesHumanEditsFeatureAndFullDescription_WithoutAudit()
    {
        using var factory = new SqliteTestFactory();
        var feature = await SeedFeature(factory);
        var service = new ExternalWorkItemService(factory);
        var created = await service.ProjectInboundGitHubIssueAsync(Request(feature), "importer");
        await using (var db = await factory.CreateDbContextAsync())
        {
            var saved = await db.Tasks.SingleAsync();
            saved.Name = "Human decision";
            saved.Description = Description + "Human continuation  \r\n";
            saved.Status = WorkStatus.InProgress;
            await db.SaveChangesAsync();
        }
        await using var lookup = await factory.CreateDbContextAsync();
        var projectId = await lookup.Features.Where(f => f.Id == feature).Select(f => f.ProjectId).SingleAsync();
        var sibling = await SeedFeature(factory, projectId);
        var replay = await service.ProjectInboundGitHubIssueAsync(Request(sibling) with
        {
            Task = new TaskCreate(sibling, "Incoming stale name", "Incoming replacement", WorkStatus.Done)
        }, "retry");
        Assert.False(replay.Created);
        Assert.Equal(created.Task.Id, replay.Task.Id);
        Assert.Equal(feature, replay.Task.FeatureId);
        Assert.Equal("Human decision", replay.Task.Name);
        Assert.Equal(Description + "Human continuation  \r\n", replay.Task.Description);
        Assert.Equal(WorkStatus.InProgress, replay.Task.Status);
        await AssertOneProjection(factory, created.Task.Id, checkDescription: false);
    }

    [Fact]
    public async Task LinkSaveFailure_RollsBackTaskCommentAndAudit_ThenRetrySucceeds()
    {
        var failure = new FailLinkSave();
        using var factory = new SqliteFileTestFactory(failure);
        var feature = await SeedFeature(factory);
        var service = new ExternalWorkItemService(factory);
        failure.Enabled = true;
        await Assert.ThrowsAsync<InjectedLinkFailure>(() => service.ProjectInboundGitHubIssueAsync(Request(feature), "importer"));
        Assert.True(failure.TaskHadAssignedId);
        await using (var db = await factory.CreateDbContextAsync())
        {
            Assert.Empty(await db.Tasks.AsNoTracking().ToListAsync());
            Assert.Empty(await db.ExternalWorkItemLinks.AsNoTracking().ToListAsync());
            Assert.Empty(await db.TaskComments.AsNoTracking().ToListAsync());
            Assert.Empty(await db.ActivityEvents.AsNoTracking().ToListAsync());
        }
        failure.Enabled = false;
        var retry = await service.ProjectInboundGitHubIssueAsync(Request(feature), "importer");
        Assert.True(retry.Created);
        await AssertOneProjection(factory, retry.Task.Id);
    }

    [Theory]
    [InlineData("cross-project", 409, "external-node-cross-project")]
    [InlineData("other-node", 409, "external-number-already-linked")]
    [InlineData("other-number", 409, "external-node-number-mismatch")]
    [InlineData("other-repository", 422, null)]
    [InlineData("wrong-url", 422, null)]
    [InlineData("rename", 422, null)]
    public async Task IdentityConflict_EndpointReturnsStructuredError_WithoutAnySave(
        string conflict, int status, string? code)
    {
        var saves = new CountSaves();
        using var factory = new SqliteFileTestFactory(saves);
        var feature = await SeedFeature(factory);
        var service = new ExternalWorkItemService(factory);
        var created = await service.ProjectInboundGitHubIssueAsync(Request(feature), "importer");
        var dto = Dto();
        if (conflict == "cross-project") feature = await SeedFeature(factory);
        if (conflict == "other-node") dto = dto with { ExternalNodeId = "I_other" };
        if (conflict == "other-number") dto = dto with { ExternalNumber = 99, Url = "https://github.com/octo/board/issues/99" };
        if (conflict == "other-repository") dto = dto with { RepositoryExternalId = "R_other" };
        if (conflict == "wrong-url") dto = dto with { Url = "https://github.com/foreign/board/issues/42" };
        if (conflict == "rename") dto = dto with { RepositoryName = "renamed", Url = "https://github.com/octo/renamed/issues/42" };
        saves.Count = 0;
        var result = await ApiEndpoints.ProjectInboundGitHubIssueEndpointAsync(feature, dto, service, default);
        Assert.Equal(status, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        var payload = JsonSerializer.SerializeToElement(Assert.IsAssignableFrom<IValueHttpResult>(result).Value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.False(string.IsNullOrWhiteSpace(payload.GetProperty("message").GetString()));
        if (code is not null)
        {
            Assert.Equal(code, payload.GetProperty("conflictCode").GetString());
            Assert.Equal(created.Task.Id, payload.GetProperty("taskId").GetInt32());
        }
        Assert.Equal(0, saves.Count);
        await AssertOneProjection(factory, created.Task.Id);
    }

    [Fact]
    public async Task Endpoint_CreateAndReplay_Return201LocationThen200_WithSameIdentity()
    {
        using var factory = new SqliteTestFactory();
        var feature = await SeedFeature(factory);
        var service = new ExternalWorkItemService(factory);
        var created = await ApiEndpoints.ProjectInboundGitHubIssueEndpointAsync(feature, Dto(), service, default);
        Assert.Equal(201, Assert.IsAssignableFrom<IStatusCodeHttpResult>(created).StatusCode);
        var first = Assert.IsType<ExternalWorkItemProjectionResponse>(Assert.IsAssignableFrom<IValueHttpResult>(created).Value);
        Assert.Equal($"/api/tasks/{first.Task.Id}", Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.HttpResults.Created<ExternalWorkItemProjectionResponse>>(created).Location);
        Assert.True(first.Created);
        Assert.Equal(Description, first.Task.Description);
        var replay = await ApiEndpoints.ProjectInboundGitHubIssueEndpointAsync(feature, Dto(), service, default);
        Assert.Equal(200, Assert.IsAssignableFrom<IStatusCodeHttpResult>(replay).StatusCode);
        var second = Assert.IsType<ExternalWorkItemProjectionResponse>(Assert.IsAssignableFrom<IValueHttpResult>(replay).Value);
        Assert.False(second.Created);
        Assert.Equal(first.Task, second.Task);
        Assert.Equal(first.Link.Id, second.Link.Id);
        await AssertOneProjection(factory, first.Task.Id);
    }

    [Theory]
    [InlineData("null", 400)]
    [InlineData("missing-feature", 404)]
    [InlineData("name", 422)]
    [InlineData("status", 422)]
    [InlineData("priority", 422)]
    [InlineData("sort", 422)]
    public async Task InvalidRequest_EndpointRejectsBeforeAnySave(string invalid, int status)
    {
        var saves = new CountSaves();
        using var factory = new SqliteFileTestFactory(saves);
        var feature = await SeedFeature(factory);
        ProjectInboundGitHubIssueDto? dto = Dto();
        if (invalid == "null") dto = null;
        if (invalid == "missing-feature") feature = 123456;
        if (invalid == "name") dto = dto! with { Name = " " };
        if (invalid == "status") dto = dto! with { Status = "123" };
        if (invalid == "priority") dto = dto! with { Priority = "bogus" };
        if (invalid == "sort") dto = dto! with { SortOrder = double.PositiveInfinity };
        saves.Count = 0;
        var result = await ApiEndpoints.ProjectInboundGitHubIssueEndpointAsync(feature, dto, new ExternalWorkItemService(factory), default);
        Assert.Equal(status, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Equal(0, saves.Count);
        await using var db = await factory.CreateDbContextAsync();
        Assert.Empty(await db.Tasks.ToListAsync());
        Assert.Empty(await db.ExternalWorkItemLinks.ToListAsync());
        Assert.Empty(await db.ActivityEvents.ToListAsync());
    }

    [Fact]
    public async Task QuarantineCandidate_ProjectionCannotBypassHumanReview()
    {
        using var factory = new SqliteTestFactory();
        var feature = await SeedFeature(factory);
        var taskService = new TaskService(factory);
        var task = await taskService.CreateAsync(new TaskCreate(feature, "Candidate"), "human");
        var service = new ExternalWorkItemService(factory);
        await service.QuarantineAsync(new(task.Id, ExternalProvider.GitHub, "R_board", "octo", "board", 42,
            "https://github.com/octo/board/issues/42", "Human review pending"), "human");
        await using var before = await factory.CreateDbContextAsync();
        var eventCount = await before.ActivityEvents.CountAsync();
        var error = await Assert.ThrowsAsync<ExternalWorkItemConflictException>(() => service.ProjectInboundGitHubIssueAsync(Request(feature), "importer"));
        Assert.Equal("quarantined-link-exists", error.ConflictCode);
        await using var after = await factory.CreateDbContextAsync();
        Assert.Equal(1, await after.Tasks.CountAsync());
        Assert.Equal(ExternalLinkState.Quarantined, (await after.ExternalWorkItemLinks.SingleAsync()).State);
        Assert.Equal(eventCount, await after.ActivityEvents.CountAsync());
    }

    [Fact]
    public async Task ExplicitCreationFields_UseSharedDefaultsAndAuditSnapshotRules()
    {
        using var factory = new SqliteTestFactory();
        var feature = await SeedFeature(factory);
        var request = Request(feature) with
        {
            Task = new TaskCreate(feature, "  Imported title  ", Description, WorkStatus.Done,
                TaskPriority.Critical, "  Ada  ", 1.25)
        };
        var result = await new ExternalWorkItemService(factory).ProjectInboundGitHubIssueAsync(request, "importer");
        await using var db = await factory.CreateDbContextAsync();
        var task = await db.Tasks.AsNoTracking().SingleAsync();
        Assert.Equal("Imported title", task.Name);
        Assert.Equal(Description, task.Description);
        Assert.Equal(WorkStatus.Done, task.Status);
        Assert.Equal(TaskPriority.Critical, task.Priority);
        Assert.Equal("Ada", task.Assignee);
        Assert.Equal(1.25, task.SortOrder);
        Assert.NotNull(task.CompletedAt);
        var created = await db.ActivityEvents.SingleAsync(e => e.EventType == "TaskCreated");
        using var snapshot = JsonDocument.Parse(created.AfterJson!);
        Assert.Equal("Done", snapshot.RootElement.GetProperty("status").GetString());
        Assert.Equal("Critical", snapshot.RootElement.GetProperty("priority").GetString());
        Assert.Equal(1.25, snapshot.RootElement.GetProperty("sortOrder").GetDouble());
        Assert.Equal(result.Task.Id, snapshot.RootElement.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task NativeMcp_CreateAndReplay_UseAtomicServiceAndPreserveFullContent()
    {
        using var factory = new SqliteTestFactory();
        var feature = await SeedFeature(factory);
        var service = new ExternalWorkItemService(factory);
        async Task<ExternalWorkItemProjectionResponse> Call() => await KanbanMcpTools.ProjectGitHubIssue(
            service, feature, "Imported issue", "R_board", "octo", "board", "I_issue", 42,
            "https://github.com/octo/board/issues/42", Description);
        var first = await Call();
        var second = await Call();
        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.Task.Id, second.Task.Id);
        Assert.Equal(Description, second.Task.Description);
        await AssertOneProjection(factory, first.Task.Id, actor: "mcp-agent");
    }

    private static ProjectInboundGitHubIssueRequest Request(int feature) => new(
        new TaskCreate(feature, "Imported issue", Description), "R_board", "octo", "board", "I_issue", 42,
        "https://github.com/octo/board/issues/42");
    private static ProjectInboundGitHubIssueDto Dto() => new("Imported issue", "R_board", "octo", "board",
        "I_issue", 42, "https://github.com/octo/board/issues/42", Description, Actor: "importer");

    private static async Task<int> SeedFeature(IDbContextFactory<AppDbContext> factory, int? projectId = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        var project = projectId.HasValue ? await db.Projects.FindAsync(projectId.Value) : new Project { Name = "Projection fixture" };
        var feature = new Feature { Name = "Imported", Project = project! };
        db.Features.Add(feature);
        await db.SaveChangesAsync();
        return feature.Id;
    }

    private static async Task AssertOneProjection(IDbContextFactory<AppDbContext> factory, int id,
        bool checkDescription = true, string actor = "importer")
    {
        await using var db = await factory.CreateDbContextAsync();
        var task = Assert.Single(await db.Tasks.AsNoTracking().ToListAsync());
        Assert.Equal(id, task.Id);
        if (checkDescription)
        {
            Assert.Equal(Description, task.Description);
            Assert.Equal(WorkStatus.Backlog, task.Status);
            Assert.Equal(TaskPriority.Low, task.Priority);
            Assert.Null(task.CompletedAt);
            Assert.Null(task.Assignee);
            Assert.Equal(0, task.SortOrder);
        }
        var link = Assert.Single(await db.ExternalWorkItemLinks.AsNoTracking().ToListAsync());
        Assert.Equal(id, link.TaskId);
        Assert.Equal("I_issue", link.ExternalNodeId);
        Assert.Equal(ExternalLinkState.Active, link.State);
        var comment = Assert.Single(await db.TaskComments.AsNoTracking().ToListAsync());
        Assert.Equal(CommentType.Created, comment.Type);
        Assert.Equal(id, comment.TaskItemId);
        Assert.Equal(actor, comment.Author);
        var events = await db.ActivityEvents.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
        Assert.Equal(2, events.Count);
        var created = Assert.Single(events, e => e.EventType == "TaskCreated");
        var linked = Assert.Single(events, e => e.EventType == "ExternalWorkItemLinked");
        Assert.All(events, e => { Assert.Equal(id, e.EntityId); Assert.Equal(actor, e.Actor); });
        using var snapshot = JsonDocument.Parse(created.AfterJson!);
        Assert.Equal(Description, snapshot.RootElement.GetProperty("description").GetString());
        using var identity = JsonDocument.Parse(linked.AfterJson!);
        Assert.Equal("inbound-projection", identity.RootElement.GetProperty("source").GetString());
        Assert.Equal("I_issue", identity.RootElement.GetProperty("externalNodeId").GetString());
    }

    private sealed class InjectedLinkFailure : Exception;
    private sealed class FailLinkSave : SaveChangesInterceptor
    {
        public bool Enabled;
        public bool TaskHadAssignedId;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,
            InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (Enabled && data.Context!.ChangeTracker.Entries<ExternalWorkItemLink>().Any(e => e.State == EntityState.Added))
            {
                TaskHadAssignedId = data.Context.ChangeTracker.Entries<TaskItem>().Any(e => e.Entity.Id > 0);
                throw new InjectedLinkFailure();
            }
            return ValueTask.FromResult(result);
        }
    }
    private sealed class CountSaves : SaveChangesInterceptor
    {
        public int Count;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,
            InterceptionResult<int> result, CancellationToken ct = default)
        { Count++; return ValueTask.FromResult(result); }
    }
    private sealed class PauseFirstTaskSave : SaveChangesInterceptor
    {
        public bool Enabled;
        public readonly TaskCompletionSource TaskConstructed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,
            InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (Enabled && data.Context!.ChangeTracker.Entries<TaskItem>().Any(e => e.State == EntityState.Added))
            {
                TaskConstructed.TrySetResult();
                await Release.Task.WaitAsync(ct);
            }
            return result;
        }
    }
    private sealed class TransactionAttempts : DbTransactionInterceptor
    {
        public bool Enabled;
        private int attempts;
        public readonly TaskCompletionSource SecondAttempt = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection,
            TransactionStartingEventData data, InterceptionResult<DbTransaction> result, CancellationToken ct = default)
        {
            if (Enabled && Interlocked.Increment(ref attempts) == 2) SecondAttempt.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }
}
