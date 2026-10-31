using System.Reflection;
using System.Text.Json;
using KanbanFlow.Components.Pages;
using KanbanFlow.Components.Workspaces;
using KanbanFlow.Models.IdeaWorkspaces;
using KanbanFlow.Services.IdeaWorkspaces;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace KanbanFlow.Tests;

public sealed class IdeaWorkspaceRestoreAndRelinkTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly WorkspaceUiScope Scope = new("idea:11", 3);

    [Fact]
    public async Task RetryAfterFailedInitialization_WithNewEditsAndValidStoredPending_RequiresExplicitPreservation()
    {
        var original = StoredDraft();
        var originalRaw = JsonSerializer.Serialize(original, Json);
        var runtime = new DraftStorageRuntime(originalRaw);
        runtime.Module.InitializeFailure = new JSException("Temporary browser identity failure");
        var failedBrowser = new WorkspaceBrowserInterop(runtime);
        await Assert.ThrowsAsync<JSException>(() => failedBrowser.InitializeAsync(Scope.WorkspaceId).AsTask());
        await using var page = Page(Context(1));
        Set(page, "browser", failedBrowser);
        Set(page, "storageHealthy", false);
        var entered = new WorkspaceContentDraft { Title = "New typing during failed initialization Ω", Content = CompleteText("new memory"), Dirty = true };
        await (Task)Invoke(page, "ContentChanged", Scope, entered)!;

        await (Task)Invoke(page, "RetryDraftStorage", Scope)!;
        runtime.Module.InitializeFailure = null;
        var retry = new WorkspaceBrowserInterop(runtime);
        Set(page, "browser", retry);
        var stored = await retry.InitializeAsync(Scope.WorkspaceId);
        var conflict = Assert.Throws<TargetInvocationException>(() => Invoke(page, "RestoreDraft", Scope, stored));

        Assert.IsType<JsonException>(conflict.InnerException);
        Assert.Equal("", Field<string>(page, "clientId"));
        Assert.False(Property<bool>(page, "Ready"));
        Assert.Equal(originalRaw, runtime.Module.Raw);
        Assert.Equal(0, runtime.Module.SaveCalls);
        Assert.Equal(0, runtime.Module.PreserveCalls);
        Assert.Same(entered, Field<WorkspaceBrowserDraft>(page, "draft").ContentDrafts["$new"]);
        Assert.DoesNotContain(Scope.WorkspaceId, Field<HashSet<string>>(page, "restoredBrowserWorkspaces"));

        await (Task)Invoke(page, "RecoverStoredDraft", Scope)!;
        Assert.Equal(originalRaw, runtime.Module.BackupRaw);
        Assert.Null(runtime.Module.Raw);
        Assert.Equal(1, runtime.Module.PreserveCalls);
        Assert.Contains(Scope.WorkspaceId, Field<HashSet<string>>(page, "restoredBrowserWorkspaces"));
        var backup = JsonSerializer.Deserialize<WorkspaceBrowserDraft>(runtime.Module.BackupRaw!, Json)!;
        Assert.Equal(original.ContentDrafts["original-card"].Content, backup.ContentDrafts["original-card"].Content);
        Assert.Equal(JsonSerializer.Serialize(original.Pending, Json), JsonSerializer.Serialize(backup.Pending, Json));

        var fresh = new WorkspaceBrowserInterop(runtime);
        Set(page, "browser", fresh);
        Invoke(page, "RestoreDraft", Scope, await fresh.InitializeAsync(Scope.WorkspaceId));
        Set(page, "restoredGeneration", Scope.Generation);
        Assert.True(await (Task<bool>)Invoke(page, "PersistDraft", Scope)!);
        var active = JsonSerializer.Deserialize<WorkspaceBrowserDraft>(runtime.Module.Raw!, Json)!;
        Assert.Equal(entered.Title, active.ContentDrafts["$new"].Title);
        Assert.Equal(entered.Content, active.ContentDrafts["$new"].Content);
        Assert.Equal(originalRaw, runtime.Module.BackupRaw);
    }

    [Fact]
    public async Task FailedExplicitPreservation_DoesNotTrustTheNewCacheOrChangeEitherDraft()
    {
        var raw = JsonSerializer.Serialize(StoredDraft(), Json);
        var runtime = new DraftStorageRuntime(raw);
        runtime.Module.PreserveFailure = new JSException("Backup storage unavailable");
        await using var page = Page(Context(1));
        Set(page, "browser", new WorkspaceBrowserInterop(runtime));
        var entered = new WorkspaceContentDraft { Content = CompleteText("retained new memory"), Dirty = true };
        await (Task)Invoke(page, "ContentChanged", Scope, entered)!;

        await (Task)Invoke(page, "RecoverStoredDraft", Scope)!;

        Assert.Equal(raw, runtime.Module.Raw);
        Assert.Null(runtime.Module.BackupRaw);
        Assert.Equal(0, runtime.Module.SaveCalls);
        Assert.DoesNotContain(Scope.WorkspaceId, Field<HashSet<string>>(page, "restoredBrowserWorkspaces"));
        Assert.Same(entered, Field<WorkspaceBrowserDraft>(page, "draft").ContentDrafts["$new"]);
        Assert.Contains("Backup storage unavailable", Field<string>(page, "storageError"));
    }

    [Fact]
    public async Task PreviouslyRestoredCache_AndLaterStorageRetry_KeepTheCompleteDraftAndPendingOperation()
    {
        var raw = JsonSerializer.Serialize(StoredDraft(), Json);
        var runtime = new DraftStorageRuntime(raw);
        await using var page = Page(Context(1));
        var browser = new WorkspaceBrowserInterop(runtime);
        Set(page, "browser", browser);
        var stored = await browser.InitializeAsync(Scope.WorkspaceId);
        Invoke(page, "RestoreDraft", Scope, stored);
        Set(page, "restoredGeneration", Scope.Generation);
        runtime.Module.SaveFailure = new JSException("Draft quota failure");
        var entered = new WorkspaceContentDraft { Content = CompleteText("later valid memory"), Dirty = true };
        await (Task)Invoke(page, "ContentChanged", Scope, entered)!;
        Assert.Equal(raw, runtime.Module.Raw);
        Assert.False(Field<bool>(page, "storageHealthy"));

        // Returning to an already restored workspace may safely prefer its newer in-memory cache.
        Invoke(page, "RestoreDraft", Scope, stored);
        Assert.Same(entered, Field<WorkspaceBrowserDraft>(page, "draft").ContentDrafts["$new"]);
        runtime.Module.SaveFailure = null;
        await (Task)Invoke(page, "RetryDraftStorage", Scope)!;

        var saved = JsonSerializer.Deserialize<WorkspaceBrowserDraft>(runtime.Module.Raw!, Json)!;
        Assert.Equal(entered.Content, saved.ContentDrafts["$new"].Content);
        Assert.Equal(stored.Draft!.ContentDrafts["original-card"].Content, saved.ContentDrafts["original-card"].Content);
        Assert.Equal(JsonSerializer.Serialize(stored.Draft.Pending, Json), JsonSerializer.Serialize(saved.Pending, Json));
        Assert.True(Field<bool>(page, "storageHealthy"));
        Assert.Equal(0, runtime.Module.PreserveCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoringAfterExternalRelink_ReconcilesStoredOrCachedDeliveryChoices_WithoutDiscardingAuthoredText(bool fromCache)
    {
        await using var page = Page(Context(2));
        var previous = DeliveryDraft(1);
        if (fromCache)
        {
            Field<Dictionary<string, WorkspaceBrowserDraft>>(page, "browserCache")[Scope.WorkspaceId] = previous;
            Field<HashSet<string>>(page, "restoredBrowserWorkspaces").Add(Scope.WorkspaceId);
        }

        Invoke(page, "RestoreDraft", Scope, new WorkspaceBrowserSession("browser", "tab", fromCache ? new() : previous));

        AssertReconciled(previous, Field<WorkspaceBrowserDraft>(page, "draft"));
        Assert.Contains("review", Field<string>(page, "deliveryError"), StringComparison.OrdinalIgnoreCase);
        await AssertCurrentProjectSelectionBuildsPreviewRequest(page);
    }

    [Fact]
    public async Task AdoptingAnExternalRelink_ClearsOldPreviewAndTaskView_WhileKeepingEveryOtherDraft()
    {
        await using var page = Page(Context(1));
        var previous = DeliveryDraft(1);
        Set(page, "draft", previous);
        Set(page, "latestContext", Context(2));
        Set(page, "publicationPreview", new IdeaWorkspacePublicationPreview(Scope.WorkspaceId, 12, 1, 7, "old-plan", "Old plan", 11, "Old feature", [], [], 0, 0));
        Set(page, "publicationRequest", new IdeaWorkspacePublicationPreviewRequest(12, 7, ["project-1-item"], 11));
        Set(page, "inspectedTaskId", 100);

        await (Task)Invoke(page, "AdoptLatest", Scope)!;

        AssertReconciled(previous, Field<WorkspaceBrowserDraft>(page, "draft"));
        Assert.Null(Field<object?>(page, "publicationPreview"));
        Assert.Null(Field<object?>(page, "publicationRequest"));
        Assert.Null(Field<object?>(page, "inspectedTaskId"));
        await AssertCurrentProjectSelectionBuildsPreviewRequest(page);
    }

    [Fact]
    public async Task LegacyDeliveryChoicesWithoutAnAssociationMarker_RequireReselectionAndRetainAuthoredNames()
    {
        await using var page = Page(Context(2));
        var previous = DeliveryDraft(1) with { Delivery = DeliveryDraft(1).Delivery with { AssociationKnown = false, AssociationProjectId = null } };

        Invoke(page, "RestoreDraft", Scope, new WorkspaceBrowserSession("browser", "tab", previous));

        AssertReconciled(previous, Field<WorkspaceBrowserDraft>(page, "draft"));
    }

    [Fact]
    public async Task RestoringTheSameKnownProject_KeepsItsExplicitDeliveryChoices()
    {
        await using var page = Page(Context(1));
        var previous = DeliveryDraft(1);

        Invoke(page, "RestoreDraft", Scope, new WorkspaceBrowserSession("browser", "tab", previous));

        Assert.Equal(previous.Delivery, Field<WorkspaceBrowserDraft>(page, "draft").Delivery);
        Assert.Null(Field<string?>(page, "deliveryError"));
        Assert.Same(previous.Pending, Field<WorkspaceBrowserDraft>(page, "draft").Pending);
    }

    [Fact]
    public async Task AnOlderProjectFieldCallback_CannotPutOldSelectionsBackAfterReconciliation()
    {
        await using var page = Page(Context(2));
        var current = DeliveryDraft(2);
        Set(page, "draft", current);

        await (Task)Invoke(page, "DeliveryChanged", Scope, DeliveryDraft(1).Delivery)!;

        Assert.Same(current, Field<WorkspaceBrowserDraft>(page, "draft"));
        Assert.Contains("association changed", Field<string>(page, "deliveryError"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldRenderedDeliveryEvents_AfterReceivingAnotherProject_CannotChangeTheCurrentChildDraft(bool featureEvent)
    {
        var (panel, current, changes) = DeliveryPanelAfterRelink();

        if (featureEvent) await (Task)Invoke(panel, "FeatureChanged", new ChangeEventArgs { Value = "11" })!;
        else await (Task)Invoke(panel, "SelectionChanged", "project-1-item", true)!;

        Assert.Empty(changes);
        Assert.Same(current, Property<WorkspaceDeliveryDraft>(panel, "editing"));
        Assert.Equal(current.FeatureName, Property<WorkspaceDeliveryDraft>(panel, "editing").FeatureName);
        Assert.Equal(current.ProjectName, Property<WorkspaceDeliveryDraft>(panel, "editing").ProjectName);
        Assert.Equal(2, Property<WorkspaceDeliveryDraft>(panel, "editing").AssociationProjectId);
    }

    [Fact]
    public async Task CurrentProjectDeliveryEvents_StillSelectItsItemFeatureAndDefaultFeature()
    {
        var (panel, current, changes) = DeliveryPanelAfterRelink();

        await (Task)Invoke(panel, "SelectionChanged", "project-2-item", true)!;
        Assert.Equal(["project-2-item"], Assert.Single(changes).SelectedItemIds);
        Assert.Equal(current.FeatureName, changes[0].FeatureName);
        await (Task)Invoke(panel, "FeatureChanged", new ChangeEventArgs { Value = "21" })!;
        Assert.Equal(2, changes.Count);
        Assert.Equal(21, changes[1].FeatureId);
        Assert.Equal("", changes[1].FeatureName);
        Assert.Equal(["project-2-item"], changes[1].SelectedItemIds);
        await (Task)Invoke(panel, "FeatureChanged", new ChangeEventArgs { Value = "" })!;
        Assert.Equal(3, changes.Count);
        Assert.Null(changes[2].FeatureId);
        Assert.Equal(["project-2-item"], changes[2].SelectedItemIds);
    }

    [Fact]
    public async Task AnOldRenderedDefaultFeatureEvent_CannotClearTheCurrentProjectsAuthoredNames()
    {
        var (panel, current, changes) = DeliveryPanelAfterRelink();

        await (Task)Invoke(panel, "FeatureChangedFromProject", 1, new ChangeEventArgs { Value = "" })!;
        await (Task)Invoke(panel, "SelectionChangedFromProject", 1, "project-2-item", true)!;

        Assert.Empty(changes);
        Assert.Same(current, Property<WorkspaceDeliveryDraft>(panel, "editing"));
        Assert.Equal(current.FeatureName, Property<WorkspaceDeliveryDraft>(panel, "editing").FeatureName);
    }

    [Fact]
    public async Task OldProjectPreview_CannotExposeItsFeaturesAfterReceivingAnotherProject()
    {
        var (panel, current, changes) = DeliveryPanelAfterRelink();
        Parameter(panel, "Preview", PreviewFor(1));
        Invoke(panel, "OnParametersSet");

        await (Task)Invoke(panel, "FeatureChanged", new ChangeEventArgs { Value = "11" })!;

        Assert.Empty(changes);
        Assert.Same(current, Property<WorkspaceDeliveryDraft>(panel, "editing"));
        Assert.Equal([21], Field<IReadOnlyList<IdeaWorkspaceFeatureOption>>(panel, "features").Select(feature => feature.Id));
    }

    [Fact]
    public async Task OldItemEvent_WhenTheCurrentProjectHasNoAcceptedPlan_IsIgnored()
    {
        var (panel, current, changes) = DeliveryPanelAfterRelink();
        Parameter(panel, "Context", Context(2) with { AcceptedPlan = null });
        Invoke(panel, "OnParametersSet");

        await (Task)Invoke(panel, "SelectionChanged", "project-1-item", true)!;

        Assert.Empty(changes);
        Assert.Same(current, Property<WorkspaceDeliveryDraft>(panel, "editing"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task AnInvalidStoredDeliveryAssociation_IsRejectedWithoutChangingTheStoredDraft(int projectId)
    {
        var raw = JsonSerializer.Serialize(new WorkspaceBrowserDraft { Delivery = new() { AssociationKnown = true, AssociationProjectId = projectId } }, Json);
        var runtime = new DraftStorageRuntime(raw);
        await using var browser = new WorkspaceBrowserInterop(runtime);

        await Assert.ThrowsAsync<JsonException>(() => browser.InitializeAsync(Scope.WorkspaceId).AsTask());

        Assert.Equal(raw, runtime.Module.Raw);
        Assert.Equal(0, runtime.Module.SaveCalls);
        Assert.Equal(0, runtime.Module.PreserveCalls);
    }

    [Fact]
    public async Task RecoveringARemovedCard_WithAnotherUnsavedNewCard_PreservesBothCompleteDraftsAndPendingOperation()
    {
        await using var page = Page(Context(2));
        var removed = new WorkspaceContentDraft { Key = "removed-card", ComponentId = "removed-card", Title = "Removed unsaved card Ω", Content = CompleteText("removed-card text"), Dirty = true, BaseRevision = 8 };
        var newCard = new WorkspaceContentDraft { Title = "Existing new card 漢字", Content = CompleteText("new-card text"), Dirty = true, BaseRevision = 8 };
        var pending = StoredDraft().Pending;
        var original = new WorkspaceBrowserDraft { SelectedComponentId = removed.ComponentId, ContentDrafts = new() { [removed.Key] = removed, ["$new"] = newCard }, Pending = pending };
        Set(page, "draft", original);

        await (Task)Invoke(page, "ReapplyContent", Scope, removed.Key)!;

        var retained = Field<WorkspaceBrowserDraft>(page, "draft");
        Assert.Same(original, retained);
        Assert.Same(removed, retained.ContentDrafts[removed.Key]);
        Assert.Same(newCard, retained.ContentDrafts["$new"]);
        Assert.Same(pending, retained.Pending);
        Assert.Equal(removed.ComponentId, retained.SelectedComponentId);
        Assert.Contains("Add component", Field<string>(page, "error"));
    }

    [Fact]
    public async Task RecoveringARemovedCard_WithACleanNewCardSlot_PreservesTheWholeRemovedDraftAsANewCard()
    {
        await using var page = Page(Context(2));
        var removed = new WorkspaceContentDraft { Key = "removed-card", ComponentId = "removed-card", Title = "  Removed unsaved card Ω  ", Content = CompleteText("complete removed-card text"), Dirty = true, BaseRevision = 8 };
        var pending = StoredDraft().Pending;
        Set(page, "draft", new WorkspaceBrowserDraft { SelectedComponentId = removed.ComponentId,
            ContentDrafts = new() { [removed.Key] = removed, ["$new"] = new() }, Pending = pending });

        await (Task)Invoke(page, "ReapplyContent", Scope, removed.Key)!;

        var retained = Field<WorkspaceBrowserDraft>(page, "draft");
        Assert.DoesNotContain(removed.Key, retained.ContentDrafts.Keys);
        Assert.Null(retained.SelectedComponentId);
        var recovered = retained.ContentDrafts["$new"];
        Assert.Equal("$new", recovered.Key);
        Assert.Null(recovered.ComponentId);
        Assert.Equal(removed.Title, recovered.Title);
        Assert.Equal(removed.Content, recovered.Content);
        Assert.True(recovered.Dirty);
        Assert.Equal(12, recovered.BaseRevision);
        Assert.Same(pending, retained.Pending);
        Assert.Null(Field<string?>(page, "error"));
    }

    private static async Task AssertCurrentProjectSelectionBuildsPreviewRequest(IdeaWorkspace page)
    {
        var context = Field<IdeaWorkspaceContext>(page, "context");
        var current = Field<WorkspaceBrowserDraft>(page, "draft");
        IdeaWorkspacePublicationPreviewRequest? request = null;
        var panel = new WorkspaceDeliveryPanel();
        var callbackOwner = new object();
        Parameter(panel, "Context", context);
        Parameter(panel, "Draft", current.Delivery);
        Parameter(panel, "OnDraftChanged", EventCallback.Factory.Create<WorkspaceDeliveryDraft>(callbackOwner,
            (WorkspaceDeliveryDraft value) => (Task)Invoke(page, "DeliveryChanged", Scope, value)!));
        Parameter(panel, "OnPreview", EventCallback.Factory.Create<IdeaWorkspacePublicationPreviewRequest>(callbackOwner,
            (IdeaWorkspacePublicationPreviewRequest value) => { request = value; }));
        Invoke(panel, "OnParametersSet");
        await (Task)Invoke(panel, "SelectionChanged", "project-2-item", true)!;
        Parameter(panel, "Draft", Field<WorkspaceBrowserDraft>(page, "draft").Delivery);
        Invoke(panel, "OnParametersSet");
        await (Task)Invoke(panel, "PreviewSelection")!;
        Assert.NotNull(request);
        Assert.Equal(["project-2-item"], request.ItemIds);
        Assert.Null(request.FeatureId);
        Assert.Equal(current.Delivery.FeatureName, request.FeatureName);
        Assert.Equal(context.Revision, request.ExpectedRevision);
        Assert.Equal(context.ProjectPlanRevision, request.ExpectedProjectPlanRevision);
    }

    private static (WorkspaceDeliveryPanel Panel, WorkspaceDeliveryDraft Current, List<WorkspaceDeliveryDraft> Changes) DeliveryPanelAfterRelink()
    {
        var panel = new WorkspaceDeliveryPanel();
        var changes = new List<WorkspaceDeliveryDraft>();
        Parameter(panel, "OnDraftChanged", EventCallback.Factory.Create<WorkspaceDeliveryDraft>(changes,
            (WorkspaceDeliveryDraft value) => changes.Add(value)));
        Parameter(panel, "Context", Context(1));
        Parameter(panel, "Draft", DeliveryDraft(1).Delivery);
        Parameter(panel, "Preview", PreviewFor(1));
        Invoke(panel, "OnParametersSet");
        var current = DeliveryDraft(2).Delivery with { SelectedItemIds = [], FeatureId = null, FeatureName = CompleteText("current feature-name draft") };
        Parameter(panel, "Context", Context(2));
        Parameter(panel, "Draft", current);
        Parameter(panel, "Preview", PreviewFor(2));
        Invoke(panel, "OnParametersSet");
        return (panel, current, changes);
    }
    private static IdeaWorkspacePublicationPreview PreviewFor(int projectId)
        => new(Scope.WorkspaceId, 12, projectId, 7, $"plan-{projectId}", "Current plan", null, "Current feature",
            [new(projectId * 10 + 1, $"Project {projectId} feature")], [], 0, 0);

    private static void AssertReconciled(WorkspaceBrowserDraft previous, WorkspaceBrowserDraft current)
    {
        Assert.True(current.Delivery.AssociationKnown);
        Assert.Equal(2, current.Delivery.AssociationProjectId);
        Assert.NotNull(current.Delivery.SelectedItemIds);
        Assert.Empty(current.Delivery.SelectedItemIds);
        Assert.Null(current.Delivery.FeatureId);
        Assert.Equal(previous.Delivery.FeatureName, current.Delivery.FeatureName);
        Assert.Equal(previous.Delivery.ProjectName, current.Delivery.ProjectName);
        Assert.Same(previous.ContentDrafts["local"], current.ContentDrafts["local"]);
        Assert.Same(previous.Plan, current.Plan);
        Assert.Same(previous.Discussion, current.Discussion);
        Assert.Same(previous.Checkpoint, current.Checkpoint);
        Assert.Same(previous.Pending, current.Pending);
    }

    private static WorkspaceBrowserDraft DeliveryDraft(int projectId) => new()
    {
        Delivery = new() { AssociationKnown = true, AssociationProjectId = projectId, SelectedItemIds = [$"project-{projectId}-item"],
            FeatureId = projectId * 10 + 1, FeatureName = "  Retained proposed feature Ω 漢字  ", ProjectName = "  Retained project name 🧭  " },
        ContentDrafts = new() { ["local"] = new() { Key = "local", ComponentId = "local", Content = CompleteText("unpublished component"), Dirty = true } },
        Plan = new() { Title = CompleteText("unpublished plan"), BaseRevision = 8 },
        Discussion = new(CompleteText("unpublished discussion")), Checkpoint = new() { Objective = CompleteText("resume objective") },
        Pending = new(new("immutable-publication", 8, "publish_plan", JsonSerializer.SerializeToElement(new { itemIds = new[] { $"project-{projectId}-item" } }), "human"), null)
    };

    private static WorkspaceBrowserDraft StoredDraft() => new()
    {
        SelectedComponentId = "original-card",
        ContentDrafts = new() { ["original-card"] = new() { Key = "original-card", ComponentId = "original-card", Title = "Stored title Ω", Content = CompleteText("stored dirty draft"), Dirty = true, BaseRevision = 8 } },
        Pending = new(new("stored-uncertain-operation", 8, "update_component", JsonSerializer.SerializeToElement(new { componentId = "original-card", content = CompleteText("immutable pending payload") }), "human"), "content:original-card")
    };

    private static IdeaWorkspace Page(IdeaWorkspaceContext context)
    {
        var page = new IdeaWorkspace();
        Set(page, "context", context);
        Set(page, "workspaceId", Scope.WorkspaceId);
        Set(page, "generation", Scope.Generation);
        return page;
    }
    private static IdeaWorkspaceContext Context(int projectId)
    {
        var source = new IdeaWorkspaceSource("idea", 11, "Current idea", "Complete source", null, null, null, null, null, projectId, default, default);
        return new(Scope.WorkspaceId, "idea", 11, source.Title, 12, source, source, false, projectId, false,
            "/ideas/workspace/idea/11", $"/project/{projectId}", $"project:{projectId}", default, 42, new())
        { ProjectPlanRevision = 7, AcceptedPlan = new() { Id = $"plan-{projectId}", Items = [new() { Id = $"project-{projectId}-item", Title = "Current project item" }] } };
    }
    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;
    private static T Field<T>(object target, string name) => (T)(target.GetType().GetField(name, Flags)
        ?? throw new Xunit.Sdk.XunitException($"Missing field {name}.")).GetValue(target)!;
    private static void Set(object target, string name, object value) => (target.GetType().GetField(name, Flags)
        ?? throw new Xunit.Sdk.XunitException($"Missing field {name}.")).SetValue(target, value);
    private static T Property<T>(object target, string name) => (T)(target.GetType().GetProperty(name, Flags)
        ?? throw new Xunit.Sdk.XunitException($"Missing property {name}.")).GetValue(target)!;
    private static object? Invoke(object target, string name, params object[] args) => (target.GetType().GetMethod(name, Flags)
        ?? throw new Xunit.Sdk.XunitException($"Missing method {name}.")).Invoke(target, args);
    private static void Parameter(object target, string name, object value) => (target.GetType().GetProperty(name)
        ?? throw new Xunit.Sdk.XunitException($"Missing parameter {name}.")).SetValue(target, value);
    private static string CompleteText(string label) => $"\r\n  ## Complete {label} Ω 漢字 🧭  \r\n" + new string('文', 40_000) + "\r\nFINAL COMPLETE REQUIREMENT  \r\n";

    // Model only the external JS/storage boundary. Page restoration, cache priority and retry are real.
    private sealed class DraftStorageRuntime(string raw) : IJSRuntime
    {
        public DraftStorageModule Module { get; } = new(raw);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, default, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Assert.Equal("import", identifier);
            return ValueTask.FromResult((TValue)(object)Module);
        }
    }
    private sealed class DraftStorageModule(string raw) : IJSObjectReference
    {
        public string? Raw { get; private set; } = raw;
        public string? BackupRaw { get; private set; }
        public Exception? InitializeFailure { get; set; }
        public Exception? PreserveFailure { get; set; }
        public Exception? SaveFailure { get; set; }
        public int SaveCalls { get; private set; }
        public int PreserveCalls { get; private set; }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, default, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            object? result;
            switch (identifier)
            {
                case "initialize":
                    if (InitializeFailure is { } initialization) return ValueTask.FromException<TValue>(initialization);
                    JsonElement? draft = Raw is null ? null : JsonSerializer.Deserialize<JsonElement>(Raw);
                    result = JsonSerializer.SerializeToElement(new { clientId = "browser", sessionId = "tab", draft });
                    break;
                case "saveDraft":
                    SaveCalls++;
                    if (SaveFailure is { } save) return ValueTask.FromException<TValue>(save);
                    Raw = JsonSerializer.Serialize(args![1], Json);
                    return ValueTask.FromResult(default(TValue)!);
                case "readStoredDraft": result = Raw; break;
                case "preserveAndClearDraft":
                    PreserveCalls++;
                    if (PreserveFailure is { } preserve) return ValueTask.FromException<TValue>(preserve);
                    BackupRaw = Raw;
                    Raw = null;
                    result = new WorkspaceBrowserDraftRecovery("verified-backup", BackupRaw);
                    break;
                case "disposeWriter": return ValueTask.FromResult(default(TValue)!);
                default: throw new Xunit.Sdk.XunitException($"Unexpected browser method {identifier}.");
            }
            return ValueTask.FromResult((TValue)result!);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
