using System.Reflection;
using System.Text.Json;
using KanbanFlow.Components.Pages;
using KanbanFlow.Components.Workspaces;
using KanbanFlow.Models.IdeaWorkspaces;
using KanbanFlow.Services.IdeaWorkspaces;
using Microsoft.AspNetCore.Components;

namespace KanbanFlow.Tests;

public sealed class IdeaWorkspaceRemovedDraftTests
{
    [Fact]
    public void RecoveryList_IncludesEveryRemovedDirtyCard_WithoutChangingAnyAuthoredBuffer()
    {
        var text = CompleteText();
        var removed = Draft("removed-a", "  Removed draft Ω  ", text);
        var caseDistinct = Draft("LIVE", "Case-sensitive removed card", text + "SECOND COMPLETE BUFFER");
        var drafts = new Dictionary<string, WorkspaceContentDraft>
        {
            [removed.Key] = removed,
            [caseDistinct.Key] = caseDistinct,
            ["live"] = Draft("live", "Still saved", text),
            ["clean-removed"] = Draft("clean-removed", "Clean old content", text) with { Dirty = false },
            ["$new"] = new() { Title = "New card", Content = text, Dirty = true }
        };
        var document = new IdeaWorkspaceDocument { Components = [new() { Id = "live", Title = "Saved card" }] };
        var before = JsonSerializer.Serialize(drafts);
        var savedBefore = JsonSerializer.Serialize(document);
        var picker = Picker(document, drafts);

        var listed = Field<IReadOnlyList<WorkspaceContentDraft>>(picker, "removedDrafts");

        Assert.Equal([caseDistinct.Key, removed.Key], listed.Select(value => value.Key).OrderBy(value => value, StringComparer.Ordinal));
        Assert.Same(removed, listed.Single(value => value.Key == removed.Key));
        Assert.Same(caseDistinct, listed.Single(value => value.Key == caseDistinct.Key));
        Assert.Equal(text, listed.Single(value => value.Key == removed.Key).Content);
        Assert.Equal(before, JsonSerializer.Serialize(drafts));
        Assert.Equal(savedBefore, JsonSerializer.Serialize(document));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoverySelection_ExposesTheExistingKeepAsNewPath_AndPreservesOtherDraftsAndPendingOperations(bool hasPending)
    {
        var removed = Draft("removed-a", "  Full unsaved title Ω  ", CompleteText());
        var other = Draft("saved-b", "Other unsaved title", CompleteText() + "OTHER BUFFER");
        var layout = new WorkspaceLayoutDraft { PlacementId = "saved-placement", BaseRevision = 8, X = 45, Y = 70, Dirty = true };
        var pending = hasPending ? new WorkspacePendingCommand(new("immutable-operation", 8, "update_component",
            JsonSerializer.SerializeToElement(new { componentId = removed.ComponentId, content = removed.Content }), "human"), "content:removed-a") : null;
        var document = new IdeaWorkspaceDocument { Components = [new() { Id = other.ComponentId!, Title = "Saved B" }] };
        var previous = new WorkspaceBrowserDraft
        {
            SelectedComponentId = other.ComponentId,
            ContentDrafts = new() { [removed.Key] = removed, [other.Key] = other },
            LayoutDrafts = new() { [layout.PlacementId] = layout },
            Pending = pending
        };
        await using var workspace = new IdeaWorkspace();
        Prepare(workspace, previous, document);
        var picker = Picker(document, previous.ContentDrafts);
        SetParameter(picker, "OnSelect", EventCallback.Factory.Create<WorkspaceSelection>(this,
            selection => (Task)Invoke(workspace, "Select", new WorkspaceUiScope("idea:11", 3), selection)!));

        await (Task)Invoke(picker, "SelectDraft", removed)!;

        var selected = Property<WorkspaceContentDraft>(workspace, "SelectedContent");
        Assert.Equal(removed.ComponentId, Field<WorkspaceBrowserDraft>(workspace, "draft").SelectedComponentId);
        Assert.Same(removed, selected);
        Assert.Same(pending, Field<WorkspaceBrowserDraft>(workspace, "draft").Pending);
        Assert.Same(other, Field<WorkspaceBrowserDraft>(workspace, "draft").ContentDrafts[other.Key]);

        await (Task)Invoke(workspace, "ReapplyContent", new WorkspaceUiScope("idea:11", 3), selected.Key)!;

        var recovered = Property<WorkspaceContentDraft>(workspace, "SelectedContent");
        var current = Field<WorkspaceBrowserDraft>(workspace, "draft");
        Assert.Equal("$new", recovered.Key);
        Assert.Null(recovered.ComponentId);
        Assert.Equal(12, recovered.BaseRevision);
        Assert.Equal(removed.Title, recovered.Title);
        Assert.Equal(removed.Content, recovered.Content);
        Assert.Equal(removed.OriginalTitle, recovered.OriginalTitle);
        Assert.Equal(removed.OriginalContent, recovered.OriginalContent);
        Assert.Equal(removed.Type, recovered.Type);
        Assert.Equal(removed.Url, recovered.Url);
        Assert.True(recovered.Dirty);
        Assert.False(current.ContentDrafts.ContainsKey(removed.Key));
        Assert.Same(other, current.ContentDrafts[other.Key]);
        Assert.Same(layout, current.LayoutDrafts[layout.PlacementId]);
        Assert.Same(pending, current.Pending);
        SetParameter(picker, "Drafts", current.ContentDrafts);
        Invoke(picker, "OnParametersSet");
        Assert.Empty(Field<IReadOnlyList<WorkspaceContentDraft>>(picker, "removedDrafts"));
    }

    [Fact]
    public void RecoveryList_ReconcilesNewSavedDocumentsAndDiscardedBuffers_WithoutStaleEntries()
    {
        var removed = Draft("removed-a", "Removed", CompleteText());
        var picker = Picker(new(), new Dictionary<string, WorkspaceContentDraft> { [removed.Key] = removed });
        Assert.Same(removed, Assert.Single(Field<IReadOnlyList<WorkspaceContentDraft>>(picker, "removedDrafts")));

        SetParameter(picker, "Document", new IdeaWorkspaceDocument { Components = [new() { Id = removed.ComponentId!, Title = "Saved again" }] });
        Invoke(picker, "OnParametersSet");
        Assert.Empty(Field<IReadOnlyList<WorkspaceContentDraft>>(picker, "removedDrafts"));

        SetParameter(picker, "Document", new IdeaWorkspaceDocument());
        SetParameter(picker, "Drafts", new Dictionary<string, WorkspaceContentDraft> { [removed.Key] = removed with { Dirty = false } });
        Invoke(picker, "OnParametersSet");
        Assert.Empty(Field<IReadOnlyList<WorkspaceContentDraft>>(picker, "removedDrafts"));
    }

    private static WorkspaceRemovedDrafts Picker(IdeaWorkspaceDocument document, IReadOnlyDictionary<string, WorkspaceContentDraft> drafts)
    {
        var picker = new WorkspaceRemovedDrafts();
        SetParameter(picker, "Document", document);
        SetParameter(picker, "Drafts", drafts);
        Invoke(picker, "OnParametersSet");
        return picker;
    }

    private static WorkspaceContentDraft Draft(string id, string title, string text)
        => new() { Key = id, ComponentId = id, Title = title, Content = text, Type = "markdown", Url = "https://example.invalid/full-source",
            OriginalTitle = "  Original title  ", OriginalContent = CompleteText() + "ORIGINAL", BaseRevision = 8, Dirty = true };

    private static void Prepare(IdeaWorkspace component, WorkspaceBrowserDraft draft, IdeaWorkspaceDocument document)
    {
        var source = new IdeaWorkspaceSource("idea", 11, "Current idea", "Source", null, null, null, null, null, null, default, default);
        SetField(component, "context", new IdeaWorkspaceContext("idea:11", "idea", 11, source.Title, 12, source, source,
            false, null, false, "/ideas/workspace/idea/11", null, null, default, 42, document));
        SetField(component, "draft", draft);
        SetField(component, "workspaceId", "idea:11");
        SetField(component, "generation", 3);
    }

    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, PrivateInstance)!.GetValue(target)!;
    private static void SetField(object target, string name, object value) => target.GetType().GetField(name, PrivateInstance)!.SetValue(target, value);
    private static T Property<T>(object target, string name) => (T)target.GetType().GetProperty(name, PrivateInstance)!.GetValue(target)!;
    private static void SetParameter(object target, string name, object value) => target.GetType().GetProperty(name)!.SetValue(target, value);
    private static object? Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, PrivateInstance)!.Invoke(target, args);
    private static string CompleteText() => "\r\n  ## Complete removed-card draft Ω 漢字 🧭  \r\n" + new string('文', 40_000) + "\r\nFINAL COMPLETE REQUIREMENT  \r\n";
}
