using System.Reflection;
using System.Text.Json;
using KanbanFlow.Components.Pages;
using KanbanFlow.Components.Workspaces;
using KanbanFlow.Models.IdeaWorkspaces;
using KanbanFlow.Services.IdeaWorkspaces;
using Microsoft.JSInterop;

namespace KanbanFlow.Tests;

public sealed class IdeaWorkspaceStorageRecoveryTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly WorkspaceUiScope Scope = new("idea:11", 3);
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    [Theory]
    [InlineData("refresh")]
    [InlineData("select")]
    [InlineData("both")]
    public async Task RetryAfterFailedInitialization_WithNavigationOnlyCacheRestoresCompleteStoredDraftAndPending(string navigation)
    {
        using var factory = new SqliteTestFactory();
        var service = new IdeaWorkspaceService(factory);
        var created = await service.CreateAsync(new("capture", "test", Title: "Storage navigation regression"));
        await service.ExecuteAsync(created.WorkspaceId, new("add-card", created.Revision, "create_component",
            JsonSerializer.SerializeToElement(new { component = new { id = "original-card", type = "note", title = "Saved card", content = "Complete saved card" },
                placement = new { id = "original-place", x = 20, y = 30, width = 280, height = 180, zIndex = 0, order = 0 } }, Json), "test"));
        var context = await service.GetAsync(created.WorkspaceId);
        var scope = new WorkspaceUiScope(context.WorkspaceId, 3);
        var original = FullDraft("stored");
        var originalRaw = Serialize(original);
        var runtime = new StorageRuntime(originalRaw) { FailInitialize = true };
        var failedBrowser = new WorkspaceBrowserInterop(runtime);
        await Assert.ThrowsAsync<JSException>(() => failedBrowser.InitializeAsync(scope.WorkspaceId).AsTask());
        await using var page = Page(context);
        Set(page, "Workspaces", service); Set(page, "browser", failedBrowser); Set(page, "storageHealthy", false);
        if (navigation is "refresh" or "both") await Call(page, "Refresh", scope);
        if (navigation is "select" or "both") await Call(page, "Select", scope, new WorkspaceSelection("original-card", "original-place"));
        var navigationCache = Field<Dictionary<string, WorkspaceBrowserDraft>>(page, "browserCache")[scope.WorkspaceId];
        Assert.All(navigationCache.ContentDrafts.Values, value => Assert.False(value.Dirty));
        Assert.All(navigationCache.LayoutDrafts.Values, value => Assert.False(value.Dirty));
        Assert.Null(navigationCache.Pending);
        Assert.Equal(originalRaw, runtime.Raw);

        await Call(page, "RetryDraftStorage", scope);
        runtime.FailInitialize = false;
        var retry = new WorkspaceBrowserInterop(runtime);
        Set(page, "browser", retry);
        var session = await retry.InitializeAsync(scope.WorkspaceId);
        Invoke(page, "RestoreDraft", scope, session);

        var restored = Field<WorkspaceBrowserDraft>(page, "draft");
        AssertCompleteDraft(original, restored);
        Assert.Same(restored, Field<Dictionary<string, WorkspaceBrowserDraft>>(page, "browserCache")[scope.WorkspaceId]);
        Assert.Equal(originalRaw, runtime.Raw);
        Assert.Equal(0, runtime.PreserveCalls);
        Assert.Equal(0, runtime.SaveCalls);
        Assert.Equal("browser", Field<string>(page, "clientId"));
        // A later trusted retry must keep the restored stored draft, rather than the earlier clean navigation cache.
        Invoke(page, "RestoreDraft", scope, session);
        Assert.Same(restored.Pending, Field<WorkspaceBrowserDraft>(page, "draft").Pending);
        Set(page, "restoredGeneration", scope.Generation);
        Assert.True(await (Task<bool>)Invoke(page, "PersistDraft", scope)!);
        AssertCompleteDraft(original, JsonSerializer.Deserialize<WorkspaceBrowserDraft>(runtime.Raw!, Json)!);
        Assert.Null(runtime.BackupRaw);
    }

    [Theory]
    [InlineData("content")]
    [InlineData("layout")]
    [InlineData("pending")]
    [InlineData("discussion")]
    [InlineData("connector-from")]
    [InlineData("connector-to")]
    [InlineData("connector-label")]
    [InlineData("plan-title")]
    [InlineData("plan-items")]
    [InlineData("plan-baseline")]
    [InlineData("project-plan-baseline")]
    [InlineData("checkpoint-objective")]
    [InlineData("checkpoint-completed")]
    [InlineData("checkpoint-questions")]
    [InlineData("checkpoint-next")]
    [InlineData("checkpoint-focus")]
    [InlineData("checkpoint-original-completed")]
    [InlineData("checkpoint-original-questions")]
    [InlineData("checkpoint-original-next")]
    [InlineData("delivery-project-name")]
    [InlineData("delivery-feature-name")]
    [InlineData("delivery-target")]
    [InlineData("delivery-items")]
    [InlineData("delivery-empty-items")]
    [InlineData("delivery-feature")]
    public async Task UntrustedCache_WithAnyAuthoredSectionRequiresPreservationAndRetainsBothCompleteDrafts(string section)
    {
        var original = FullDraft("stored");
        var originalRaw = Serialize(original);
        var runtime = new StorageRuntime(originalRaw);
        var browser = new WorkspaceBrowserInterop(runtime);
        var stored = await browser.InitializeAsync(Scope.WorkspaceId);
        await using var page = Page(Context());
        Set(page, "browser", browser);
        var authored = WithAuthoredSection(NavigationDraft(), section);
        authored.Validate();
        Set(page, "draft", authored);
        Assert.False(await (Task<bool>)Invoke(page, "PersistDraft", Scope)!);
        var completeMemory = Serialize(authored);

        var failure = Assert.Throws<TargetInvocationException>(() => Invoke(page, "RestoreDraft", Scope, stored));

        Assert.IsType<JsonException>(failure.InnerException);
        Assert.Same(authored, Field<WorkspaceBrowserDraft>(page, "draft"));
        Assert.Equal(completeMemory, Serialize(Field<WorkspaceBrowserDraft>(page, "draft")));
        Assert.Same(authored, Field<Dictionary<string, WorkspaceBrowserDraft>>(page, "browserCache")[Scope.WorkspaceId]);
        Assert.Equal(originalRaw, runtime.Raw);
        Assert.Equal(0, runtime.SaveCalls); Assert.Equal(0, runtime.PreserveCalls);
        Assert.Equal("", Field<string>(page, "clientId"));
        Assert.False(Field<bool>(page, "storageHealthy"));
        Assert.DoesNotContain(Scope.WorkspaceId, Field<HashSet<string>>(page, "restoredBrowserWorkspaces"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Recovery_HoldsBrowserOwnershipAgainstRetryAndDisposesOnlyItsOwnWrapper(bool hasClientIdentity, bool replaceWrapper)
    {
        var originalRaw = Serialize(FullDraft("stored"));
        var memory = FullDraft("current memory");
        var runtime = new StorageRuntime(originalRaw) { HoldPreserve = true };
        var owned = new WorkspaceBrowserInterop(runtime);
        await owned.InitializeAsync(Scope.WorkspaceId);
        var ownedModule = Assert.Single(runtime.Modules);
        await using var page = Page(Context());
        Set(page, "browser", owned); Set(page, "draft", memory); Set(page, "storedDraftRaw", originalRaw);
        Set(page, "storageHealthy", false); Set(page, "storageError", "Storage initialization failed.");
        if (hasClientIdentity) Set(page, "clientId", "browser");
        var recovery = Call(page, "RecoverStoredDraft", Scope);
        WorkspaceBrowserInterop? replacement = null;
        StorageModule? replacementModule = null;
        try
        {
            await runtime.PreserveEntered.Task.WaitAsync(Deadline);
            Assert.True(Field<bool>(page, "busy"));

            await Call(page, "RetryDraftStorage", Scope);

            Assert.Same(owned, Field<WorkspaceBrowserInterop>(page, "browser"));
            Assert.Equal(0, ownedModule.DisposeWriterCalls); Assert.Equal(0, ownedModule.DisposeCalls);
            Assert.Equal(0, runtime.SaveCalls); Assert.Equal(originalRaw, runtime.Raw);
            Assert.Equal(3, Field<int>(page, "restoredGeneration"));
            if (replaceWrapper)
            {
                replacement = new WorkspaceBrowserInterop(runtime);
                await replacement.InitializeAsync(Scope.WorkspaceId);
                replacementModule = runtime.Modules.Last();
                Set(page, "browser", replacement);
            }
            runtime.PreserveRelease.TrySetResult();
            await recovery.WaitAsync(Deadline);

            Assert.Equal(originalRaw, runtime.BackupRaw); Assert.Null(runtime.Raw);
            Assert.Equal(1, runtime.PreserveCalls);
            Assert.Equal(1, ownedModule.DisposeWriterCalls); Assert.Equal(1, ownedModule.DisposeCalls);
            Assert.Same(replacement, Field<WorkspaceBrowserInterop?>(page, "browser"));
            if (replacementModule is not null) { Assert.Equal(0, replacementModule.DisposeWriterCalls); Assert.Equal(0, replacementModule.DisposeCalls); }
            Assert.Same(memory, Field<WorkspaceBrowserDraft>(page, "draft"));
            Assert.Same(memory, Field<Dictionary<string, WorkspaceBrowserDraft>>(page, "browserCache")[Scope.WorkspaceId]);
            Assert.Contains(Scope.WorkspaceId, Field<HashSet<string>>(page, "restoredBrowserWorkspaces"));
            Assert.Equal(originalRaw, Field<Dictionary<string, WorkspaceBrowserDraftRecovery>>(page, "draftRecoveries")[Scope.WorkspaceId].Raw);
            Assert.Null(Field<string?>(page, "storageError")); Assert.False(Field<bool>(page, "busy"));
            Assert.Equal(-1, Field<int>(page, "restoredGeneration")); Assert.Equal("", Field<string>(page, "clientId"));
            var fresh = replacement ?? new WorkspaceBrowserInterop(runtime);
            Set(page, "browser", fresh);
            Invoke(page, "RestoreDraft", Scope, await fresh.InitializeAsync(Scope.WorkspaceId));
            Set(page, "restoredGeneration", Scope.Generation);
            Assert.True(await (Task<bool>)Invoke(page, "PersistDraft", Scope)!);
            AssertCompleteDraft(memory, JsonSerializer.Deserialize<WorkspaceBrowserDraft>(runtime.Raw!, Json)!);
            Assert.Equal(originalRaw, runtime.BackupRaw);
        }
        finally { runtime.PreserveRelease.TrySetResult(); await recovery.WaitAsync(Deadline); }
    }

    private static WorkspaceBrowserDraft WithAuthoredSection(WorkspaceBrowserDraft draft, string section) => section switch
    {
        "content" => draft with { ContentDrafts = new(draft.ContentDrafts) { ["$new"] = new() { Dirty = true, Content = CompleteText("new memory") } } },
        "layout" => draft with { LayoutDrafts = new(draft.LayoutDrafts) { ["original-place"] = draft.LayoutDrafts["original-place"] with { Dirty = true, X = 740 } } },
        "pending" => draft with { Pending = FullDraft("pending only").Pending },
        "discussion" => draft with { Discussion = new(" \r\n ") },
        "connector-from" => draft with { Connector = new("original-card") },
        "connector-to" => draft with { Connector = new(ToComponentId: "original-card") },
        "connector-label" => draft with { Connector = new(Label: "") },
        "plan-title" => draft with { Plan = new() { Title = " \r\n " } },
        "plan-items" => draft with { Plan = new() { Items = [new() { Id = "unfinished-item" }] } },
        "plan-baseline" => draft with { Plan = new() { BaseRevision = 0 } },
        "project-plan-baseline" => draft with { Plan = new() { BaseProjectPlanRevision = 0 } },
        "checkpoint-objective" => draft with { Checkpoint = new() { Objective = CompleteText("objective") } },
        "checkpoint-completed" => draft with { Checkpoint = new() { CompletedChanges = " \r\n " } },
        "checkpoint-questions" => draft with { Checkpoint = new() { OpenQuestions = " \r\n " } },
        "checkpoint-next" => draft with { Checkpoint = new() { NextActions = " \r\n " } },
        "checkpoint-focus" => draft with { Checkpoint = new() { FocusedComponentIds = ["original-card"] } },
        "checkpoint-original-completed" => draft with { Checkpoint = new() { OriginalCompletedChanges = [] } },
        "checkpoint-original-questions" => draft with { Checkpoint = new() { OriginalOpenQuestions = [""] } },
        "checkpoint-original-next" => draft with { Checkpoint = new() { OriginalNextActions = [] } },
        "delivery-project-name" => draft with { Delivery = draft.Delivery with { ProjectName = " \r\n " } },
        "delivery-feature-name" => draft with { Delivery = draft.Delivery with { FeatureName = CompleteText("feature name") } },
        "delivery-target" => draft with { Delivery = draft.Delivery with { TargetProjectId = 1 } },
        "delivery-items" => draft with { Delivery = draft.Delivery with { SelectedItemIds = ["plan-item"] } },
        "delivery-empty-items" => draft with { Delivery = draft.Delivery with { SelectedItemIds = [] } },
        "delivery-feature" => draft with { Delivery = draft.Delivery with { FeatureId = 1 } },
        _ => throw new ArgumentOutOfRangeException(nameof(section))
    };

    private static WorkspaceBrowserDraft NavigationDraft() => new()
    {
        SelectedComponentId = "original-card", SelectedPlacementId = "original-place",
        ContentDrafts = new() { ["original-card"] = WorkspaceContentDraft.From(Context().Document.Components[0], 4), ["$new"] = new() { BaseRevision = 4 } },
        LayoutDrafts = new() { ["original-place"] = WorkspaceLayoutDraft.From(Context().Document.Placements[0], 4) },
        Delivery = new() { AssociationKnown = true, AssociationProjectId = null }
    };
    private static WorkspaceBrowserDraft FullDraft(string label)
    {
        var content = new WorkspaceContentDraft { Key = "original-card", ComponentId = "original-card", BaseRevision = 1, Title = "  " + label + " Ω  ",
            Content = CompleteText(label), OriginalContent = "Original saved content", OriginalTitle = "Original saved title", Dirty = true };
        var command = new IdeaWorkspaceCommand(label + "-same-operation", 1, "update_component",
            JsonSerializer.SerializeToElement(new { componentId = content.ComponentId, title = content.Title, content = content.Content }, Json), "human");
        return new()
        {
            SelectedComponentId = "original-card", SelectedPlacementId = "original-place", ContentDrafts = new() { [content.Key] = content },
            LayoutDrafts = new() { ["original-place"] = new() { PlacementId = "original-place", BaseRevision = 1, X = 840, Y = 730, Width = 680, Height = 520, Dirty = true } },
            Discussion = new(CompleteText(label + " discussion")), Connector = new("original-card", "another-card", "  Full relation Ω  "),
            Plan = new() { Title = CompleteText(label + " plan"), BaseRevision = 1, Items = [new() { Id = "local-plan-item", Title = "Full task", Description = CompleteText(label + " specification"), AcceptanceCriteria = "  criterion\r\nΩ  " }] },
            Checkpoint = new() { Objective = CompleteText(label + " checkpoint"), CompletedChanges = "  Complete change\r\n", OpenQuestions = "  Unanswered Ω  ", NextActions = "  Keep going\r\n", FocusedComponentIds = [content.ComponentId!] },
            Delivery = new() { AssociationKnown = true, FeatureName = CompleteText(label + " feature"), ProjectName = "  " + label + " project Ω  " },
            Pending = new(command, "content:original-card", JsonSerializer.SerializeToElement(content, Json))
        };
    }
    private static void AssertCompleteDraft(WorkspaceBrowserDraft expected, WorkspaceBrowserDraft actual)
    {
        Assert.Equal(Serialize(expected), Serialize(actual));
        Assert.Equal(expected.Pending!.Command.Payload.GetRawText(), actual.Pending!.Command.Payload.GetRawText());
        Assert.Equal(expected.Pending.DraftAtSend!.Value.GetRawText(), actual.Pending.DraftAtSend!.Value.GetRawText());
    }
    private static string CompleteText(string label) => "  # " + label + " Ω\r\n\r\n    indented source  \r\n" + new string('文', 12_000) + "\r\ncomplete final requirement  ";
    private static string Serialize(WorkspaceBrowserDraft draft) => JsonSerializer.Serialize(draft, Json);
    private static IdeaWorkspaceContext Context()
    {
        var source = new IdeaWorkspaceSource("idea", 11, "Storage recovery", "Complete source", null, null, "New", null, null, null, DateTime.UnixEpoch, DateTime.UnixEpoch);
        return new(Scope.WorkspaceId, "idea", 11, source.Title, 4, source, source, false, null, false, "/ideas/workspace/idea/11", null, null, DateTime.UnixEpoch, 0,
            new() { Components = [new() { Id = "original-card", Title = "Saved card", Content = "Complete saved card" }],
                Placements = [new() { Id = "original-place", ComponentId = "original-card", X = 20, Y = 30 }] });
    }
    private static IdeaWorkspace Page(IdeaWorkspaceContext context)
    {
        var page = new IdeaWorkspace();
        Set(page, "context", context); Set(page, "workspaceId", context.WorkspaceId); Set(page, "generation", 3); Set(page, "restoredGeneration", 3);
        return page;
    }
    private static Task Call(object target, string name, params object?[] args) => (Task)Invoke(target, name, args)!;
    private static object? Invoke(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Flags)!.Invoke(target, args);
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Flags)!.GetValue(target)!;
    private static void Set(object target, string name, object? value)
    {
        var property = target.GetType().GetProperty(name, Flags);
        if (property is not null) property.SetValue(target, value); else target.GetType().GetField(name, Flags)!.SetValue(target, value);
    }

    private sealed class StorageRuntime(string raw) : IJSRuntime
    {
        public string? Raw { get; set; } = raw;
        public string? BackupRaw { get; set; }
        public bool FailInitialize { get; set; }
        public bool HoldPreserve { get; set; }
        public int SaveCalls { get; set; }
        public int PreserveCalls { get; set; }
        public List<StorageModule> Modules { get; } = [];
        public TaskCompletionSource PreserveEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PreserveRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => InvokeAsync<T>(identifier, default, args);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier != "import") throw new Xunit.Sdk.XunitException("Unexpected browser import: " + identifier);
            var module = new StorageModule(this);
            Modules.Add(module);
            return ValueTask.FromResult((T)(object)module);
        }
    }
    private sealed class StorageModule(StorageRuntime runtime) : IJSObjectReference
    {
        public int DisposeWriterCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => InvokeAsync<T>(identifier, default, args);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "initialize")
            {
                if (runtime.FailInitialize) return ValueTask.FromException<T>(new JSException("Temporary initialization failure"));
                JsonElement? draft = runtime.Raw is null ? null : JsonSerializer.Deserialize<JsonElement>(runtime.Raw);
                return ValueTask.FromResult((T)(object)JsonSerializer.SerializeToElement(new { clientId = "browser", sessionId = "tab", draft }));
            }
            if (identifier == "saveDraft") { runtime.SaveCalls++; runtime.Raw = JsonSerializer.Serialize(args![1], Json); }
            else if (identifier == "preserveAndClearDraft") return Preserve<T>();
            else if (identifier == "disposeWriter") DisposeWriterCalls++;
            else throw new Xunit.Sdk.XunitException("Unexpected browser method: " + identifier);
            return ValueTask.FromResult(default(T)!);
        }
        private async ValueTask<T> Preserve<T>()
        {
            runtime.PreserveCalls++;
            runtime.PreserveEntered.TrySetResult();
            if (runtime.HoldPreserve) await runtime.PreserveRelease.Task;
            runtime.BackupRaw = runtime.Raw;
            runtime.Raw = null;
            return (T)(object)new WorkspaceBrowserDraftRecovery("verified-backup", runtime.BackupRaw);
        }
        public ValueTask DisposeAsync() { DisposeCalls++; return ValueTask.CompletedTask; }
    }
}
