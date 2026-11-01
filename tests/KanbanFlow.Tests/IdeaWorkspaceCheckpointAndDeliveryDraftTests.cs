using System.Reflection;
using System.Text.Json;
using KanbanFlow.Components.Pages;
using KanbanFlow.Components.Workspaces;
using KanbanFlow.Models;
using KanbanFlow.Models.IdeaWorkspaces;
using KanbanFlow.Services.IdeaWorkspaces;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace KanbanFlow.Tests;

public sealed class IdeaWorkspaceCheckpointAndDeliveryDraftTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicationCompletion_PreservesNewerDeliveryNamesAndSelections_AcrossServiceAwaits(bool holdRefresh)
    {
        using var factory = new SqliteTestFactory();
        var service = new IdeaWorkspaceService(factory);
        var created = await service.CreateAsync(new("capture", "test", Title: "Delivery draft race"));
        var key = created.WorkspaceId;
        var project = new Project { Name = "Publication target" };
        await using (var db = factory.CreateDbContext()) { db.Projects.Add(project); await db.SaveChangesAsync(); }
        async Task<IdeaWorkspaceMutationResult> Change(string action, object payload)
            => await service.ExecuteAsync(key, new(Guid.NewGuid().ToString("N"), (await service.GetAsync(key)).Revision,
                action, JsonSerializer.SerializeToElement(payload, Json), "test"));
        await Change("link_project", new { projectId = project.Id });
        await Change("propose_plan", new { title = "Accepted plan", expectedProjectPlanRevision = (await service.GetAsync(key)).ProjectPlanRevision,
            items = new[] { new { id = "first", title = "First real task", description = "Full task scope", acceptanceCriteria = new[] { "Verified" }, dependsOn = Array.Empty<string>() } } });
        var proposed = await service.GetAsync(key);
        await Change("accept_plan", new { planRevisionId = proposed.Document.PlanRevisions.Last().Id, expectedProjectPlanRevision = proposed.ProjectPlanRevision });
        var context = await service.GetAsync(key);
        var initial = new WorkspaceDeliveryDraft { AssociationKnown = true, AssociationProjectId = project.Id,
            SelectedItemIds = context.AcceptedPlan!.Items.Select(item => item.Id).ToList(), FeatureName = "Consumed feature", ProjectName = "Original project draft" };
        var runtime = new DraftRuntime();
        var browser = new WorkspaceBrowserInterop(runtime);
        await browser.InitializeAsync(key);
        var wrapped = DispatchProxy.Create<IIdeaWorkspaceService, HeldService>();
        var held = (HeldService)(object)wrapped;
        held.Inner = service;
        held.HoldRefresh = holdRefresh;
        await using var page = new IdeaWorkspace();
        Set(page, "Workspaces", wrapped); Set(page, "context", context); Set(page, "workspaceId", key);
        Set(page, "generation", 3); Set(page, "restoredGeneration", 3); Set(page, "clientId", "browser");
        Set(page, "sessionId", "tab"); Set(page, "browser", browser);
        Set(page, "draft", new WorkspaceBrowserDraft { Delivery = initial });
        var scope = new WorkspaceUiScope(key, 3);
        var operation = (Task)Invoke(page, "RunCommand", scope, WorkspaceUiCommand.Create("publish_plan", new {
            expectedProjectPlanRevision = context.ProjectPlanRevision, itemIds = initial.SelectedItemIds, featureName = initial.FeatureName }, context.Revision))!;
        try
        {
            await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var newer = initial with { FeatureName = "  New authored feature Ω\r\n" + new string('文', 40_000),
                ProjectName = "  New authored project name 🧭  ", SelectedItemIds = [] };
            await (Task)Invoke(page, "DeliveryChanged", scope, newer)!;
            held.Release.TrySetResult();
            await operation.WaitAsync(TimeSpan.FromSeconds(10));
            var current = Field<WorkspaceBrowserDraft>(page, "draft");
            Assert.Null(current.Pending);
            Assert.Equal(newer.FeatureName, current.Delivery.FeatureName);
            Assert.Equal(newer.ProjectName, current.Delivery.ProjectName);
            Assert.NotNull(current.Delivery.SelectedItemIds);
            Assert.Empty(current.Delivery.SelectedItemIds);
            Assert.Equal(newer.FeatureName, JsonSerializer.Deserialize<WorkspaceBrowserDraft>(runtime.Raw!, Json)!.Delivery.FeatureName);
            var persisted = await service.GetAsync($"project:{project.Id}");
            Assert.Single(persisted.Document.Publications);
            await using var db = factory.CreateDbContext();
            Assert.Equal("Consumed feature", Assert.Single(db.Features.Where(feature => feature.ProjectId == project.Id)).Name);
            Assert.Single(db.Tasks);
        }
        finally { held.Release.TrySetResult(); await operation.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact]
    public void DeliveryAcknowledgement_ClearsOnlyMatchingConsumedFields_AndPreservesLegacyUncertainNames()
    {
        var sent = new WorkspaceDeliveryDraft { AssociationKnown = true, AssociationProjectId = 7, SelectedItemIds = ["item"], FeatureId = 4, FeatureName = "Consumed", ProjectName = "Retain" };
        var command = new IdeaWorkspaceCommand("same-operation", 12, "publish_plan", JsonSerializer.SerializeToElement(new { featureName = "Consumed" }), "human");
        var pending = new WorkspacePendingCommand(command, "delivery", JsonSerializer.SerializeToElement(sent, Json));
        var cleared = (WorkspaceDeliveryDraft)InvokeStatic("CompleteDeliveryDraft", sent, pending, (int?)7)!;
        Assert.Null(cleared.SelectedItemIds); Assert.Null(cleared.FeatureId); Assert.Equal("", cleared.FeatureName);
        Assert.Equal(sent.ProjectName, cleared.ProjectName);
        var legacy = (WorkspaceDeliveryDraft)InvokeStatic("CompleteDeliveryDraft", sent, pending with { DraftSection = null, DraftAtSend = null }, (int?)7)!;
        Assert.Equal(sent, legacy);
        var otherProject = sent with { AssociationProjectId = 9 };
        var replayedOldProject = (WorkspaceDeliveryDraft)InvokeStatic("CompleteDeliveryDraft", otherProject, pending, (int?)9)!;
        Assert.Equal(otherProject, replayedOldProject);
        var linked = (WorkspaceDeliveryDraft)InvokeStatic("CompleteDeliveryDraft", sent, pending with { Command = command with { Action = "link_project" } }, (int?)9)!;
        Assert.Null(linked.SelectedItemIds); Assert.Null(linked.FeatureId); Assert.Equal(sent.FeatureName, linked.FeatureName);
        Assert.Equal(9, linked.AssociationProjectId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoredAndResumedCheckpoints_PreserveExactListEntries_UntilTheCorrespondingFieldChanges(bool resume)
    {
        using var factory = new SqliteTestFactory();
        var service = new IdeaWorkspaceService(factory);
        var created = await service.CreateAsync(new("checkpoint-capture", "test", Title: "Checkpoint preservation"));
        var context = await service.GetAsync(created.WorkspaceId);
        var original = new IdeaWorkspaceCheckpoint { ClientId = "browser", SessionId = "tab", Objective = "Original objective",
            CompletedChanges = ["  first\r\nsecond  ", "", "Ω\nlast"], OpenQuestions = ["", "  question\r\ncontinuation Ω  "], NextActions = [] };
        WorkspaceCheckpointDraft draft;
        await using var page = new IdeaWorkspace();
        Set(page, "context", context with { Document = new IdeaWorkspaceDocument { Checkpoints = [original] } });
        Set(page, "clientId", "browser"); Set(page, "sessionId", "tab");
        draft = ((WorkspaceBrowserDraft)Invoke(page, "DraftFromCheckpoint")!).Checkpoint;
        var panel = new WorkspaceCheckpointPanel();
        Set(panel, "Context", context); Set(panel, "Draft", resume ? new WorkspaceCheckpointDraft() : draft);
        Set(panel, "ClientId", "copy-browser"); Set(panel, "SessionId", "copy-tab");
        WorkspaceUiCommand? request = null;
        Set(panel, "OnCommand", EventCallback.Factory.Create<WorkspaceUiCommand>(this, (WorkspaceUiCommand value) => { request = value; }));
        Invoke(panel, "OnParametersSet");
        if (resume) await (Task)Invoke(panel, "Resume", original)!;
        await (Task)Invoke(panel, "Save")!;
        Assert.Equal(original.CompletedChanges, Strings(request!.Payload, "completedChanges"));
        Assert.Equal(original.OpenQuestions, Strings(request.Payload, "openQuestions"));
        Assert.Empty(Strings(request.Payload, "nextActions"));
        var first = await service.ExecuteAsync(context.WorkspaceId, new("checkpoint-first", context.Revision, request.Action, request.Payload, "browser"));
        var saved = Assert.Single((await service.GetAsync(context.WorkspaceId)).Document.Checkpoints);
        Assert.Equal(original.CompletedChanges, saved.CompletedChanges); Assert.Equal(original.OpenQuestions, saved.OpenQuestions);
        await (Task)Invoke(panel, "CompletedChanged", new ChangeEventArgs { Value = "New first\r\nNew second\n" })!;
        await (Task)Invoke(panel, "Save")!;
        Assert.Equal(["New first", "New second", ""], Strings(request!.Payload, "completedChanges"));
        Assert.Equal(original.OpenQuestions, Strings(request.Payload, "openQuestions"));
        Assert.Empty(Strings(request.Payload, "nextActions"));
        await (Task)Invoke(panel, "QuestionsChanged", new ChangeEventArgs { Value = "Edited question" })!;
        await (Task)Invoke(panel, "NextChanged", new ChangeEventArgs { Value = "one\r\ntwo" })!;
        await (Task)Invoke(panel, "Save")!;
        Assert.Equal(["Edited question"], Strings(request!.Payload, "openQuestions"));
        Assert.Equal(["one", "two"], Strings(request.Payload, "nextActions"));
        Assert.True(first.Revision > context.Revision);
    }

    private static string[] Strings(JsonElement value, string name) => value.GetProperty(name).EnumerateArray().Select(item => item.GetString()!).ToArray();
    private static object? InvokeStatic(string name, params object?[] args) => typeof(IdeaWorkspace).GetMethod(name, Flags)!.Invoke(null, args);
    private static object? Invoke(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Flags)!.Invoke(target, args);
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Flags)!.GetValue(target)!;
    private static void Set(object target, string name, object value)
    {
        var property = target.GetType().GetProperty(name, Flags);
        if (property is not null) property.SetValue(target, value); else target.GetType().GetField(name, Flags)!.SetValue(target, value);
    }

    // Holds only the asynchronous service boundary; real workspace publication and SQLite persistence run underneath.
    public class HeldService : DispatchProxy
    {
        public IIdeaWorkspaceService Inner { get; set; } = default!;
        public bool HoldRefresh { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool published;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            var result = method!.Invoke(Inner, args);
            if (method.Name == nameof(IIdeaWorkspaceService.ExecuteAsync)) return HoldWrite((Task<IdeaWorkspaceMutationResult>)result!);
            if (HoldRefresh && published && method.Name == nameof(IIdeaWorkspaceService.GetAsync)) return HoldRead((Task<IdeaWorkspaceContext>)result!);
            return result;
        }
        private async Task<IdeaWorkspaceMutationResult> HoldWrite(Task<IdeaWorkspaceMutationResult> task)
        {
            var result = await task; published = true;
            if (!HoldRefresh) { Entered.TrySetResult(); await Release.Task; }
            return result;
        }
        private async Task<IdeaWorkspaceContext> HoldRead(Task<IdeaWorkspaceContext> task)
        { var result = await task; Entered.TrySetResult(); await Release.Task; return result; }
    }

    private sealed class DraftRuntime : IJSRuntime, IJSObjectReference
    {
        public string? Raw { get; private set; }
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => InvokeAsync<T>(identifier, default, args);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "import") return ValueTask.FromResult((T)(object)this);
            if (identifier == "initialize") return ValueTask.FromResult((T)(object)JsonSerializer.SerializeToElement(new WorkspaceBrowserSession("browser", "tab", null), Json));
            if (identifier == "saveDraft") Raw = JsonSerializer.Serialize(args![1], Json);
            else if (identifier != "disposeWriter") throw new Xunit.Sdk.XunitException("Unexpected browser method: " + identifier);
            return ValueTask.FromResult(default(T)!);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
