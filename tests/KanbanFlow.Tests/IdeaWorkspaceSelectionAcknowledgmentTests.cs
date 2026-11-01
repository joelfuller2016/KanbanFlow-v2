using System.Reflection;
using System.Text.Json;
using KanbanFlow.Components.Pages;
using KanbanFlow.Components.Workspaces;
using KanbanFlow.Services.IdeaWorkspaces;
using Microsoft.JSInterop;

namespace KanbanFlow.Tests;

public sealed class IdeaWorkspaceSelectionAcknowledgmentTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData("update_component", false)]
    [InlineData("update_component", true)]
    [InlineData("remove_component", false)]
    [InlineData("remove_component", true)]
    public async Task AcknowledgingA_AfterSelectingB_PreservesBSelectionAndCompleteAuthoredDrafts(string action, bool holdContext)
    {
        await using var scenario = await Scenario.Create();
        scenario.Held.HoldContext = holdContext;
        var saving = scenario.Send(action);
        try
        {
            await scenario.Held.Entered.Task.WaitAsync(Deadline);
            await scenario.Select("card-b", "place-b");
            var newer = scenario.Draft;
            scenario.Held.Release.TrySetResult();
            await saving.WaitAsync(Deadline);

            Assert.Equal("card-b", scenario.Draft.SelectedComponentId);
            Assert.Equal("place-b", scenario.Draft.SelectedPlacementId);
            Assert.Equal("card-b", Field<IdeaWorkspaceComponentDetail>(scenario.Page, "selectedDetail").Component.Id);
            Assert.Equal(Serialize(newer.ContentDrafts["card-b"]), Serialize(scenario.Draft.ContentDrafts["card-b"]));
            Assert.Equal(Serialize(newer.Discussion), Serialize(scenario.Draft.Discussion));
            Assert.Equal(Serialize(newer.LayoutDrafts), Serialize(scenario.Draft.LayoutDrafts));
            Assert.Null(scenario.Draft.Pending);
            Assert.Equal(Serialize(scenario.Draft), scenario.Runtime.Raw);
            var saved = await scenario.Service.GetAsync(scenario.Scope.WorkspaceId);
            if (action == "remove_component") Assert.DoesNotContain(saved.Document.Components, component => component.Id == "card-a");
            else Assert.Equal("Saved by A", saved.Document.Components.Single(component => component.Id == "card-a").Title);
            Assert.Equal(scenario.InitialRevision + 1, saved.Revision);
        }
        finally { scenario.Held.Release.TrySetResult(); await saving.WaitAsync(Deadline); }
    }

    [Fact]
    public async Task AcknowledgingA_DoesNotReplaceANewerAppearanceOfTheSameComponent()
    {
        await using var scenario = await Scenario.Create();
        var saving = scenario.Send("update_component");
        try
        {
            await scenario.Held.Entered.Task.WaitAsync(Deadline);
            await scenario.Select("card-a", "place-a-second");
            scenario.Held.Release.TrySetResult(); await saving.WaitAsync(Deadline);
            Assert.Equal("card-a", scenario.Draft.SelectedComponentId);
            Assert.Equal("place-a-second", scenario.Draft.SelectedPlacementId);
        }
        finally { scenario.Held.Release.TrySetResult(); await saving.WaitAsync(Deadline); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovingTheStillSelectedComponent_ClearsSelectionAndRetainsAnyAuthoredBuffer(bool dirty)
    {
        await using var scenario = await Scenario.Create();
        var content = scenario.Draft.ContentDrafts["card-a"] with { Dirty = dirty };
        Set(scenario.Page, "draft", scenario.Draft with { ContentDrafts = new(scenario.Draft.ContentDrafts) { ["card-a"] = content } });
        scenario.Held.Release.TrySetResult();
        await scenario.Send("remove_component").WaitAsync(Deadline);
        Assert.Null(scenario.Draft.SelectedComponentId);
        Assert.Null(scenario.Draft.SelectedPlacementId);
        if (dirty) Assert.Equal(Serialize(content), Serialize(scenario.Draft.ContentDrafts["card-a"]));
        Assert.Null(scenario.Draft.Pending);
    }

    [Fact]
    public async Task RemovingA_AfterAnExplicitAtoBtoASelection_RetainsTheNewerRemovedDraftSelection()
    {
        await using var scenario = await Scenario.Create();
        var saving = scenario.Send("remove_component");
        try
        {
            await scenario.Held.Entered.Task.WaitAsync(Deadline);
            await scenario.Select("card-b", "place-b");
            await scenario.Select("card-a", "place-a");
            var authored = Serialize(scenario.Draft.ContentDrafts["card-a"]);
            scenario.Held.Release.TrySetResult(); await saving.WaitAsync(Deadline);
            Assert.Equal("card-a", scenario.Draft.SelectedComponentId);
            Assert.Equal(authored, Serialize(scenario.Draft.ContentDrafts["card-a"]));
            Assert.DoesNotContain((await scenario.Service.GetAsync(scenario.Scope.WorkspaceId)).Document.Components, component => component.Id == "card-a");
        }
        finally { scenario.Held.Release.TrySetResult(); await saving.WaitAsync(Deadline); }
    }

    [Fact]
    public async Task CreatingA_AfterAnExplicitNewToBtoNewSelection_RetainsTheNewerUntitledDraft()
    {
        await using var scenario = await Scenario.Create();
        await scenario.Select(null, null);
        var saving = Call(scenario.Page, "RunCommand", scenario.Scope, WorkspaceUiCommand.Create("create_component",
            new { component = new { id = "created-a", type = "note", title = "Created A", content = "Committed A" } }));
        try
        {
            await scenario.Held.Entered.Task.WaitAsync(Deadline);
            await scenario.Select("card-b", "place-b");
            // This can also arrive from an Add component click queued before the busy render.
            await Call(scenario.Page, "NewComponent", scenario.Scope);
            var newer = new WorkspaceContentDraft { Dirty = true, Title = "Newer untitled card", Content = "  Unsent Ω\r\nretain complete new card  " };
            await Call(scenario.Page, "ContentChanged", scenario.Scope, newer);
            scenario.Held.Release.TrySetResult(); await saving.WaitAsync(Deadline);
            Assert.Null(scenario.Draft.SelectedComponentId);
            Assert.Null(scenario.Draft.SelectedPlacementId);
            Assert.Equal(Serialize(newer), Serialize(scenario.Draft.ContentDrafts["$new"]));
            Assert.Contains((await scenario.Service.GetAsync(scenario.Scope.WorkspaceId)).Document.Components, component => component.Id == "created-a");
        }
        finally { scenario.Held.Release.TrySetResult(); await saving.WaitAsync(Deadline); }
    }

    [Fact]
    public async Task RetryingAnUncertainAReceipt_AfterSelectingB_UsesTheOriginalSelectionSnapshot()
    {
        await using var scenario = await Scenario.Create();
        scenario.Held.FailReceipt = true;
        scenario.Held.Release.TrySetResult();
        await scenario.Send("update_component");
        var pending = Assert.IsType<WorkspacePendingCommand>(scenario.Draft.Pending);
        Assert.Contains("uncertain", Field<string>(scenario.Page, "error"));
        var revision = (await scenario.Service.GetAsync(scenario.Scope.WorkspaceId)).Revision;
        await scenario.Select("card-b", "place-b");
        var buffer = Serialize(scenario.Draft.ContentDrafts["card-b"]);

        await Call(scenario.Page, "RetryPending", scenario.Scope);

        Assert.Equal("card-b", scenario.Draft.SelectedComponentId);
        Assert.Equal("place-b", scenario.Draft.SelectedPlacementId);
        Assert.Equal(buffer, Serialize(scenario.Draft.ContentDrafts["card-b"]));
        Assert.Null(scenario.Draft.Pending);
        Assert.Equal(revision, (await scenario.Service.GetAsync(scenario.Scope.WorkspaceId)).Revision);
        Assert.Contains("recovered prior result", Field<string>(scenario.Page, "notice"));
        Assert.Equal(pending.Command.OperationId, scenario.Held.OperationId);
    }

    [Theory]
    [InlineData("update_component")]
    [InlineData("remove_component")]
    public async Task LegacyPendingWithoutSelectionMetadata_CannotOverrideTheCurrentSelection(string action)
    {
        await using var scenario = await Scenario.Create();
        await scenario.Select("card-b", "place-b");
        var pending = new WorkspacePendingCommand(scenario.Command(action), null);
        Set(scenario.Page, "draft", scenario.Draft with { Pending = pending });
        scenario.Held.Release.TrySetResult();
        await Call(scenario.Page, "ExecutePending", scenario.Scope, pending);
        Assert.Equal("card-b", scenario.Draft.SelectedComponentId);
        Assert.Equal("place-b", scenario.Draft.SelectedPlacementId);
        Assert.Null(scenario.Draft.Pending);
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
    private static Task Call(object target, string name, params object?[] args) => (Task)target.GetType().GetMethod(name, Flags)!.Invoke(target, args)!;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Flags)!.GetValue(target)!;
    private static void Set(object target, string name, object value)
    { var property = target.GetType().GetProperty(name, Flags); if (property is not null) property.SetValue(target, value); else target.GetType().GetField(name, Flags)!.SetValue(target, value); }

    private sealed class Scenario : IAsyncDisposable
    {
        private readonly SqliteTestFactory database = new();
        public IdeaWorkspaceService Service { get; }
        public IdeaWorkspace Page { get; } = new();
        public HeldReceiptService Held { get; }
        public DraftRuntime Runtime { get; } = new();
        public WorkspaceUiScope Scope { get; private set; }
        public long InitialRevision { get; private set; }
        public WorkspaceBrowserDraft Draft => Field<WorkspaceBrowserDraft>(Page, "draft");
        private Scenario()
        {
            Service = new(database);
            var wrapped = DispatchProxy.Create<IIdeaWorkspaceService, HeldReceiptService>();
            Held = (HeldReceiptService)(object)wrapped; Held.Inner = Service; Set(Page, "Workspaces", wrapped);
        }
        public static async Task<Scenario> Create()
        {
            var scenario = new Scenario();
            var created = await scenario.Service.CreateAsync(new("capture", "test", Title: "Acknowledgment selection"));
            foreach (var id in new[] { "a", "b" })
                await scenario.Service.ExecuteAsync(created.WorkspaceId, new("create-" + id, (await scenario.Service.GetAsync(created.WorkspaceId)).Revision, "create_component",
                    JsonSerializer.SerializeToElement(new { component = new { id = "card-" + id, type = "note", title = "Saved " + id, content = "Saved content " + id }, placement = new { id = "place-" + id, x = 20, y = 30, width = 280, height = 180 } }, Json), "test"));
            await scenario.Service.ExecuteAsync(created.WorkspaceId, new("second-appearance", (await scenario.Service.GetAsync(created.WorkspaceId)).Revision, "add_placement",
                JsonSerializer.SerializeToElement(new { placement = new { id = "place-a-second", componentId = "card-a", x = 350, y = 30, width = 280, height = 180 } }, Json), "test"));
            var context = await scenario.Service.GetAsync(created.WorkspaceId);
            scenario.Scope = new(context.WorkspaceId, 3);
            scenario.InitialRevision = context.Revision;
            var draft = new WorkspaceBrowserDraft
            {
                SelectedComponentId = "card-a", SelectedPlacementId = "place-a",
                ContentDrafts = context.Document.Components.ToDictionary(component => component.Id, component => WorkspaceContentDraft.From(component, context.Revision) with { Dirty = true, Title = "Unsaved " + component.Id, Content = "  Complete Ω\r\n" + new string('文', 12_000) + "\r\nretain last line  " }),
                LayoutDrafts = context.Document.Placements.ToDictionary(placement => placement.Id, placement => WorkspaceLayoutDraft.From(placement, context.Revision) with { Dirty = true, X = 740 }),
                Discussion = new("  Unsent discussion Ω\r\nretain indentation  ")
            };
            var browser = new WorkspaceBrowserInterop(scenario.Runtime); await browser.InitializeAsync(context.WorkspaceId);
            Set(scenario.Page, "context", context); Set(scenario.Page, "workspaceId", context.WorkspaceId); Set(scenario.Page, "generation", 3); Set(scenario.Page, "restoredGeneration", 3);
            Set(scenario.Page, "clientId", "browser"); Set(scenario.Page, "sessionId", "tab"); Set(scenario.Page, "browser", browser); Set(scenario.Page, "draft", draft);
            await scenario.Select("card-a", "place-a");
            return scenario;
        }
        public IdeaWorkspaceCommand Command(string action) => new(Guid.NewGuid().ToString("N"), Field<IdeaWorkspaceContext>(Page, "context").Revision, action,
            JsonSerializer.SerializeToElement(action == "remove_component" ? (object)new { componentId = "card-a" } : new { componentId = "card-a", title = "Saved by A" }, Json), "test");
        public Task Send(string action)
        { var command = Command(action); return Call(Page, "RunCommand", Scope, new WorkspaceUiCommand(action, command.Payload, command.ExpectedRevision, action == "update_component" ? "content:card-a" : null)); }
        public Task Select(string? componentId, string? placementId) => Call(Page, "Select", Scope, new WorkspaceSelection(componentId, placementId));
        public async ValueTask DisposeAsync() { Held.Release.TrySetResult(); await Page.DisposeAsync(); database.Dispose(); }
    }

    // Forward to the real SQLite service, controlling only receipt/context delivery to the page.
    public class HeldReceiptService : DispatchProxy
    {
        public IIdeaWorkspaceService Inner { get; set; } = default!;
        public bool HoldContext { get; set; }
        public bool FailReceipt { get; set; }
        private bool held;
        public string? OperationId { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            var result = method!.Invoke(Inner, args);
            if (method.Name == nameof(IIdeaWorkspaceService.ExecuteAsync))
            {
                OperationId = ((IdeaWorkspaceCommand)args![1]!).OperationId;
                if (!HoldContext && !held) { held = true; return Hold((Task<IdeaWorkspaceMutationResult>)result!); }
            }
            if (HoldContext && !held && method.Name == nameof(IIdeaWorkspaceService.GetAsync)) { held = true; return Hold((Task<IdeaWorkspaceContext>)result!); }
            return result;
        }
        private async Task<T> Hold<T>(Task<T> task)
        {
            var value = await task; Entered.TrySetResult(); await Release.Task;
            if (FailReceipt) { FailReceipt = false; throw new IOException("Acknowledgment transport interrupted."); }
            return value;
        }
    }

    private sealed class DraftRuntime : IJSRuntime, IJSObjectReference
    {
        public string? Raw { get; private set; }
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => InvokeAsync<T>(identifier, default, args);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "import") return ValueTask.FromResult((T)(object)this);
            if (identifier == "initialize") return ValueTask.FromResult((T)(object)JsonSerializer.SerializeToElement(new WorkspaceBrowserSession("browser", "tab", null), Json));
            if (identifier == "saveDraft") Raw = Serialize(args![1]);
            else if (identifier != "disposeWriter") throw new Xunit.Sdk.XunitException("Unexpected browser method: " + identifier);
            return ValueTask.FromResult(default(T)!);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
