using System.Reflection;
using System.Text.Json;
using KanbanFlow.Components.Pages;
using KanbanFlow.Components.Workspaces;
using KanbanFlow.Models;
using KanbanFlow.Services.IdeaWorkspaces;
using Microsoft.JSInterop;

namespace KanbanFlow.Tests;

public sealed class IdeaWorkspacePollingTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly string NewClean = "  New saved content Ω\r\n" + new string('保', 12_000) + "\r\ncomplete ending  ";

    [Theory]
    [InlineData("save")]
    [InlineData("refresh")]
    [InlineData("adopt")]
    public async Task PollRead_DelayedSnapshotCannotReplaceNewerContextOrRollbackCompleteDrafts(string advancement)
    {
        await using var scenario = await Scenario.Create();
        await scenario.ChangeSource();
        scenario.Held.HoldNextRead = true;
        var pendingRead = ReadPoll(scenario.Page, scenario.Scope, scenario.Original);
        try
        {
            var captured = await scenario.Held.Entered.Task.WaitAsync(Deadline);
            Assert.Equal(scenario.Original.Revision, captured.Revision);
            Assert.NotEqual(scenario.Original.Source, captured.Source);
            if (advancement == "save") await scenario.SaveCleanOnPage();
            else
            {
                await scenario.ChangeClean(NewClean);
                if (advancement == "refresh") await Call(scenario.Page, "Refresh", scenario.Scope);
                else
                {
                    Set(scenario.Page, "latestContext", await scenario.Service.GetAsync(scenario.Key));
                    await Call(scenario.Page, "AdoptLatest", scenario.Scope);
                }
            }
            var current = Field<IdeaWorkspaceContext>(scenario.Page, "context");
            var completeDraft = Serialize(Field<WorkspaceBrowserDraft>(scenario.Page, "draft"));
            Assert.True(current.Revision > captured.Revision);
            scenario.Held.Release.TrySetResult();

            Assert.Null(await pendingRead.WaitAsync(Deadline));
            await Call(scenario.Page, "AdoptLatest", scenario.Scope);

            Assert.Same(current, Field<IdeaWorkspaceContext>(scenario.Page, "context"));
            Assert.Null(Field<IdeaWorkspaceContext?>(scenario.Page, "latestContext"));
            Assert.Equal(completeDraft, Serialize(Field<WorkspaceBrowserDraft>(scenario.Page, "draft")));
            scenario.AssertDraftsRetained(NewClean, advancement != "save");
        }
        finally { scenario.Held.Release.TrySetResult(); await pendingRead.WaitAsync(Deadline); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refresh_DelayedSnapshotOrFailureCannotUndoNewerRefresh(bool failHeldRead)
    {
        await using var scenario = await Scenario.Create();
        scenario.Held.HoldNextRead = true;
        var firstRefresh = Call(scenario.Page, "Refresh", scenario.Scope);
        try
        {
            await scenario.Held.Entered.Task.WaitAsync(Deadline);
            await scenario.ChangeClean(NewClean);
            await Call(scenario.Page, "Refresh", scenario.Scope);
            var current = Field<IdeaWorkspaceContext>(scenario.Page, "context");
            var completeDraft = Serialize(Field<WorkspaceBrowserDraft>(scenario.Page, "draft"));
            Set(scenario.Page, "error", "Keep the current context's relevant message.");
            if (failHeldRead) scenario.Held.Failure = new IOException("Obsolete read failed.");
            scenario.Held.Release.TrySetResult();

            await firstRefresh.WaitAsync(Deadline);

            Assert.Same(current, Field<IdeaWorkspaceContext>(scenario.Page, "context"));
            Assert.Null(Field<IdeaWorkspaceContext?>(scenario.Page, "latestContext"));
            Assert.Equal(completeDraft, Serialize(Field<WorkspaceBrowserDraft>(scenario.Page, "draft")));
            Assert.Equal("Keep the current context's relevant message.", Field<string>(scenario.Page, "error"));
            scenario.AssertDraftsRetained(NewClean);
        }
        finally { scenario.Held.Release.TrySetResult(); await firstRefresh.WaitAsync(Deadline); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PolledCandidate_QueuedAfterContextReplacementCannotOverwriteNewerCandidate(bool equalContextValues)
    {
        await using var scenario = await Scenario.Create();
        await scenario.ChangeSource();
        var candidate = Assert.IsType<IdeaWorkspaceContext>(await ReadPoll(scenario.Page, scenario.Scope, scenario.Original));
        if (equalContextValues) Set(scenario.Page, "context", scenario.Original with { });
        else
        {
            await scenario.ChangeClean(NewClean);
            await Call(scenario.Page, "Refresh", scenario.Scope);
        }
        var current = Field<IdeaWorkspaceContext>(scenario.Page, "context");
        await scenario.ChangeClean("A later external update remains available.");
        var later = await scenario.Service.GetAsync(scenario.Key);
        Set(scenario.Page, "latestContext", later);
        var completeDraft = Serialize(Field<WorkspaceBrowserDraft>(scenario.Page, "draft"));

        // This is the state transition executed inside the dispatched callback, after it leaves the read path.
        Assert.False((bool)Invoke(scenario.Page, "TryApplyPolledContext", scenario.Scope, scenario.Original, candidate)!);

        Assert.Same(current, Field<IdeaWorkspaceContext>(scenario.Page, "context"));
        Assert.Same(later, Field<IdeaWorkspaceContext>(scenario.Page, "latestContext"));
        Assert.Equal(completeDraft, Serialize(Field<WorkspaceBrowserDraft>(scenario.Page, "draft")));
        scenario.AssertDraftsRetained(equalContextValues ? Scenario.OldClean : NewClean);
    }

    [Fact]
    public async Task PollRead_ContextReplacedWithEqualValuesBeforeCompletionRejectsSnapshot()
    {
        await using var scenario = await Scenario.Create();
        await scenario.ChangeSource();
        scenario.Held.HoldNextRead = true;
        var pendingRead = ReadPoll(scenario.Page, scenario.Scope, scenario.Original);
        try
        {
            await scenario.Held.Entered.Task.WaitAsync(Deadline);
            var replacement = scenario.Original with { };
            Set(scenario.Page, "context", replacement);
            var completeDraft = Serialize(Field<WorkspaceBrowserDraft>(scenario.Page, "draft"));
            scenario.Held.Release.TrySetResult();

            Assert.Null(await pendingRead.WaitAsync(Deadline));

            Assert.Same(replacement, Field<IdeaWorkspaceContext>(scenario.Page, "context"));
            Assert.Null(Field<IdeaWorkspaceContext?>(scenario.Page, "latestContext"));
            Assert.Equal(completeDraft, Serialize(Field<WorkspaceBrowserDraft>(scenario.Page, "draft")));
            scenario.AssertDraftsRetained(Scenario.OldClean);
        }
        finally { scenario.Held.Release.TrySetResult(); await pendingRead.WaitAsync(Deadline); }
    }

    [Theory]
    [InlineData("unchanged")]
    [InlineData("revision")]
    [InlineData("source")]
    [InlineData("project-plan")]
    [InlineData("project-source")]
    [InlineData("project-missing")]
    public async Task PollRead_CurrentContextDetectsExternalChangesAndRetainsAuthoredDrafts(string change)
    {
        await using var scenario = await Scenario.Create();
        if (change == "revision") await scenario.ChangeClean(NewClean);
        else if (change == "source") await scenario.ChangeSource();
        else if (change == "project-plan")
            await scenario.Change($"project:{scenario.ProjectId}", "create_component", new { component = new { id = "project-note", type = "note", title = "External project work", content = "Complete project component" } });
        else if (change is "project-source" or "project-missing")
        {
            await using var db = scenario.Factory.CreateDbContext();
            var project = await db.Projects.FindAsync(scenario.ProjectId);
            if (change == "project-missing") db.Projects.Remove(project!);
            else project!.Description = "  Full external project description Ω\r\n";
            await db.SaveChangesAsync();
        }
        var completeDraft = Serialize(Field<WorkspaceBrowserDraft>(scenario.Page, "draft"));

        var candidate = await ReadPoll(scenario.Page, scenario.Scope, scenario.Original);

        if (change == "unchanged") Assert.Null(candidate);
        else
        {
            Assert.NotNull(candidate);
            if (change == "revision") Assert.True(candidate.Revision > scenario.Original.Revision);
            else
            {
                Assert.Equal(scenario.Original.Revision, candidate.Revision);
                if (change == "source") Assert.NotEqual(scenario.Original.Source, candidate.Source);
                else if (change == "project-plan") Assert.NotEqual(scenario.Original.ProjectPlanRevision, candidate.ProjectPlanRevision);
                else if (change == "project-source") Assert.NotEqual(scenario.Original.ProjectSource, candidate.ProjectSource);
                else { Assert.False(scenario.Original.ProjectMissing); Assert.True(candidate.ProjectMissing); }
            }
            Assert.True((bool)Invoke(scenario.Page, "TryApplyPolledContext", scenario.Scope, scenario.Original, candidate)!);
            Assert.Same(candidate, Field<IdeaWorkspaceContext>(scenario.Page, "latestContext"));
        }
        Assert.Same(scenario.Original, Field<IdeaWorkspaceContext>(scenario.Page, "context"));
        Assert.Equal(completeDraft, Serialize(Field<WorkspaceBrowserDraft>(scenario.Page, "draft")));
        await Call(scenario.Page, "AdoptLatest", scenario.Scope);
        if (candidate is not null) Assert.Same(candidate, Field<IdeaWorkspaceContext>(scenario.Page, "context"));
        scenario.AssertDraftsRetained(change == "revision" ? NewClean : Scenario.OldClean);
    }

    private static string Serialize(WorkspaceBrowserDraft draft) => JsonSerializer.Serialize(draft, Json);
    private static Task<IdeaWorkspaceContext?> ReadPoll(IdeaWorkspace page, WorkspaceUiScope scope, IdeaWorkspaceContext observed)
        => (Task<IdeaWorkspaceContext?>)Invoke(page, "ReadPolledContext", scope, observed)!;
    private static Task Call(object target, string name, params object?[] args) => (Task)Invoke(target, name, args)!;
    private static object? Invoke(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Flags)!.Invoke(target, args);
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Flags)!.GetValue(target)!;
    private static void Set(object target, string name, object? value)
    {
        var property = target.GetType().GetProperty(name, Flags);
        if (property is not null) property.SetValue(target, value); else target.GetType().GetField(name, Flags)!.SetValue(target, value);
    }

    private sealed class Scenario : IAsyncDisposable
    {
        public const string OldClean = "  Original saved card\r\ncomplete old content  ";
        public SqliteTestFactory Factory { get; } = new();
        public IdeaWorkspaceService Service { get; }
        public IdeaWorkspace Page { get; } = new();
        public HeldReadService Held { get; }
        public string Key { get; private set; } = "";
        public int ProjectId { get; private set; }
        public WorkspaceUiScope Scope => new(Key, 3);
        public IdeaWorkspaceContext Original { get; private set; } = default!;
        private WorkspaceBrowserDraft originalDraft = default!;

        private Scenario()
        {
            Service = new(Factory);
            var wrapped = DispatchProxy.Create<IIdeaWorkspaceService, HeldReadService>();
            Held = (HeldReadService)(object)wrapped;
            Held.Inner = Service;
            Set(Page, "Workspaces", wrapped);
        }

        public static async Task<Scenario> Create()
        {
            var scenario = new Scenario();
            var project = new Project { Name = "Linked project", Description = "Original project description" };
            await using (var db = scenario.Factory.CreateDbContext()) { db.Projects.Add(project); await db.SaveChangesAsync(); }
            scenario.ProjectId = project.Id;
            var created = await scenario.Service.CreateAsync(new("capture", "test", Title: "Polling regression", Body: "Original source"));
            scenario.Key = created.WorkspaceId;
            await scenario.Change(scenario.Key, "link_project", new { projectId = project.Id });
            foreach (var id in new[] { "clean", "dirty" })
                await scenario.Change(scenario.Key, "create_component", new { component = new { id, type = "note", title = id, content = OldClean },
                    placement = new { id = id + "-place", x = 20, y = 30, width = 280, height = 180, zIndex = 0, order = 0 } });
            scenario.Original = await scenario.Service.GetAsync(scenario.Key);
            var dirty = WorkspaceContentDraft.From(scenario.Original.Document.Components.Single(item => item.Id == "dirty"), scenario.Original.Revision)
                with { Dirty = true, Title = "  Unsaved title Ω  ", Content = "  Full unsaved text\r\n" + new string('文', 40_000) + "\r\nfinal requirement  ", Url = "https://example.com/full-source" };
            var command = new IdeaWorkspaceCommand("retained-pending-operation", scenario.Original.Revision, "update_component",
                JsonSerializer.SerializeToElement(new { componentId = dirty.ComponentId, title = dirty.Title, content = dirty.Content }, Json), "browser:human");
            scenario.originalDraft = new()
            {
                ContentDrafts = new() { ["clean"] = WorkspaceContentDraft.From(scenario.Original.Document.Components.Single(item => item.Id == "clean"), scenario.Original.Revision),
                    ["dirty"] = dirty, ["$new"] = new() { Dirty = true, Title = "Unsaved new card", Content = "  New card Ω\r\n" + new string('新', 10_000) } },
                LayoutDrafts = new() { ["dirty-place"] = WorkspaceLayoutDraft.From(scenario.Original.Document.Placements.Single(item => item.Id == "dirty-place"), scenario.Original.Revision)
                    with { Dirty = true, X = 845, Y = 672, Width = 740, Height = 610 } },
                Discussion = new("  Full discussion Ω\r\n" + new string('会', 10_000)),
                Connector = new("clean", "dirty", "  Authored relation Ω  "),
                Plan = new() { Title = "Full unsaved plan", Items = [new() { Id = "retained-plan-item", Title = "Task", Description = dirty.Content, AcceptanceCriteria = "  Keep all criteria\r\nΩ  " }] },
                Checkpoint = new() { Objective = dirty.Content, OpenQuestions = "  Full unanswered question Ω  ", NextActions = "  Keep working\r\n" },
                Delivery = new() { AssociationKnown = true, AssociationProjectId = project.Id, FeatureName = dirty.Content, ProjectName = "  Authored project name Ω  " },
                Pending = new(command, "content:dirty", JsonSerializer.SerializeToElement(dirty, Json))
            };
            var browser = new WorkspaceBrowserInterop(new DraftRuntime());
            await browser.InitializeAsync(scenario.Key);
            Set(scenario.Page, "context", scenario.Original); Set(scenario.Page, "workspaceId", scenario.Key);
            Set(scenario.Page, "generation", 3); Set(scenario.Page, "restoredGeneration", 3); Set(scenario.Page, "clientId", "browser");
            Set(scenario.Page, "sessionId", "tab"); Set(scenario.Page, "browser", browser); Set(scenario.Page, "draft", scenario.originalDraft);
            return scenario;
        }

        public async Task Change(string key, string action, object payload)
        {
            await Service.ExecuteAsync(key, new(Guid.NewGuid().ToString("N"), (await Service.GetAsync(key)).Revision,
                action, JsonSerializer.SerializeToElement(payload, Json), "test"));
        }
        public Task ChangeClean(string content) => Change(Key, "update_component", new { componentId = "clean", title = "Latest saved title", content });
        public async Task ChangeSource()
        {
            await using var db = Factory.CreateDbContext();
            var idea = await db.Ideas.FindAsync(Original.SourceId);
            idea!.Body = "  External source change Ω\r\n" + new string('源', 4_000) + "\r\nlast source line  ";
            await db.SaveChangesAsync();
        }
        public async Task SaveCleanOnPage()
        {
            var current = Field<WorkspaceBrowserDraft>(Page, "draft");
            Set(Page, "draft", current with { Pending = null, ContentDrafts = new(current.ContentDrafts) {
                ["clean"] = current.ContentDrafts["clean"] with { Dirty = true, Title = "Latest saved title", Content = NewClean } } });
            await Call(Page, "RunCommand", Scope, WorkspaceUiCommand.Create("update_component", new { componentId = "clean", title = "Latest saved title", content = NewClean },
                Original.Revision, "content:clean"));
            Assert.Null(Field<string?>(Page, "error"));
        }
        public void AssertDraftsRetained(string cleanContent, bool pendingRetained = true)
        {
            var current = Field<WorkspaceBrowserDraft>(Page, "draft");
            Assert.Equal(cleanContent, current.ContentDrafts["clean"].Content);
            Assert.False(current.ContentDrafts["clean"].Dirty);
            Assert.Same(originalDraft.ContentDrafts["dirty"], current.ContentDrafts["dirty"]);
            Assert.Same(originalDraft.ContentDrafts["$new"], current.ContentDrafts["$new"]);
            Assert.Same(originalDraft.LayoutDrafts["dirty-place"], current.LayoutDrafts["dirty-place"]);
            Assert.Same(originalDraft.Discussion, current.Discussion); Assert.Same(originalDraft.Connector, current.Connector);
            Assert.Same(originalDraft.Plan, current.Plan); Assert.Same(originalDraft.Checkpoint, current.Checkpoint);
            Assert.Equal(originalDraft.Delivery, current.Delivery);
            if (pendingRetained)
            {
                Assert.Same(originalDraft.Pending, current.Pending);
                Assert.Same(originalDraft.Pending!.Command, current.Pending!.Command);
                Assert.Equal(originalDraft.Pending.DraftAtSend!.Value.GetRawText(), current.Pending.DraftAtSend!.Value.GetRawText());
            }
            else Assert.Null(current.Pending);
        }
        public async ValueTask DisposeAsync() { await Page.DisposeAsync(); Factory.Dispose(); }
    }

    // Only completion of the first real SQLite snapshot is held; other service calls run normally.
    public class HeldReadService : DispatchProxy
    {
        public IIdeaWorkspaceService Inner { get; set; } = default!;
        public bool HoldNextRead { get; set; }
        public Exception? Failure { get; set; }
        public TaskCompletionSource<IdeaWorkspaceContext> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            var result = method!.Invoke(Inner, args);
            if (method.Name != nameof(IIdeaWorkspaceService.GetAsync) || !HoldNextRead) return result;
            HoldNextRead = false;
            return HoldRead((Task<IdeaWorkspaceContext>)result!);
        }
        private async Task<IdeaWorkspaceContext> HoldRead(Task<IdeaWorkspaceContext> read)
        {
            var snapshot = await read;
            Entered.TrySetResult(snapshot);
            await Release.Task;
            if (Failure is not null) throw Failure;
            return snapshot;
        }
    }

    private sealed class DraftRuntime : IJSRuntime, IJSObjectReference
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => InvokeAsync<T>(identifier, default, args);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "import") return ValueTask.FromResult((T)(object)this);
            if (identifier == "initialize") return ValueTask.FromResult((T)(object)JsonSerializer.SerializeToElement(new WorkspaceBrowserSession("browser", "tab", null), Json));
            if (identifier is not ("saveDraft" or "disposeWriter")) throw new Xunit.Sdk.XunitException("Unexpected browser method: " + identifier);
            return ValueTask.FromResult(default(T)!);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
