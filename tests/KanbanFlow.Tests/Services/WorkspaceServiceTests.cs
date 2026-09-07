using System.Data.Common;
using KanbanFlow.Models;
using KanbanFlow.Models.Workflows;
using KanbanFlow.Services.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using static KanbanFlow.Tests.WorkflowSqliteTestFactory;

namespace KanbanFlow.Tests.Services;

public sealed partial class WorkspaceServiceTests
{
    [Fact]
    public async Task CaptureAndBriefVersions_PreserveOriginalTextAndProvenanceAfterReload()
    {
        using var f = new WorkflowSqliteTestFactory();
        const string original = " \r\nOriginal intent: café 👋\t\n  keep indentation  \r\n";
        var captured = await f.Service.CaptureAsync(new CaptureRequest("  Original title  ", original, null, "external-assistant"), "requester", "capture");
        var capturedAt = (await f.LoadAsync(captured.RequestId)).Messages.Single().CreatedAt;
        var first = new BriefDraft("  First outcome\n", "scope\t", "local only\r\n", "  Initial checks  ");
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var firstBriefAt = f.Clock.GetUtcNow().UtcDateTime;
        var briefOne = await f.Service.SaveBriefAsync(captured.RequestId, 1, first, "editor-one", "brief-one");
        var second = new BriefDraft("Second outcome", "Expanded scope", "Keep original intact", "Revised checks");
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var briefTwo = await f.Service.SaveBriefAsync(captured.RequestId, briefOne.Version, second, "editor-two", "brief-two");

        var reloaded = await new WorkspaceService(f, f.Clock).GetAsync(captured.RequestId);
        Assert.NotNull(reloaded);
        Assert.Equal("Original title", reloaded.Title);
        Assert.Null(reloaded.ProjectId);
        Assert.Equal(3, reloaded.Version);
        Assert.Equal(3, briefTwo.Version);
        var message = Assert.Single(reloaded.Messages);
        Assert.Equal(original, message.Text);
        Assert.Equal("original", message.Kind);
        Assert.Equal("external-assistant", message.Source);
        Assert.Equal("requester", message.Author);
        Assert.Equal(capturedAt, message.CreatedAt);
        Assert.Collection(reloaded.Briefs.OrderBy(b => b.Number),
            b => { Assert.Equal(1, b.Number); Assert.Equal(first.Outcome, b.Outcome); Assert.Equal(first.Scope, b.Scope); Assert.Equal(first.Constraints, b.Constraints); Assert.Equal(first.Acceptance, b.Acceptance); Assert.Equal("editor-one", b.Author); Assert.Equal(firstBriefAt, b.CreatedAt); },
            b => { Assert.Equal(2, b.Number); Assert.Equal(second.Outcome, b.Outcome); Assert.Equal(second.Scope, b.Scope); Assert.Equal(second.Constraints, b.Constraints); Assert.Equal(second.Acceptance, b.Acceptance); Assert.Equal("editor-two", b.Author); Assert.Equal(firstBriefAt.AddMinutes(1), b.CreatedAt); });
    }

    [Fact]
    public async Task Capture_RetriesAreScopedToActorOperationAndExactPayload()
    {
        using var f = new WorkflowSqliteTestFactory();
        var input = new CaptureRequest("Request", "  Original\n", null);
        var first = await f.Service.CaptureAsync(input, "alice", "same-key");
        var retry = await f.Service.CaptureAsync(input, "alice", "same-key");
        Assert.Equal(first, retry);
        await RejectAsync("conflict", () => f.Service.CaptureAsync(input with { Original = input.Original.Trim() }, "alice", "same-key"));
        var otherActor = await f.Service.CaptureAsync(input, "bob", "same-key");
        var otherOperation = await f.Service.CreateProjectAsync("Project", "alice", "same-key");

        Assert.NotEqual(first.RequestId, otherActor.RequestId);
        await using var db = f.CreateDbContext();
        Assert.Equal(2, await db.WorkRequests.CountAsync());
        Assert.Equal(2, await db.RequestMessages.CountAsync());
        Assert.Equal(3, await db.WorkflowReceipts.CountAsync());
        Assert.Equal(2, await db.ActivityEvents.CountAsync(a => a.EventType == "RequestCaptured"));
        Assert.Equal("Project", (await db.Projects.FindAsync(otherOperation))!.Name);
        Assert.Equal(input.Original, (await db.RequestMessages.SingleAsync(m => m.RequestId == first.RequestId)).Text);
    }

    [Fact]
    public async Task StaleMutation_DoesNotLoseAcceptedWriteOrCreateReceipt()
    {
        using var f = new WorkflowSqliteTestFactory();
        var captured = await f.Service.CaptureAsync(new CaptureRequest("Request", "Original", null), Actor, "capture");
        var accepted = await f.Service.SaveBriefAsync(captured.RequestId, 1, Brief("Accepted edit"), Actor, "accepted");
        await RejectAsync("conflict", () => f.Service.SaveBriefAsync(captured.RequestId, 1, Brief("Stale edit"), Actor, "rejected"));
        var retry = await f.Service.SaveBriefAsync(captured.RequestId, 1, Brief("Accepted edit"), Actor, "accepted");
        Assert.Equal(accepted, retry);
        var request = await f.LoadAsync(captured.RequestId);
        Assert.Equal(2, request.Version);
        Assert.Equal("Accepted edit", Assert.Single(request.Briefs).Outcome);
        await using var db = f.CreateDbContext();
        Assert.False(await db.WorkflowReceipts.AnyAsync(r => r.Key == "rejected"));
        Assert.Single(await db.ActivityEvents.Where(a => a.EventType == "brief.save").ToListAsync());
    }

    [Fact]
    public async Task ConcurrentMutations_KeepTheWinningRevisionAcrossIndependentConnections()
    {
        var gate = new WriterGate();
        using var f = new WorkflowSqliteTestFactory(gate);
        var captured = await f.Service.CaptureAsync(new CaptureRequest("Request", "Original", null), Actor, "capture");
        gate.Enabled = true;
        var first = Task.Run(() => f.Service.SaveBriefAsync(captured.RequestId, 1, Brief("Winning write"), "first", "first"));
        Task<MutationReceipt>? second = null;
        try
        {
            await gate.FirstAcquired.Task.WaitAsync(TimeSpan.FromSeconds(10));
            second = Task.Run(() => new WorkspaceService(f, f.Clock).AddMessageAsync(captured.RequestId, 1, "clarification", "Stale write", "second", "second"));
            await gate.SecondStarting.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotSame(gate.FirstConnection, gate.SecondConnection);
            Assert.False(second.IsCompleted);
        }
        finally { gate.Release.TrySetResult(); }

        Assert.Equal(2, (await first).Version);
        Assert.NotNull(second);
        await RejectAsync("conflict", () => second);
        var reloaded = await f.LoadAsync(captured.RequestId);
        Assert.Equal(2, reloaded.Version);
        Assert.Equal("Winning write", Assert.Single(reloaded.Briefs).Outcome);
        Assert.Equal("Original", Assert.Single(reloaded.Messages).Text);
        await using var db = f.CreateDbContext();
        Assert.Equal(2, await db.WorkflowReceipts.CountAsync());
        Assert.False(await db.WorkflowReceipts.AnyAsync(r => r.Actor == "second"));
        Assert.False(await db.ActivityEvents.AnyAsync(a => a.EventType == "message.add"));
    }

    [Fact]
    public async Task ConcurrentCapture_WithSameActorAndOperationKeyCreatesOneRequest()
    {
        var gate = new WriterGate { Enabled = true };
        using var f = new WorkflowSqliteTestFactory(gate);
        var input = new CaptureRequest("One request", "Original", null);
        var first = Task.Run(() => f.Service.CaptureAsync(input, Actor, "same-capture"));
        Task<MutationReceipt>? second = null;
        try
        {
            await gate.FirstAcquired.Task.WaitAsync(TimeSpan.FromSeconds(10));
            second = Task.Run(() => f.Service.CaptureAsync(input, Actor, "same-capture"));
            await gate.SecondStarting.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotSame(gate.FirstConnection, gate.SecondConnection);
            Assert.False(second.IsCompleted);
        }
        finally { gate.Release.TrySetResult(); }
        var firstReceipt = await first;
        Assert.NotNull(second);
        Assert.Equal(firstReceipt, await second);
        await using var db = f.CreateDbContext();
        Assert.Equal(firstReceipt.RequestId, (await db.WorkRequests.SingleAsync()).Id);
        Assert.Equal("Original", (await db.RequestMessages.SingleAsync()).Text);
        Assert.Equal("request.capture", (await db.WorkflowReceipts.SingleAsync()).Operation);
        Assert.Equal(1, await db.ActivityEvents.CountAsync(a => a.EventType == "RequestCaptured"));
        Assert.Equal(1, await db.ActivityEvents.CountAsync(a => a.EventType == "IdeaCreated"));
        Assert.Equal(1, await db.Ideas.CountAsync());
    }

    private sealed class WriterGate : DbTransactionInterceptor
    {
        public bool Enabled { get; set; }
        public TaskCompletionSource FirstAcquired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondStarting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DbConnection? FirstConnection { get; private set; }
        public DbConnection? SecondConnection { get; private set; }
        private int _starting;

        public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection, TransactionStartingEventData eventData,
            InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            if (Enabled)
            {
                if (Interlocked.Increment(ref _starting) == 1) FirstConnection = connection;
                else { SecondConnection = connection; SecondStarting.TrySetResult(); }
            }
            return ValueTask.FromResult(result);
        }

        public override async ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData eventData,
            DbTransaction result, CancellationToken cancellationToken = default)
        {
            if (Enabled && ReferenceEquals(connection, FirstConnection))
            {
                FirstAcquired.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            return result;
        }
    }

    [Theory]
    [InlineData("clarification")]
    [InlineData("assistant-report")]
    [InlineData("progress")]
    [InlineData("question")]
    [InlineData("decision")]
    public async Task AddMessage_PreservesSupportedReportTextAndAuthor(string kind)
    {
        using var f = new WorkflowSqliteTestFactory();
        var request = await f.BriefedAsync();
        await f.Service.AddMessageAsync(request.Id, request.Version, kind, "  Report\r\n", "reporter", "report");
        var reloaded = await f.LoadAsync(request.Id);
        var added = Assert.Single(reloaded.Messages, m => m.Kind == kind);
        Assert.Equal("  Report\r\n", added.Text);
        Assert.Equal("reporter", added.Author);
        Assert.Equal("reported", added.Source);
        Assert.Equal("Original request", Assert.Single(reloaded.Messages, m => m.Kind == "original").Text);
    }

    [Fact]
    public async Task AddMessage_CannotReplaceTheOriginalWithAnotherOriginal()
    {
        using var f = new WorkflowSqliteTestFactory();
        var request = await f.BriefedAsync();
        await RejectAsync("invalid", () => f.Service.AddMessageAsync(request.Id, request.Version, "original", "Replacement", Actor, "replacement"));
        Assert.Equal("Original request", Assert.Single((await f.LoadAsync(request.Id)).Messages).Text);
    }

    [Fact]
    public async Task ProjectQueries_FilterRequestsAndPreserveInboxUntilAttached()
    {
        using var f = new WorkflowSqliteTestFactory();
        var z = await f.Service.CreateProjectAsync("Zulu", Actor, "z");
        var a = await f.Service.CreateProjectAsync("Alpha", Actor, "a");
        var inbox = await f.Service.CaptureAsync(new CaptureRequest("Inbox", "Original", null), Actor, "inbox");
        var assigned = await f.Service.CaptureAsync(new CaptureRequest("Assigned", "Original", z), Actor, "assigned");
        Assert.Empty(await f.Service.RequestsAsync(a));
        Assert.Equal(assigned.RequestId, Assert.Single(await f.Service.RequestsAsync(z)).Id);
        Assert.Equal(2, (await f.Service.RequestsAsync()).Count);
        await f.Service.AttachProjectAsync(inbox.RequestId, inbox.Version, a, Actor, "attach");
        Assert.Equal(a, (await f.LoadAsync(inbox.RequestId)).ProjectId);
        Assert.Collection(await f.Service.ProjectsAsync(),
            p => { Assert.Equal("Alpha", p.Name); Assert.Equal(1, p.RequestCount); },
            p => { Assert.Equal("Zulu", p.Name); Assert.Equal(1, p.RequestCount); });
        Assert.Null(await f.Service.GetAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Requests_OrdersActiveBeforeArchivedAndReturnsNewestTwoHundred()
    {
        using var f = new WorkflowSqliteTestFactory();
        Assert.Empty(await f.Service.RequestsAsync());
        Assert.Empty(await f.Service.ProjectsAsync());
        var now = f.Clock.GetUtcNow().UtcDateTime;
        Guid archivedId;
        await using (var db = f.CreateDbContext())
        {
            var archived = new WorkRequest { Title = "Archived newest", Idea = new Idea { Title = "Archived newest" }, Archived = true, UpdatedAt = now.AddDays(1), CreatedAt = now };
            archivedId = archived.Id;
            db.WorkRequests.Add(archived);
            db.WorkRequests.AddRange(Enumerable.Range(0, 200).Select(i => new WorkRequest { Title = $"Request {i}", Idea = new Idea { Title = $"Request {i}" }, UpdatedAt = now.AddMinutes(i), CreatedAt = now }));
            await db.SaveChangesAsync();
        }
        var listed = await f.Service.RequestsAsync();
        Assert.Equal(200, listed.Count);
        Assert.Equal("Request 199", listed[0].Title);
        Assert.Equal("Request 0", listed[^1].Title);
        Assert.DoesNotContain(listed, r => r.Id == archivedId);
        Assert.All(listed, r => { Assert.False(r.Archived); Assert.Equal(0, r.TaskCount); Assert.Equal(0, r.DoneCount); });
    }

    [Fact]
    public async Task AttachProject_CannotMovePublishedTasksToAnotherProject()
    {
        using var f = new WorkflowSqliteTestFactory();
        var request = await f.PublishedAsync(Draft());
        var other = await f.Service.CreateProjectAsync("Other", Actor, "other");
        await RejectAsync("invalid", () => f.Service.AttachProjectAsync(request.Id, request.Version, other, Actor, "move"));
        Assert.Equal(request.ProjectId, (await f.LoadAsync(request.Id)).ProjectId);
    }

    [Theory]
    [InlineData("capture-project")]
    [InlineData("attach-project")]
    [InlineData("request")]
    public async Task MissingEntities_RejectWithoutReceipt(string missing)
    {
        using var f = new WorkflowSqliteTestFactory();
        var captured = await f.Service.CaptureAsync(new CaptureRequest("Unassigned", "Original", null), Actor, "capture");
        var request = await f.LoadAsync(captured.RequestId);
        Func<Task> action = missing switch
        {
            "capture-project" => () => f.Service.CaptureAsync(new CaptureRequest("Title", "Original", int.MaxValue), Actor, "missing"),
            "attach-project" => () => f.Service.AttachProjectAsync(request.Id, request.Version, int.MaxValue, Actor, "missing"),
            _ => () => f.Service.SaveBriefAsync(Guid.NewGuid(), 1, Brief(), Actor, "missing")
        };
        if (missing == "request") await RejectAsync("not_found", action);
        else await Assert.ThrowsAsync<KanbanFlow.Services.ProjectNotFoundException>(action);
        await using var db = f.CreateDbContext();
        Assert.False(await db.WorkflowReceipts.AnyAsync(r => r.Key == "missing"));
        Assert.Equal(request.Version, (await f.LoadAsync(request.Id)).Version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \r\n\t")]
    public void Required_RejectsMissingText(string? value) => Assert.Equal("invalid", Assert.Throws<WorkflowException>(() => WorkspaceService.Required(value, "Field", 4)).Code);

    [Fact]
    public void Bounded_PreservesTextAtLimitAndRejectsOverLimitOrNullBytes()
    {
        Assert.Equal(" a\n ", WorkspaceService.Required(" a\n ", "Field", 4));
        Assert.Equal("", WorkspaceService.Bounded("", "Optional", 4));
        foreach (var value in new string?[] { null, "abcde", "a\0b" })
            Assert.Equal("invalid", Assert.Throws<WorkflowException>(() => WorkspaceService.Bounded(value, "Field", 4)).Code);
    }
}
