using System.Reflection;
using System.Text.Json;
using KanbanFlow.Components.Pages;
using KanbanFlow.Components.Workspaces;
using KanbanFlow.Models.IdeaWorkspaces;
using KanbanFlow.Services.IdeaWorkspaces;
using Microsoft.JSInterop;

namespace KanbanFlow.Tests;

public sealed class IdeaWorkspaceDraftIdentityTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly WorkspaceUiScope Scope = new("idea:11", 3);

    [Fact]
    public async Task SavedLiteralNewComponent_AndUnsavedNewCard_HaveIndependentSelectionAndDirtyIdentities()
    {
        await using var page = Page();
        var unsaved = NewCard();
        var original = new WorkspaceBrowserDraft { SelectedComponentId = "$new", ContentDrafts = new() { ["$new"] = unsaved } };

        Restore(page, original);

        var restored = Field<WorkspaceBrowserDraft>(page, "draft");
        Assert.Equal("", Property<string>(page, "SelectedContentKey"));
        Assert.Equal(WorkspaceContentDraft.From(Context().Document.Components[0], 12), restored.ContentDrafts[""]);
        Assert.Same(unsaved, restored.ContentDrafts["$new"]);
        Assert.Equal("$new", restored.ContentDrafts[""].ComponentId);
        Assert.Empty(Property<IReadOnlySet<string>>(page, "DirtyIds"));
        var edited = restored.ContentDrafts[""] with { Dirty = true, Content = CompleteText("saved component edit") };
        await (Task)Invoke(page, "ContentChanged", Scope, edited)!;
        Assert.Equal(["$new"], Property<IReadOnlySet<string>>(page, "DirtyIds"));
        await (Task)Invoke(page, "NewComponent", Scope)!;
        Assert.Equal("$new", Property<string>(page, "SelectedContentKey"));
        Assert.Same(unsaved, Property<WorkspaceContentDraft>(page, "SelectedContent"));
        Assert.Same(edited, Field<WorkspaceBrowserDraft>(page, "draft").ContentDrafts[""]);
    }

    [Fact]
    public async Task RefreshingCleanBuffers_UsesComponentIdentity_InsteadOfTheNewCardDictionaryKey()
    {
        await using var page = Page();
        var unsaved = NewCard() with { Dirty = false, BaseRevision = 8 };
        var saved = SavedCard() with { Key = "", Dirty = false };
        Set(page, "draft", new WorkspaceBrowserDraft { SelectedComponentId = "$new", ContentDrafts = new() { ["$new"] = unsaved, [""] = saved } });

        Invoke(page, "UpdateCleanDrafts");

        var refreshed = Field<WorkspaceBrowserDraft>(page, "draft");
        Assert.Equal(unsaved with { BaseRevision = 12 }, refreshed.ContentDrafts["$new"]);
        Assert.Equal(WorkspaceContentDraft.From(Context().Document.Components[0], 12), refreshed.ContentDrafts[""]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyStoredOrCachedSavedLiteralNewDraft_MigratesWithoutChangingItsContentOrCommand(bool fromCache)
    {
        await using var page = Page();
        var saved = SavedCard();
        var command = Command("update_component", "$new");
        var pending = new WorkspacePendingCommand(command, "content:$new", JsonSerializer.SerializeToElement(saved, Json));
        var previous = new WorkspaceBrowserDraft { SelectedComponentId = "$new", ContentDrafts = new() { ["$new"] = saved }, Pending = pending };
        if (fromCache)
        {
            Field<Dictionary<string, WorkspaceBrowserDraft>>(page, "browserCache")[Scope.WorkspaceId] = previous;
            Field<HashSet<string>>(page, "restoredBrowserWorkspaces").Add(Scope.WorkspaceId);
        }

        Restore(page, fromCache ? new() : previous);

        var restored = Field<WorkspaceBrowserDraft>(page, "draft");
        Assert.Equal(saved with { Key = "" }, restored.ContentDrafts[""]);
        Assert.DoesNotContain("$new", restored.ContentDrafts.Keys);
        Assert.Equal("$new", restored.SelectedComponentId);
        Assert.Same(command, restored.Pending!.Command);
        Assert.Equal("content:", restored.Pending.DraftSection);
        Assert.Equal("", restored.Pending.DraftAtSend!.Value.GetProperty("key").GetString());
        Assert.Equal("$new", restored.Pending.DraftAtSend.Value.GetProperty("componentId").GetString());
        Assert.Equal(saved, previous.ContentDrafts["$new"]);
        Assert.Equal("$new", pending.DraftAtSend!.Value.GetProperty("key").GetString());
        Assert.Equal(command.Payload.GetRawText(), restored.Pending.Command.Payload.GetRawText());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RemovedMappedSavedLiteralNewDraft_RemainsReachable_AndCannotOverwriteAnotherDirtyNewCard(bool dirtyNewSlot)
    {
        await using var page = Page(false);
        var saved = SavedCard() with { Key = "" };
        var unsaved = NewCard() with { Dirty = dirtyNewSlot };
        var original = new WorkspaceBrowserDraft { SelectedComponentId = "$new", ContentDrafts = new() { [""] = saved, ["$new"] = unsaved } };
        Set(page, "draft", original);

        Invoke(page, "EnsureSelectedDrafts");
        Assert.Equal("$new", Field<WorkspaceBrowserDraft>(page, "draft").SelectedComponentId);
        Assert.Same(saved, Property<WorkspaceContentDraft>(page, "SelectedContent"));
        var beforeRecovery = Field<WorkspaceBrowserDraft>(page, "draft");
        await (Task)Invoke(page, "ReapplyContent", Scope, "")!;

        var recovered = Field<WorkspaceBrowserDraft>(page, "draft");
        if (dirtyNewSlot)
        {
            Assert.Same(beforeRecovery, recovered);
            Assert.Same(saved, recovered.ContentDrafts[""]);
            Assert.Same(unsaved, recovered.ContentDrafts["$new"]);
            Assert.Contains("Add component", Field<string>(page, "error"));
        }
        else
        {
            Assert.DoesNotContain("", recovered.ContentDrafts.Keys);
            Assert.Equal(saved with { Key = "$new", ComponentId = null, BaseRevision = 12 }, recovered.ContentDrafts["$new"]);
            Assert.Null(recovered.SelectedComponentId);
        }
    }

    [Theory]
    [InlineData("update_component")]
    [InlineData("remove_component")]
    public void LegacyRealComponentAcknowledgement_ClearsOnlyItsMappedSlot_AndKeepsTheNewCard(string action)
    {
        var saved = SavedCard();
        var unsaved = NewCard();
        var pending = new WorkspacePendingCommand(Command(action, "$new"), "content:$new", JsonSerializer.SerializeToElement(saved, Json));
        var normalized = new WorkspaceBrowserDraft { ContentDrafts = new() { [""] = saved with { Key = "" }, ["$new"] = unsaved }, Pending = pending }.NormalizeContentIdentity();

        var acknowledged = Clear(normalized with { Pending = null }, normalized.Pending!);

        Assert.DoesNotContain("", acknowledged.ContentDrafts.Keys);
        Assert.Same(unsaved, acknowledged.ContentDrafts["$new"]);
        Assert.Same(pending.Command, normalized.Pending!.Command);
        Assert.Equal("content:", normalized.Pending.DraftSection);
    }

    [Fact]
    public void NewCardCreationMetadataAndSnapshot_StayUnchanged_AndClearOnlyTheNewCardSlot()
    {
        var saved = SavedCard() with { Key = "" };
        var unsaved = NewCard();
        var command = new IdeaWorkspaceCommand("create-operation", 8, "create_component", JsonSerializer.SerializeToElement(new { component = new { id = "$new", content = unsaved.Content } }), "human");
        var pending = new WorkspacePendingCommand(command, "content:$new", JsonSerializer.SerializeToElement(unsaved, Json));
        var original = new WorkspaceBrowserDraft { ContentDrafts = new() { [""] = saved, ["$new"] = unsaved }, Pending = pending };

        var normalized = original.NormalizeContentIdentity();
        var acknowledged = Clear(normalized with { Pending = null }, normalized.Pending!);

        Assert.Same(original, normalized);
        Assert.Same(pending, normalized.Pending);
        Assert.Equal(pending.DraftAtSend!.Value.GetRawText(), normalized.Pending!.DraftAtSend!.Value.GetRawText());
        Assert.DoesNotContain("$new", acknowledged.ContentDrafts.Keys);
        Assert.Same(saved, acknowledged.ContentDrafts[""]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcknowledgementPreservesAnyNewerEdit_InEitherTheSavedOrNewCardSlot(bool creation)
    {
        var atSend = creation ? NewCard() : SavedCard();
        var action = creation ? "create_component" : "update_component";
        var command = creation
            ? new IdeaWorkspaceCommand("create-operation", 8, action, JsonSerializer.SerializeToElement(new { component = new { content = atSend.Content } }), "human")
            : Command(action, "$new");
        var pending = new WorkspacePendingCommand(command, "content:$new", JsonSerializer.SerializeToElement(atSend, Json));
        var original = new WorkspaceBrowserDraft { ContentDrafts = new() {
            [""] = SavedCard() with { Key = "" }, ["$new"] = NewCard() }, Pending = pending }.NormalizeContentIdentity();
        var key = creation ? "$new" : "";
        var later = original.ContentDrafts[key] with { Content = CompleteText("newer edit must survive") };
        var updated = original with { ContentDrafts = new(original.ContentDrafts) { [key] = later }, Pending = null };

        var acknowledged = Clear(updated, original.Pending!);

        Assert.Same(updated, acknowledged);
        Assert.Same(later, acknowledged.ContentDrafts[key]);
        Assert.Equal(2, acknowledged.ContentDrafts.Count);
    }

    [Theory]
    [InlineData("update_component", "other", "content:$new")]
    [InlineData("move_components", "$new", "content:$new")]
    [InlineData("update_component", "$new", "content:other")]
    public void NonmatchingPendingMetadata_RemainsUnchanged(string action, string componentId, string section)
    {
        var pending = new WorkspacePendingCommand(Command(action, componentId), section, JsonSerializer.SerializeToElement(SavedCard(), Json));
        var original = new WorkspaceBrowserDraft { Pending = pending };

        var normalized = original.NormalizeContentIdentity();

        Assert.Same(original, normalized);
        Assert.Same(pending, normalized.Pending);
        Assert.Equal(pending.Command.Payload.GetRawText(), normalized.Pending!.Command.Payload.GetRawText());
        Assert.Equal(pending.DraftAtSend!.Value.GetRawText(), normalized.Pending.DraftAtSend!.Value.GetRawText());
    }

    [Fact]
    public void SnapshotRemapping_PreservesEveryUnknownPropertyIncludingDuplicates_AndTheExactCommand()
    {
        var text = JsonSerializer.Serialize(CompleteText("unknown snapshot content"));
        using var document = JsonDocument.Parse("{\"key\":\"$new\",\"componentId\":\"$new\",\"future\":{\"scale\":1e+2},\"future\":{\"content\":" + text + "},\"futureNull\":null}");
        var snapshot = document.RootElement.Clone();
        var pending = new WorkspacePendingCommand(Command("update_component", "$new"), "content:$new", snapshot);
        var unsaved = NewCard();

        var normalized = new WorkspaceBrowserDraft { ContentDrafts = new() { ["$new"] = unsaved }, Pending = pending }.NormalizeContentIdentity();

        Assert.Same(pending.Command, normalized.Pending!.Command);
        Assert.Same(unsaved, normalized.ContentDrafts["$new"]);
        var before = snapshot.EnumerateObject().ToArray();
        var after = normalized.Pending.DraftAtSend!.Value.EnumerateObject().ToArray();
        Assert.Equal(before.Length, after.Length);
        for (var index = 0; index < before.Length; index++)
        {
            Assert.Equal(before[index].Name, after[index].Name);
            if (before[index].Name == "key") Assert.Equal("", after[index].Value.GetString());
            else Assert.Equal(before[index].Value.GetRawText(), after[index].Value.GetRawText());
        }
        Assert.Equal("$new", snapshot.GetProperty("key").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrMismatchedLegacySnapshot_CannotClearTheMappedOrNewCard(bool missing)
    {
        var saved = SavedCard() with { Key = "" };
        var unsaved = NewCard();
        JsonElement? snapshot = missing ? null : JsonSerializer.SerializeToElement(unsaved, Json);
        var pending = new WorkspacePendingCommand(Command("update_component", "$new"), "content:$new", snapshot);
        var normalized = new WorkspaceBrowserDraft { ContentDrafts = new() { [""] = saved, ["$new"] = unsaved }, Pending = pending }.NormalizeContentIdentity();
        var active = normalized with { Pending = null };

        Assert.Same(active, Clear(active, normalized.Pending!));
        Assert.Same(saved, active.ContentDrafts[""]);
        Assert.Same(unsaved, active.ContentDrafts["$new"]);
        Assert.Same(pending.Command, normalized.Pending!.Command);
        Assert.Equal("content:", normalized.Pending.DraftSection);
        if (missing) Assert.Equal(JsonValueKind.Null, normalized.Pending.DraftAtSend!.Value.ValueKind);
        else Assert.Equal(snapshot!.Value.GetRawText(), normalized.Pending.DraftAtSend!.Value.GetRawText());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConflictingLegacyMapping_AndBackupFailure_RetainEveryOriginalBufferAndStoredByte(bool fromCache)
    {
        var original = new WorkspaceBrowserDraft { SelectedComponentId = "$new", ContentDrafts = new() {
            ["$new"] = SavedCard(), [""] = NewCard() with { Key = "", Content = CompleteText("occupied mapped slot") } } };
        var raw = JsonSerializer.Serialize(original, Json);
        var runtime = new FailingBackupRuntime(raw);
        await using var page = Page();
        var browser = new WorkspaceBrowserInterop(runtime);
        Set(page, "browser", browser);
        if (fromCache)
        {
            Set(page, "draft", original);
            Field<Dictionary<string, WorkspaceBrowserDraft>>(page, "browserCache")[Scope.WorkspaceId] = original;
            Field<HashSet<string>>(page, "restoredBrowserWorkspaces").Add(Scope.WorkspaceId);
        }
        var before = Field<WorkspaceBrowserDraft>(page, "draft");

        var collision = Assert.Throws<TargetInvocationException>(() => Invoke(page, "RestoreDraft", Scope, new WorkspaceBrowserSession("browser", "tab", original)));
        Assert.IsType<JsonException>(collision.InnerException);
        Assert.Same(before, Field<WorkspaceBrowserDraft>(page, "draft"));
        await (Task)Invoke(page, "RecoverStoredDraft", Scope)!;

        Assert.Equal(raw, runtime.Raw);
        Assert.Equal(0, runtime.SaveCalls);
        Assert.Equal(1, runtime.PreserveCalls);
        Assert.Same(before, Field<WorkspaceBrowserDraft>(page, "draft"));
        Assert.Equal(raw, JsonSerializer.Serialize(original, Json));
        Assert.Contains("backup quota exhausted", Field<string>(page, "storageError"));
    }

    private static IdeaWorkspaceCommand Command(string action, string componentId)
        => new("immutable-operation", 8, action, JsonSerializer.SerializeToElement(new { componentId, content = CompleteText("exact pending command"), future = new { retain = true } }), "human");
    private static WorkspaceContentDraft SavedCard() => new() { Key = "$new", ComponentId = "$new", BaseRevision = 8,
        Title = "  Saved literal identifier Ω  ", Content = CompleteText("saved literal component"), Type = "markdown",
        OriginalTitle = "Original saved title", OriginalContent = CompleteText("original saved content"), Url = "https://example.invalid/source", Dirty = true };
    private static WorkspaceContentDraft NewCard() => new() { Title = "  Unsaved new card 漢字  ", Content = CompleteText("independent new card"), Dirty = true, BaseRevision = 8 };
    private static string CompleteText(string label) => $"\r\n  ## Complete {label} Ω 漢字 🧭  \r\n" + new string('文', 40_000) + "\r\nFINAL COMPLETE CONTENT  \r\n";
    private static IdeaWorkspace Page(bool hasComponent = true)
    {
        var page = new IdeaWorkspace();
        Set(page, "workspaceId", Scope.WorkspaceId);
        Set(page, "generation", Scope.Generation);
        Set(page, "context", Context(hasComponent));
        return page;
    }
    private static IdeaWorkspaceContext Context(bool hasComponent = true)
    {
        var source = new IdeaWorkspaceSource("idea", 11, "Identity fixture", "Source", null, null, null, null, null, null, default, default);
        return new(Scope.WorkspaceId, "idea", 11, source.Title, 12, source, source, false, null, false,
            "/ideas/workspace/idea/11", null, null, default, 42, new() { Components = hasComponent ? [new() { Id = "$new", Title = "Current saved component", Content = CompleteText("current saved text") }] : [] });
    }
    private static void Restore(IdeaWorkspace page, WorkspaceBrowserDraft stored) => Invoke(page, "RestoreDraft", Scope, new WorkspaceBrowserSession("browser", "tab", stored));
    private static WorkspaceBrowserDraft Clear(WorkspaceBrowserDraft draft, WorkspacePendingCommand pending)
        => (WorkspaceBrowserDraft)(typeof(IdeaWorkspace).GetMethod("ClearSavedSection", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new Xunit.Sdk.XunitException("Missing acknowledgement helper")).Invoke(null, [draft, pending])!;
    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;
    private static T Field<T>(object target, string name) => (T)(target.GetType().GetField(name, Flags)
        ?? throw new Xunit.Sdk.XunitException($"Missing field {name}")).GetValue(target)!;
    private static void Set(object target, string name, object value) => (target.GetType().GetField(name, Flags)
        ?? throw new Xunit.Sdk.XunitException($"Missing field {name}")).SetValue(target, value);
    private static T Property<T>(object target, string name) => (T)(target.GetType().GetProperty(name, Flags)
        ?? throw new Xunit.Sdk.XunitException($"Missing property {name}")).GetValue(target)!;
    private static object? Invoke(object target, string name, params object[] args) => (target.GetType().GetMethod(name, Flags)
        ?? throw new Xunit.Sdk.XunitException($"Missing method {name}")).Invoke(target, args);

    private sealed class FailingBackupRuntime(string raw) : IJSRuntime, IJSObjectReference
    {
        public string Raw { get; } = raw;
        public int SaveCalls { get; private set; }
        public int PreserveCalls { get; private set; }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, default, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "import") return ValueTask.FromResult((TValue)(object)this);
            if (identifier == "preserveAndClearDraft") { PreserveCalls++; return ValueTask.FromException<TValue>(new JSException("backup quota exhausted")); }
            if (identifier == "saveDraft") SaveCalls++;
            if (identifier == "disposeWriter") return ValueTask.FromResult(default(TValue)!);
            throw new Xunit.Sdk.XunitException($"Unexpected browser method {identifier}");
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
