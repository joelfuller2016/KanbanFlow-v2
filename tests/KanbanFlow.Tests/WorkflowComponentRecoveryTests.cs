using System.Data.Common;
using System.Reflection;
using KanbanFlow.Components.Workflows;
using KanbanFlow.Models.Workflows;
using Microsoft.AspNetCore.Components;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace KanbanFlow.Tests;

// Direct handler tests use the real service and SQLite provider; they do not claim browser rendering.
public sealed class WorkflowComponentRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadFailure_PreservesLoadedRequestAndDraft_ThenRefreshRecovers(bool select)
    {
        var fault = new ProviderFault();
        using var f = new WorkflowSqliteTestFactory(new QueryFault(fault));
        var request = await f.BriefedAsync();
        using var component = Workspace(f, out var logger);
        var context = (await f.Service.ContextAsync(request.Id))!;
        Set(component, "current", context);
        Set(component, "title", "Unsaved capture title");
        Set(component, "original", "Unsaved original request");
        fault.FailNextRead = true;

        if (select) await Invoke(component, "SelectRequest", new ChangeEventArgs { Value = request.Id.ToString() });
        else await Invoke(component, "Refresh");

        Assert.Same(context, Field(component, "current"));
        Assert.Equal("Unsaved capture title", Field(component, "title"));
        Assert.Equal("Unsaved original request", Field(component, "original"));
        Assert.NotNull(Field(component, "error"));
        Assert.Contains(logger.Errors, error => error is SqliteException);
        await Invoke(component, "Refresh");
        Assert.Null(Field(component, "error"));
        Assert.Equal(request.Id, Assert.IsType<WorkflowContext>(Field(component, "current")).Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UncertainSave_ReplaysOriginalImmutableCommand_WithoutDuplicateWrites(bool afterCommit)
    {
        var fault = new ProviderFault();
        using var f = new WorkflowSqliteTestFactory(new QueryFault(fault), new CommitFault(fault));
        var request = await f.BriefedAsync();
        using var component = Workspace(f, out var logger);
        var before = (await f.Service.ContextAsync(request.Id))!;
        Set(component, "current", before);
        Set(component, "actor", "original author");
        var submission = new WorkflowSubmission(new() { Operation = "add_message", Kind = "clarification", Text = "Original exact text" });
        fault.FailNextRead = !afterCommit;
        fault.FailAfterCommit = afterCommit;

        await Invoke(component, "Submit", submission);

        Assert.False(submission.Succeeded);
        Assert.Same(before, Field(component, "current"));
        Assert.NotNull(Field(component, "pendingCommand"));
        Assert.NotNull(Field(component, "error"));
        Assert.Contains(logger.Errors, error => error is SqliteException);
        var pending = Assert.IsType<WorkflowCommandInput>(Field(component, "pendingCommand"));
        var originalKey = pending.Key;
        Assert.NotEmpty(originalKey);
        Assert.Equal(before.Version, pending.ExpectedVersion);
        var committed = (await f.Service.ContextAsync(request.Id))!;
        Assert.Equal(afterCommit ? 1 : 0, committed.Messages.Count(m => m.Kind == "clarification"));

        Set(component, "actor", "changed author");
        submission.Command.Text = "Mutated after the uncertain outcome";
        submission.Command.ExpectedVersion = 999;
        submission.Command.Key = "replacement-key";
        await Invoke(component, "Submit", new WorkflowSubmission(new() { Operation = "add_message", Kind = "progress", Text = "Must wait for original" }));
        Assert.Equal(originalKey, Assert.IsType<WorkflowCommandInput>(Field(component, "pendingCommand")).Key);
        await Invoke(component, "RetryPending");

        Assert.True(submission.Succeeded);
        Assert.Null(Field(component, "pendingCommand"));
        Assert.Null(Field(component, "error"));
        Assert.NotNull(Field(component, "notice"));
        var after = (await f.Service.ContextAsync(request.Id))!;
        Assert.Equal(before.Version + 1, after.Version);
        var saved = Assert.Single(after.Messages, m => m.Kind == "clarification");
        Assert.Equal("Original exact text", saved.Text);
        Assert.Equal("original author", saved.Author);
        Assert.DoesNotContain(after.Messages, m => m.Kind == "progress");
        await using var db = f.CreateDbContext();
        var receipt = Assert.Single(await db.WorkflowReceipts.Where(r => r.Operation == "message.add").ToArrayAsync());
        Assert.Equal(originalKey, receipt.Key);
        Assert.Equal("original author", receipt.Actor);
        Assert.Single(await db.ActivityEvents.Where(e => e.EventType == "message.add").ToArrayAsync());
    }

    [Fact]
    public async Task ProjectCreation_RecoversAfterCommittedCreate_WithoutDuplicateProjectOrAttachment()
    {
        var fault = new ProviderFault();
        using var f = new WorkflowSqliteTestFactory(new QueryFault(fault), new CommitFault(fault));
        var capture = await f.Service.CaptureAsync(new("Request project", "Original", null), "seed", "capture");
        using var component = Workspace(f, out _);
        Set(component, "current", (await f.Service.ContextAsync(capture.RequestId))!);
        Set(component, "actor", "project author");
        fault.FailAfterCommit = true;

        await Invoke(component, "CreateProject");

        await using (var db = f.CreateDbContext())
        {
            Assert.Single(await db.Projects.ToArrayAsync());
            Assert.Null((await f.Service.ContextAsync(capture.RequestId))!.ProjectId);
        }
        Assert.NotNull(Field(component, "error"));
        Set(component, "actor", "changed author");
        await Invoke(component, "RetryPending");

        var context = (await f.Service.ContextAsync(capture.RequestId))!;
        Assert.NotNull(context.ProjectId);
        // Promotion and the enclosing workflow change each advance the established version.
        Assert.Equal(capture.Version + 2, context.Version);
        await using var verify = f.CreateDbContext();
        Assert.Equal(context.ProjectId, Assert.Single(await verify.Projects.ToArrayAsync()).Id);
        var receipts = await verify.WorkflowReceipts.Where(r => r.Operation == "project.create" || r.Operation == "request.project").ToArrayAsync();
        Assert.Equal(2, receipts.Length);
        Assert.All(receipts, receipt => Assert.Equal("project author", receipt.Actor));
        Assert.Null(Field(component, "pendingCommand"));
    }

    [Fact]
    public async Task ProjectCreation_AttachConflictThenRefreshAndNewAuthor_ReusesTheCreatedProject()
    {
        var fault = new ProviderFault();
        using var f = new WorkflowSqliteTestFactory(new CommitFault(fault));
        var capture = await f.Service.CaptureAsync(new("Original project title", "Original", null), "seed", "capture");
        using var component = Workspace(f, out _);
        Set(component, "current", (await f.Service.ContextAsync(capture.RequestId))!);
        Set(component, "actor", "original creator");
        fault.AfterNextCommit = () => f.Service.AddMessageAsync(capture.RequestId, capture.Version, "progress",
            "Concurrent change after project commit", "other writer", "concurrent-change");

        await Invoke(component, "CreateProject");

        Assert.Contains("changed in another tab", Assert.IsType<string>(Field(component, "error")));
        Assert.Null(Field(component, "pendingCommand"));
        int createdId;
        await using (var db = f.CreateDbContext()) createdId = Assert.Single(await db.Projects.ToArrayAsync()).Id;
        Assert.Null((await f.Service.ContextAsync(capture.RequestId))!.ProjectId);
        var ideaService = new KanbanFlow.Services.IdeaService(f);
        var idea = (await ideaService.GetByIdAsync((await f.Service.ContextAsync(capture.RequestId))!.IdeaId))!;
        await ideaService.UpdateAsync(idea.Id, new KanbanFlow.Services.IdeaUpdate(Title: "Changed title after conflict", ExpectedWorkflowVersion: idea.WorkflowVersion), "other writer");
        await Invoke(component, "Refresh");
        Set(component, "actor", "new attachment author");

        await Invoke(component, "CreateProject");

        Assert.Null(Field(component, "error"));
        var attached = (await f.Service.ContextAsync(capture.RequestId))!;
        Assert.Equal(createdId, attached.ProjectId);
        await using var verify = f.CreateDbContext();
        Assert.Equal(createdId, Assert.Single(await verify.Projects.ToArrayAsync()).Id);
        var createReceipt = Assert.Single(await verify.WorkflowReceipts.Where(receipt => receipt.Operation == "project.create").ToArrayAsync());
        Assert.Equal("original creator", createReceipt.Actor);
        var attachReceipt = Assert.Single(await verify.WorkflowReceipts.Where(receipt => receipt.Operation == "request.project").ToArrayAsync());
        Assert.Equal("new attachment author", attachReceipt.Actor);
    }

    [Fact]
    public async Task Capture_AfterCommitReadFailure_RetriesReceiptAndFinishesOriginalCapture()
    {
        var fault = new ProviderFault();
        using var f = new WorkflowSqliteTestFactory(new QueryFault(fault), new CommitFault(fault));
        using var component = Workspace(f, out _);
        Set(component, "creating", true);
        Set(component, "title", "Captured title");
        Set(component, "original", "Complete original text");
        fault.FailAfterCommit = true;
        await Invoke(component, "Capture");
        Assert.True(Assert.IsType<bool>(Field(component, "creating")));
        Assert.Equal("Complete original text", Field(component, "original"));
        await Invoke(component, "RetryPending");
        Assert.False(Assert.IsType<bool>(Field(component, "creating")));
        Assert.Equal("", Field(component, "original"));
        await using var db = f.CreateDbContext();
        Assert.Single(await db.Ideas.ToArrayAsync());
        var context = Assert.IsType<WorkflowContext>(Field(component, "current"));
        Assert.Equal("Complete original text", Assert.Single(context.Messages).Text);
        Assert.Single(await db.WorkflowReceipts.ToArrayAsync());
    }

    [Fact]
    public async Task DomainRejection_DoesNotKeepAnUncertainOperationOrLoseTheDraft()
    {
        using var f = new WorkflowSqliteTestFactory();
        var request = await f.BriefedAsync();
        using var component = Workspace(f, out _);
        Set(component, "current", (await f.Service.ContextAsync(request.Id))!);
        Set(component, "original", "Preserved draft");
        await Invoke(component, "Submit", new WorkflowSubmission(new() { Operation = "add_message", Kind = "invalid", Text = "Draft message" }));
        Assert.Null(Field(component, "pendingCommand"));
        Assert.Equal("Preserved draft", Field(component, "original"));
        Assert.Contains("supported message type", Assert.IsType<string>(Field(component, "error")));
        await using var db = f.CreateDbContext();
        Assert.DoesNotContain(await db.WorkflowReceipts.ToArrayAsync(), r => r.Operation == "message.add");
    }

    [Fact]
    public async Task ConversationDraft_IsAcknowledgedOnlyAfterThePendingSaveIsConfirmed()
    {
        var fault = new ProviderFault();
        using var f = new WorkflowSqliteTestFactory(new QueryFault(fault), new CommitFault(fault));
        var request = await f.BriefedAsync();
        using var workspace = Workspace(f, out _);
        var context = (await f.Service.ContextAsync(request.Id))!;
        Set(workspace, "current", context);
        var editor = new RequestConversation();
        ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(RequestConversation.Context)] = context,
            [nameof(RequestConversation.OnSubmit)] = EventCallback.Factory.Create<WorkflowSubmission>(new object(),
                submission => Invoke(workspace, "Submit", submission))
        }).SetParameterProperties(editor);
        Set(editor, "text", "Draft retained until confirmation");
        fault.FailAfterCommit = true;
        await Invoke(editor, "Save");
        Assert.Equal("Draft retained until confirmation", Field(editor, "text"));
        await Invoke(workspace, "RetryPending");
        Assert.Equal("", Field(editor, "text"));
        Assert.Equal("Draft retained until confirmation", Assert.Single((await f.Service.ContextAsync(request.Id))!.Messages,
            message => message.Kind == "clarification").Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BriefAndPlanEditors_LateConfirmationAdoptsSavedVersionAndPreservesDraft(bool plan)
    {
        var fault = new ProviderFault();
        using var f = new WorkflowSqliteTestFactory(new QueryFault(fault), new CommitFault(fault));
        var request = await f.PublishedAsync(WorkflowSqliteTestFactory.Draft());
        using var workspace = Workspace(f, out _);
        var before = (await f.Service.ContextAsync(request.Id))!;
        Set(workspace, "current", before);
        object editor = plan ? new RequestPlanEditor() : new RequestBriefEditor();
        ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            ["Context"] = before,
            ["OnSubmit"] = EventCallback.Factory.Create<WorkflowSubmission>(new object(), submission => Invoke(workspace, "Submit", submission))
        }).SetParameterProperties(editor);
        editor.GetType().GetMethod("OnInitialized", Members)!.Invoke(editor, null);
        if (!plan) Set(editor, "outcome", "Keep the authored brief draft");
        fault.FailAfterCommit = true;
        await Invoke(editor, "Save");
        Assert.Equal(before.Version, Field(editor, "version"));
        var pending = Assert.IsType<WorkflowSubmission>(Field(workspace, "pendingSubmission"));
        if (plan) pending.Command.Tasks![0] = pending.Command.Tasks[0] with { Title = "Mutated after commit" };
        else pending.Command.Brief = WorkflowSqliteTestFactory.Brief("Mutated after commit");
        await Invoke(workspace, "RetryPending");
        var after = (await f.Service.ContextAsync(request.Id))!;
        Assert.Equal(before.Version + 1, after.Version);
        Assert.Equal(after.Version, Field(editor, "version"));
        if (plan)
        {
            Assert.Equal(before.Plans.Last().Tasks.Single().Title, after.Plans.Last().Tasks.Single().Title);
            var tokens = Assert.IsType<Dictionary<int, string>>(Field(editor, "taskBaselines"));
            Assert.Equal(after.Tasks.ToDictionary(task => task.TaskId, task => task.EditToken), tokens);
        }
        else
        {
            Assert.Equal("Keep the authored brief draft", Field(editor, "outcome"));
            Assert.Equal("Keep the authored brief draft", after.Briefs.Last().Outcome);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TaskContext_ReadFailureIsVisibleAndRetryable_AndPreservesExistingContext(bool initiallyLoaded)
    {
        var fault = new ProviderFault();
        using var f = new WorkflowSqliteTestFactory(new QueryFault(fault));
        var request = await f.PublishedAsync(WorkflowSqliteTestFactory.Draft());
        using var component = new TaskRequestContext();
        Inject(component, "Workflow", f.Service);
        var logger = new RecordingLogger<TaskRequestContext>();
        InjectIfPresent(component, "Logger", logger);
        ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(TaskRequestContext.TaskId)] = request.WorkItems.Single().TaskItemId }).SetParameterProperties(component);
        if (initiallyLoaded) await Invoke(component, "Load");
        var before = Field(component, "context");
        fault.FailNextRead = true;
        await Invoke(component, "Load");
        Assert.Same(before, Field(component, "context"));
        Assert.NotNull(Field(component, "error"));
        Assert.Contains(logger.Errors, error => error is SqliteException);
        await Invoke(component, "Load");
        Assert.Null(Field(component, "error"));
        Assert.Equal(request.Id, Assert.IsType<WorkflowContext>(Field(component, "context")).Id);
        Assert.NotNull(Field(component, "task"));
    }

    private static RequestWorkspace Workspace(WorkflowSqliteTestFactory f, out RecordingLogger<RequestWorkspace> logger)
    {
        var component = new RequestWorkspace();
        Inject(component, "Workflow", f.Service);
        Inject(component, "Options", new WorkflowOptions());
        logger = new();
        InjectIfPresent(component, "Logger", logger);
        return component;
    }

    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic;
    private static Task Invoke(object component, string method, params object?[] args) =>
        Assert.IsAssignableFrom<Task>(component.GetType().GetMethod(method, Members)!.Invoke(component, args));
    private static object? Field(object component, string field) => component.GetType().GetField(field, Members)?.GetValue(component);
    private static void Set(object component, string field, object? value) => component.GetType().GetField(field, Members)!.SetValue(component, value);
    private static void Inject(object component, string property, object value) => component.GetType().GetProperty(property, Members)!.SetValue(component, value);
    private static void InjectIfPresent(object component, string property, object value) => component.GetType().GetProperty(property, Members)?.SetValue(component, value);

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<Exception> Errors { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception is not null) Errors.Add(exception);
        }
    }

    private sealed class ProviderFault
    {
        public bool FailNextRead;
        public bool FailAfterCommit;
        public Func<Task>? AfterNextCommit;
    }
    private sealed class QueryFault(ProviderFault fault) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (fault.FailNextRead)
            {
                fault.FailNextRead = false;
                command.CommandText = "SELECT * FROM __missing_workflow_provider_test__";
            }
            return ValueTask.FromResult(result);
        }
    }
    private sealed class CommitFault(ProviderFault fault) : DbTransactionInterceptor
    {
        public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (fault.FailAfterCommit) { fault.FailAfterCommit = false; fault.FailNextRead = true; }
            if (fault.AfterNextCommit is { } action) { fault.AfterNextCommit = null; await action(); }
        }
    }
}
