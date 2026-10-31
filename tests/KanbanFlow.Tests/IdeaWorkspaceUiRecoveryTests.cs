using System.Reflection;
using System.Text.Json;
using KanbanFlow.Components.Pages;
using KanbanFlow.Components.Workspaces;
using KanbanFlow.Models.IdeaWorkspaces;
using KanbanFlow.Services.IdeaWorkspaces;
using Microsoft.AspNetCore.Components;

namespace KanbanFlow.Tests;

public sealed class IdeaWorkspaceUiRecoveryTests
{
    [Fact]
    public async Task RecoveryAfterFailedInitialization_RetainsCompleteNewHumanEditsAndPendingOperation_ThroughRestoration()
    {
        const string originalRaw = "{\"contentDrafts\":null}";
        var runtime = new WorkspaceBrowserInteropTests.BrowserRuntime();
        runtime.Module.Raw = originalRaw;
        runtime.Module.Initialization = JsonSerializer.SerializeToElement(new { clientId = "browser-client", sessionId = "tab-session", draft = new { contentDrafts = (object?)null } });
        var browser = new WorkspaceBrowserInterop(runtime);
        await Assert.ThrowsAsync<JsonException>(() => browser.InitializeAsync("idea:11").AsTask());
        await using var component = new IdeaWorkspace();
        var text = CompleteText();
        var pending = new WorkspacePendingCommand(new("retained-operation", 8, "create_component", JsonSerializer.SerializeToElement(new { content = text }), "human"), "content:$new");
        Prepare(component, new() { Pending = pending });
        SetField(component, "browser", browser);
        SetField(component, "storageHealthy", false);
        SetField(component, "storedDraftRaw", originalRaw);
        var scope = new WorkspaceUiScope("idea:11", 3);
        var entered = new WorkspaceContentDraft { Title = "Human edit after failed initialization Ω", Content = text, Dirty = true, BaseRevision = 12 };

        await (Task)Invoke(component, "ContentChanged", scope, entered)!;
        Assert.Equal(0, runtime.Module.PreserveCalls);
        await (Task)Invoke(component, "RecoverStoredDraft", scope)!;

        Assert.Equal(1, runtime.Module.PreserveCalls);
        var retained = Field<WorkspaceBrowserDraft>(component, "draft");
        Assert.Same(entered, retained.ContentDrafts["$new"]);
        Assert.Same(pending, retained.Pending);
        var preserved = Field<Dictionary<string, WorkspaceBrowserDraftRecovery>>(component, "draftRecoveries")["idea:11"];
        Assert.Equal(originalRaw, preserved.Raw);
        Assert.Equal("preserved-original", preserved.BackupKey);
        Invoke(component, "RestoreDraft", scope, new WorkspaceBrowserSession("browser-client", "tab-session", null));
        var restored = Property<WorkspaceContentDraft>(component, "SelectedContent");
        Assert.Equal(entered.Title, restored.Title);
        Assert.Equal(text, restored.Content);
        Assert.True(restored.Dirty);
        Assert.Same(pending, Field<WorkspaceBrowserDraft>(component, "draft").Pending);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingCleanSelection_BecomesANewDraft_AndSuccessiveFieldEditsRetainTheirText(bool cachedCleanBuffer)
    {
        await using var component = new IdeaWorkspace();
        var previous = new WorkspaceBrowserDraft { SelectedComponentId = "removed-card", SelectedPlacementId = "removed-placement" };
        if (cachedCleanBuffer)
            previous.ContentDrafts["removed-card"] = new() { Key = "removed-card", ComponentId = "removed-card", Title = "Previously saved card", Content = CompleteText(), BaseRevision = 8 };
        Prepare(component, previous);

        Invoke(component, "EnsureSelectedDrafts");
        var selected = Property<WorkspaceContentDraft>(component, "SelectedContent");
        Assert.Null(Field<WorkspaceBrowserDraft>(component, "draft").SelectedComponentId);
        Assert.Null(Field<WorkspaceBrowserDraft>(component, "draft").SelectedPlacementId);
        Assert.Equal("$new", selected.Key);
        Assert.Null(selected.ComponentId);

        var title = selected with { Title = "  New title after collaborator removal Ω  ", Dirty = true };
        await (Task)Invoke(component, "ContentChanged", new WorkspaceUiScope("idea:11", 3), title)!;
        var content = Property<WorkspaceContentDraft>(component, "SelectedContent") with { Content = CompleteText(), Dirty = true };
        await (Task)Invoke(component, "ContentChanged", new WorkspaceUiScope("idea:11", 3), content)!;
        var final = Property<WorkspaceContentDraft>(component, "SelectedContent");
        Assert.Equal(title.Title, final.Title);
        Assert.Equal(CompleteText(), final.Content);
        Assert.Equal("$new", final.Key);
        if (cachedCleanBuffer) Assert.Same(previous.ContentDrafts["removed-card"], Field<WorkspaceBrowserDraft>(component, "draft").ContentDrafts["removed-card"]);
    }

    [Fact]
    public async Task MissingDirtySelection_RetainsTheCompleteDraftAndPendingOperation_UntilExplicitKeepAsNew()
    {
        await using var component = new IdeaWorkspace();
        var content = new WorkspaceContentDraft { Key = "removed-card", ComponentId = "removed-card", Title = "Unsaved title Ω", Content = CompleteText(), BaseRevision = 8, Dirty = true };
        var pending = new WorkspacePendingCommand(new("original-operation", 8, "update_component", JsonSerializer.SerializeToElement(new { componentId = "removed-card", content = content.Content }), "human"), "content:removed-card");
        Prepare(component, new()
        {
            SelectedComponentId = "removed-card", SelectedPlacementId = "removed-placement",
            ContentDrafts = new() { ["removed-card"] = content }, Pending = pending
        });

        Invoke(component, "EnsureSelectedDrafts");

        Assert.Equal("removed-card", Field<WorkspaceBrowserDraft>(component, "draft").SelectedComponentId);
        Assert.Same(content, Property<WorkspaceContentDraft>(component, "SelectedContent"));
        Assert.Same(pending, Field<WorkspaceBrowserDraft>(component, "draft").Pending);
        await (Task)Invoke(component, "ReapplyContent", new WorkspaceUiScope("idea:11", 3), "removed-card")!;
        var kept = Property<WorkspaceContentDraft>(component, "SelectedContent");
        Assert.Equal("$new", kept.Key);
        Assert.Null(kept.ComponentId);
        Assert.Equal(content.Title, kept.Title);
        Assert.Equal(content.Content, kept.Content);
        Assert.Equal(12, kept.BaseRevision);
        Assert.Same(pending, Field<WorkspaceBrowserDraft>(component, "draft").Pending);
    }

    [Fact]
    public async Task CopyPlan_RequiresExplicitConfirmation_AndCancelPreservesTheCompleteUnsavedPlan()
    {
        var context = Context();
        var draft = new WorkspacePlanDraft { Title = "Human unsaved title Ω", BaseRevision = 12, Items = [new() { Id = "human-item", Title = "Human step", Description = CompleteText(), AcceptanceCriteria = CompleteText() }] };
        var panel = new WorkspacePlanPanel();
        var changes = new List<WorkspacePlanDraft>();
        SetParameter(panel, "Context", context);
        SetParameter(panel, "Draft", draft);
        SetParameter(panel, "OnDraftChanged", EventCallback.Factory.Create<WorkspacePlanDraft>(this, (WorkspacePlanDraft value) => changes.Add(value)));
        Invoke(panel, "OnParametersSet");
        var source = new IdeaWorkspacePlanRevision { Id = "saved-plan", Version = 2, Title = "Saved plan", Items = [new() { Id = "saved-item", Title = "Saved step", Description = "Saved reasoning", AcceptanceCriteria = ["  Keep saved criterion Ω  ", ""] }] };

        await (Task)Invoke(panel, "CopyPlan", source)!;
        Assert.Empty(changes);
        Assert.Same(draft, Property<WorkspacePlanDraft>(panel, "editing"));
        Assert.Same(source, Field<IdeaWorkspacePlanRevision>(panel, "pendingCopy"));
        Invoke(panel, "CancelCopy");
        Assert.Null(Field<object?>(panel, "pendingCopy"));
        Assert.Empty(changes);
        Assert.Equal(CompleteText(), Property<WorkspacePlanDraft>(panel, "editing").Items[0].Description);
        Assert.Equal(CompleteText(), Property<WorkspacePlanDraft>(panel, "editing").Items[0].AcceptanceCriteria);

        await (Task)Invoke(panel, "CopyPlan", source)!;
        await (Task)Invoke(panel, "ConfirmCopy")!;
        var copied = Assert.Single(changes);
        Assert.Equal(source.Title, copied.Title);
        Assert.Equal(12, copied.BaseRevision);
        Assert.Equal("saved-item", Assert.Single(copied.Items).Id);
        Assert.Equal(source.Items[0].Description, copied.Items[0].Description);
        Assert.Equal(source.Items[0].AcceptanceCriteria, copied.Items[0].OriginalAcceptanceCriteria);
        Assert.Equal("  Keep saved criterion Ω  \n", copied.Items[0].AcceptanceCriteria);
        Assert.Null(Field<object?>(panel, "pendingCopy"));
    }

    [Fact]
    public async Task EditingAfterCopyRequest_CancelsReplacementAndPreservesTheLatestHumanDraft()
    {
        var panel = new WorkspacePlanPanel();
        var draft = new WorkspacePlanDraft { Title = "Unsaved", BaseRevision = 12, Items = [new() { Title = "Unsaved step", Description = CompleteText() }] };
        var changes = new List<WorkspacePlanDraft>();
        SetParameter(panel, "Context", Context());
        SetParameter(panel, "Draft", draft);
        SetParameter(panel, "OnDraftChanged", EventCallback.Factory.Create<WorkspacePlanDraft>(this, (WorkspacePlanDraft value) => changes.Add(value)));
        Invoke(panel, "OnParametersSet");
        await (Task)Invoke(panel, "CopyPlan", new IdeaWorkspacePlanRevision { Title = "Saved replacement" })!;

        await (Task)Invoke(panel, "TitleChanged", new ChangeEventArgs { Value = "Latest human edit Ω" })!;
        await (Task)Invoke(panel, "ConfirmCopy")!;

        var changed = Assert.Single(changes);
        Assert.Equal("Latest human edit Ω", changed.Title);
        Assert.Equal(CompleteText(), Assert.Single(changed.Items).Description);
        Assert.Null(Field<object?>(panel, "pendingCopy"));
    }

    // Invoke the real component state transitions without adding a renderer dependency. Actual DOM
    // input, confirmation and recovery controls are exercised separately by the browser regressions.
    private static void Prepare(IdeaWorkspace component, WorkspaceBrowserDraft draft)
    {
        SetField(component, "context", Context());
        SetField(component, "draft", draft);
        SetField(component, "workspaceId", "idea:11");
        SetField(component, "generation", 3);
    }
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    private static FieldInfo FieldInfo(object target, string name) => target.GetType().GetField(name, PrivateInstance)
        ?? throw new Xunit.Sdk.XunitException($"{target.GetType().Name} no longer has field {name}.");
    private static T Field<T>(object target, string name) => (T)FieldInfo(target, name).GetValue(target)!;
    private static void SetField(object target, string name, object value) => FieldInfo(target, name).SetValue(target, value);
    private static T Property<T>(object target, string name) => (T)(target.GetType().GetProperty(name, PrivateInstance)
        ?? throw new Xunit.Sdk.XunitException($"{target.GetType().Name} no longer has property {name}.")).GetValue(target)!;
    private static void SetParameter(object target, string name, object value) => (target.GetType().GetProperty(name)
        ?? throw new Xunit.Sdk.XunitException($"{target.GetType().Name} no longer has parameter {name}.")).SetValue(target, value);
    private static object? Invoke(object target, string name, params object[] args) => (target.GetType().GetMethod(name, PrivateInstance)
        ?? throw new Xunit.Sdk.XunitException($"{target.GetType().Name} no longer has method {name}.")).Invoke(target, args);
    private static string CompleteText() => "\r\n  ## Complete human draft Ω 漢字 🧭  \r\n" + new string('文', 40_000) + "\r\nFINAL PRESERVED REQUIREMENT  \r\n";
    private static IdeaWorkspaceContext Context()
    {
        var source = new IdeaWorkspaceSource("idea", 11, "Current idea", "Source", null, null, null, null, null, null, default, default);
        return new("idea:11", "idea", 11, source.Title, 12, source, source, false, null, false,
            "/ideas/workspace/idea/11", null, null, default, 42, new());
    }
}
