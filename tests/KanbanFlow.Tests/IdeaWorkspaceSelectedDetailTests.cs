using System.Reflection;
using System.Text.Json;
using KanbanFlow.Components.Pages;
using KanbanFlow.Components.Workspaces;
using KanbanFlow.Services.IdeaWorkspaces;
using Microsoft.Data.Sqlite;
using Microsoft.JSInterop;

namespace KanbanFlow.Tests;

public sealed class IdeaWorkspaceSelectedDetailTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    [Theory]
    [InlineData("domain")]
    [InlineData("io")]
    [InlineData("sqlite")]
    [InlineData("cancellation")]
    public async Task Select_ActiveDetailFailureIsVisibleAndRetainsCompleteDraftAndPending(string failureKind)
    {
        await using var scenario = await Scenario.Create();
        scenario.Held.FailureKind = failureKind;
        var selecting = scenario.SelectHeldCard();
        try
        {
            await scenario.Held.Entered.Task.WaitAsync(Deadline);
            Assert.Null(Field<IdeaWorkspaceComponentDetail?>(scenario.Page, "selectedDetail"));
            var completeDraft = Serialize(Field<WorkspaceBrowserDraft>(scenario.Page, "draft"));
            scenario.Held.Release.TrySetResult();

            await selecting.WaitAsync(Deadline);

            Assert.Null(Field<IdeaWorkspaceComponentDetail?>(scenario.Page, "selectedDetail"));
            Assert.Contains(failureKind + " detail read failed", Field<string>(scenario.Page, "error"));
            Assert.Equal(completeDraft, Serialize(Field<WorkspaceBrowserDraft>(scenario.Page, "draft")));
            Assert.Equal(completeDraft, scenario.Runtime.Raw);
            scenario.AssertAuthoredDraft(scenario.Memory, Field<WorkspaceBrowserDraft>(scenario.Page, "draft"));
        }
        finally { scenario.Held.Release.TrySetResult(); await selecting.WaitAsync(Deadline); }
    }

    [Theory]
    [InlineData("domain")]
    [InlineData("io")]
    [InlineData("sqlite")]
    public async Task Select_LateFailureCannotOverwriteNewSelectionOrItsError(string failureKind)
    {
        await using var scenario = await Scenario.Create();
        scenario.Held.FailureKind = failureKind;
        var selecting = scenario.SelectHeldCard();
        try
        {
            await scenario.Held.Entered.Task.WaitAsync(Deadline);
            await Call(scenario.Page, "Select", scenario.Scope, new WorkspaceSelection("card-b", "place-b"));
            var currentDetail = Field<IdeaWorkspaceComponentDetail>(scenario.Page, "selectedDetail");
            Assert.Equal("card-b", currentDetail.Component.Id);
            Assert.Equal(CompleteText("saved B"), currentDetail.Component.Content);
            Set(scenario.Page, "error", "Current selection message.");
            var completeDraft = Serialize(Field<WorkspaceBrowserDraft>(scenario.Page, "draft"));
            scenario.Held.Release.TrySetResult();

            await selecting.WaitAsync(Deadline);

            Assert.Same(currentDetail, Field<IdeaWorkspaceComponentDetail>(scenario.Page, "selectedDetail"));
            Assert.Equal("Current selection message.", Field<string>(scenario.Page, "error"));
            Assert.Equal(completeDraft, Serialize(Field<WorkspaceBrowserDraft>(scenario.Page, "draft")));
            Assert.Equal(completeDraft, scenario.Runtime.Raw);
            scenario.AssertAuthoredDraft(scenario.Memory, Field<WorkspaceBrowserDraft>(scenario.Page, "draft"));
        }
        finally { scenario.Held.Release.TrySetResult(); await selecting.WaitAsync(Deadline); }
    }

    [Theory]
    [InlineData("domain")]
    [InlineData("io")]
    [InlineData("sqlite")]
    public async Task Select_LateFailureCannotOverwriteNavigatedWorkspaceOrItsCachedAuthoredDraft(string failureKind)
    {
        await using var scenario = await Scenario.Create();
        scenario.Held.FailureKind = failureKind;
        var selecting = scenario.SelectHeldCard();
        try
        {
            await scenario.Held.Entered.Task.WaitAsync(Deadline);
            var other = await scenario.Service.CreateAsync(new("capture-other", "test", Title: "Another idea"));
            Set(scenario.Page, "Kind", "idea"); Set(scenario.Page, "SourceId", other.IdeaId!.Value);
            await Call(scenario.Page, "OnParametersSetAsync");
            var scope = new WorkspaceUiScope(other.WorkspaceId, 4);
            await Call(scenario.Page, "ContentChanged", scope, new WorkspaceContentDraft { Dirty = true, Title = "  New idea title Ω  ", Content = CompleteText("new workspace buffer") });
            Set(scenario.Page, "error", "Current workspace message.");
            var current = Field<IdeaWorkspaceContext>(scenario.Page, "context");
            var completeDraft = Serialize(Field<WorkspaceBrowserDraft>(scenario.Page, "draft"));
            var saveCalls = scenario.Runtime.SaveCalls;
            scenario.Held.Release.TrySetResult();

            await selecting.WaitAsync(Deadline);

            Assert.Same(current, Field<IdeaWorkspaceContext>(scenario.Page, "context"));
            Assert.Equal(other.WorkspaceId, Field<string>(scenario.Page, "workspaceId"));
            Assert.Null(Field<IdeaWorkspaceComponentDetail?>(scenario.Page, "selectedDetail"));
            Assert.Equal("Current workspace message.", Field<string>(scenario.Page, "error"));
            Assert.Equal(completeDraft, Serialize(Field<WorkspaceBrowserDraft>(scenario.Page, "draft")));
            Assert.Equal(saveCalls, scenario.Runtime.SaveCalls);
            Assert.Equal(Serialize(scenario.Memory), Serialize(Field<Dictionary<string, WorkspaceBrowserDraft>>(scenario.Page, "browserCache")[scenario.Scope.WorkspaceId]));
        }
        finally { scenario.Held.Release.TrySetResult(); await selecting.WaitAsync(Deadline); }
    }

    [Theory]
    [InlineData("cancellation")]
    [InlineData("io")]
    public async Task Select_DisposalWhileReadIsHeldSettlesWithoutNewErrorOrPersistence(string failureKind)
    {
        await using var scenario = await Scenario.Create();
        scenario.Held.FailureKind = failureKind;
        var selecting = scenario.SelectHeldCard();
        try
        {
            await scenario.Held.Entered.Task.WaitAsync(Deadline);
            Set(scenario.Page, "error", "Retain the previous message.");
            var completeDraft = Serialize(Field<WorkspaceBrowserDraft>(scenario.Page, "draft"));
            var saveCalls = scenario.Runtime.SaveCalls;
            await scenario.DisposePage();
            scenario.Held.Release.TrySetResult();

            await selecting.WaitAsync(Deadline);

            Assert.Null(Field<IdeaWorkspaceComponentDetail?>(scenario.Page, "selectedDetail"));
            Assert.Equal("Retain the previous message.", Field<string>(scenario.Page, "error"));
            Assert.Equal(completeDraft, Serialize(Field<WorkspaceBrowserDraft>(scenario.Page, "draft")));
            Assert.Equal(saveCalls, scenario.Runtime.SaveCalls);
            scenario.AssertAuthoredDraft(scenario.Memory, Field<WorkspaceBrowserDraft>(scenario.Page, "draft"));
        }
        finally { scenario.Held.Release.TrySetResult(); await selecting.WaitAsync(Deadline); }
    }

    [Fact]
    public async Task Select_CurrentSuccessfulReadStoresCompleteDetailsWithoutChangingAuthoredDraft()
    {
        await using var scenario = await Scenario.Create();
        scenario.Held.HoldNextRead = false;

        await Call(scenario.Page, "Select", scenario.Scope, new WorkspaceSelection("card-a", "place-a"));

        var detail = Field<IdeaWorkspaceComponentDetail>(scenario.Page, "selectedDetail");
        Assert.Equal("card-a", detail.Component.Id); Assert.Equal(CompleteText("saved A"), detail.Component.Content);
        Assert.Null(Field<string?>(scenario.Page, "error"));
        Assert.Equal(Serialize(Field<WorkspaceBrowserDraft>(scenario.Page, "draft")), scenario.Runtime.Raw);
        scenario.AssertAuthoredDraft(scenario.Memory, Field<WorkspaceBrowserDraft>(scenario.Page, "draft"));
    }

    private static string CompleteText(string label) => "  # " + label + " Ω\r\n\r\n    retain indentation  \r\n" + new string('文', 12_000) + "\r\ncomplete final requirement  ";
    private static string Serialize(WorkspaceBrowserDraft value) => JsonSerializer.Serialize(value, Json);
    private static Task Call(object target, string name, params object?[] args) => (Task)target.GetType().GetMethod(name, Flags)!.Invoke(target, args)!;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Flags)!.GetValue(target)!;
    private static void Set(object target, string name, object value)
    { var property = target.GetType().GetProperty(name, Flags); if (property is not null) property.SetValue(target, value); else target.GetType().GetField(name, Flags)!.SetValue(target, value); }

    private sealed class Scenario : IAsyncDisposable
    {
        private readonly SqliteTestFactory database = new();
        private bool pageDisposed;
        public IdeaWorkspaceService Service { get; }
        public IdeaWorkspace Page { get; } = new();
        public HeldDetailService Held { get; }
        public DraftRuntime Runtime { get; } = new();
        public WorkspaceUiScope Scope { get; private set; }
        public WorkspaceBrowserDraft Memory { get; private set; } = default!;
        private Scenario()
        {
            Service = new(database);
            var wrapped = DispatchProxy.Create<IIdeaWorkspaceService, HeldDetailService>();
            Held = (HeldDetailService)(object)wrapped; Held.Inner = Service;
            Set(Page, "Workspaces", wrapped);
        }
        public static async Task<Scenario> Create()
        {
            var scenario = new Scenario();
            var created = await scenario.Service.CreateAsync(new("capture-details", "test", Title: "Selected detail recovery"));
            foreach (var label in new[] { "a", "b" })
            {
                var revision = (await scenario.Service.GetAsync(created.WorkspaceId)).Revision;
                await scenario.Service.ExecuteAsync(created.WorkspaceId, new("create-" + label, revision, "create_component",
                    JsonSerializer.SerializeToElement(new { component = new { id = "card-" + label, type = "note", title = "Saved " + label, content = CompleteText("saved " + label.ToUpperInvariant()) },
                        placement = new { id = "place-" + label, x = 20, y = 30, width = 280, height = 180, zIndex = 0, order = 0 } }, Json), "test"));
            }
            var context = await scenario.Service.GetAsync(created.WorkspaceId);
            scenario.Scope = new(context.WorkspaceId, 3);
            var contents = context.Document.Components.ToDictionary(component => component.Id, component => WorkspaceContentDraft.From(component, context.Revision)
                with { Dirty = true, Title = "  Unsaved " + component.Id + " Ω  ", Content = CompleteText("unsaved " + component.Id) });
            contents["$new"] = new() { Dirty = true, Title = "  Unsaved new card Ω  ", Content = CompleteText("new card") };
            var pending = new WorkspacePendingCommand(new("same-pending-operation", context.Revision, "update_component",
                JsonSerializer.SerializeToElement(new { componentId = "card-a", content = contents["card-a"].Content }, Json), "human"), "content:card-a",
                JsonSerializer.SerializeToElement(contents["card-a"], Json));
            scenario.Memory = new()
            {
                SelectedComponentId = "card-b", SelectedPlacementId = "place-b", ContentDrafts = contents,
                LayoutDrafts = context.Document.Placements.ToDictionary(placement => placement.Id, placement => WorkspaceLayoutDraft.From(placement, context.Revision) with { Dirty = true, X = 740, Y = 680 }),
                Discussion = new(CompleteText("unsent discussion")), Connector = new("card-a", "card-b", "  Full relation Ω  "),
                Plan = new() { Title = CompleteText("unpublished plan"), BaseRevision = context.Revision, Items = [new() { Id = "item", Title = "Task", Description = CompleteText("task specification"), AcceptanceCriteria = "  Full criterion Ω\r\n" }] },
                Checkpoint = new() { Objective = CompleteText("checkpoint objective"), OpenQuestions = CompleteText("questions"), NextActions = "  Continue working\r\n" },
                Delivery = new() { AssociationKnown = true, FeatureName = CompleteText("proposed feature"), ProjectName = "  Proposed project Ω  " },
                Pending = pending
            };
            var browser = new WorkspaceBrowserInterop(scenario.Runtime);
            await browser.InitializeAsync(context.WorkspaceId);
            Set(scenario.Page, "context", context); Set(scenario.Page, "workspaceId", context.WorkspaceId); Set(scenario.Page, "generation", 3); Set(scenario.Page, "restoredGeneration", 3);
            Set(scenario.Page, "clientId", "browser"); Set(scenario.Page, "sessionId", "tab"); Set(scenario.Page, "browser", browser); Set(scenario.Page, "draft", scenario.Memory);
            Set(scenario.Page, "selectedDetail", await scenario.Service.GetComponentAsync(context.WorkspaceId, "card-b"));
            Assert.True(await (Task<bool>)scenario.Page.GetType().GetMethod("PersistDraft", Flags)!.Invoke(scenario.Page, [scenario.Scope])!);
            return scenario;
        }
        public Task SelectHeldCard() => Call(Page, "Select", Scope, new WorkspaceSelection("card-a", "place-a"));
        public void AssertAuthoredDraft(WorkspaceBrowserDraft expected, WorkspaceBrowserDraft actual)
        {
            Assert.Equal(Serialize(expected with { SelectedComponentId = actual.SelectedComponentId, SelectedPlacementId = actual.SelectedPlacementId }), Serialize(actual));
            Assert.Same(expected.Pending, actual.Pending);
            Assert.Same(expected.Pending!.Command, actual.Pending!.Command);
            Assert.Equal(expected.Pending.DraftAtSend!.Value.GetRawText(), actual.Pending.DraftAtSend!.Value.GetRawText());
        }
        public async Task DisposePage()
        {
            if (pageDisposed) return;
            pageDisposed = true;
            await Page.DisposeAsync();
        }
        public async ValueTask DisposeAsync() { await DisposePage(); database.Dispose(); }
    }

    // Hold a real SQLite component result at the service boundary, then deliver the chosen failure.
    public class HeldDetailService : DispatchProxy
    {
        public IIdeaWorkspaceService Inner { get; set; } = default!;
        public bool HoldNextRead { get; set; } = true;
        public string? FailureKind { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            var result = method!.Invoke(Inner, args);
            if (method.Name != nameof(IIdeaWorkspaceService.GetComponentAsync) || !HoldNextRead || args![1] is not "card-a") return result;
            HoldNextRead = false;
            return HoldRead((Task<IdeaWorkspaceComponentDetail>)result!, (CancellationToken)args[2]!);
        }
        private async Task<IdeaWorkspaceComponentDetail> HoldRead(Task<IdeaWorkspaceComponentDetail> read, CancellationToken cancellationToken)
        {
            var detail = await read;
            Entered.TrySetResult();
            await Release.Task;
            if (FailureKind is { } kind)
                throw kind switch
                {
                    "domain" => new IdeaWorkspaceException(404, "component_not_found", "domain detail read failed"),
                    "io" => new IOException("io detail read failed"),
                    "sqlite" => new SqliteException("sqlite detail read failed", 5),
                    "cancellation" => new OperationCanceledException("cancellation detail read failed", cancellationToken),
                    _ => new Xunit.Sdk.XunitException("Unexpected controlled failure: " + kind)
                };
            return detail;
        }
    }

    private sealed class DraftRuntime : IJSRuntime, IJSObjectReference
    {
        public string? Raw { get; private set; }
        public int SaveCalls { get; private set; }
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => InvokeAsync<T>(identifier, default, args);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "import") return ValueTask.FromResult((T)(object)this);
            if (identifier == "initialize") return ValueTask.FromResult((T)(object)JsonSerializer.SerializeToElement(new WorkspaceBrowserSession("browser", "tab", null), Json));
            if (identifier == "saveDraft") { SaveCalls++; Raw = JsonSerializer.Serialize(args![1], Json); }
            else if (identifier != "disposeWriter") throw new Xunit.Sdk.XunitException("Unexpected browser method: " + identifier);
            return ValueTask.FromResult(default(T)!);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
