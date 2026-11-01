using System.Reflection;
using System.Text.Json;
using KanbanFlow.Components.Pages;
using KanbanFlow.Components.Workspaces;
using KanbanFlow.Services.IdeaWorkspaces;

namespace KanbanFlow.Tests;

public sealed class IdeaWorkspaceAcknowledgmentTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("content:card")]
    [InlineData("content:$new")]
    [InlineData("content:")]
    [InlineData("layout:appearance")]
    [InlineData("discussion")]
    [InlineData("connector")]
    [InlineData("plan")]
    public void LegacyAcknowledgmentWithoutSendSnapshot_PreservesEveryCompleteCurrentBuffer(string section)
    {
        var draft = CurrentDraft();
        var pending = new WorkspacePendingCommand(new("original-operation", 7, "update_component",
            JsonSerializer.SerializeToElement(new { componentId = "card", content = "Earlier submitted content" }), "human"), section);
        draft = draft with { Pending = pending };
        draft.Validate();
        var before = JsonSerializer.Serialize(draft, Json);

        var acknowledged = Clear(draft, pending);

        Assert.Equal(before, JsonSerializer.Serialize(acknowledged, Json));
        Assert.Same(draft, acknowledged);
        Assert.Same(pending, acknowledged.Pending);
        Assert.Equal(CompleteText("new ordinary card text"), acknowledged.ContentDrafts["card"].Content);
        Assert.Equal(CompleteText("independent unsaved new card"), acknowledged.ContentDrafts["$new"].Content);
        Assert.Equal(CompleteText("new discussion"), acknowledged.Discussion.Text);
    }

    [Fact]
    public void MatchingSendSnapshot_ClearsOnlyTheAcknowledgedCard_AndKeepsEveryIndependentSection()
    {
        var draft = CurrentDraft();
        var sent = draft.ContentDrafts["card"];
        var pending = new WorkspacePendingCommand(new("original-operation", 7, "update_component",
            JsonSerializer.SerializeToElement(new { componentId = "card", content = sent.Content }), "human"), "content:card",
            JsonSerializer.SerializeToElement(sent, Json));
        var before = JsonSerializer.Serialize(draft, Json);

        var acknowledged = Clear(draft, pending);

        Assert.False(acknowledged.ContentDrafts.ContainsKey("card"));
        Assert.Equal(2, acknowledged.ContentDrafts.Count);
        Assert.Same(draft.ContentDrafts["$new"], acknowledged.ContentDrafts["$new"]);
        Assert.Same(draft.ContentDrafts[""], acknowledged.ContentDrafts[""]);
        Assert.Same(draft.LayoutDrafts, acknowledged.LayoutDrafts);
        Assert.Same(draft.Discussion, acknowledged.Discussion);
        Assert.Same(draft.Connector, acknowledged.Connector);
        Assert.Same(draft.Plan, acknowledged.Plan);
        Assert.Same(draft.Checkpoint, acknowledged.Checkpoint);
        Assert.Same(draft.Delivery, acknowledged.Delivery);
        Assert.Equal(before, JsonSerializer.Serialize(draft, Json));
    }

    private static WorkspaceBrowserDraft CurrentDraft() => new()
    {
        SelectedComponentId = "card", SelectedPlacementId = "appearance",
        ContentDrafts = new() {
            ["card"] = new() { Key = "card", ComponentId = "card", Title = "Newer ordinary title Ω", Content = CompleteText("new ordinary card text"), Dirty = true },
            ["$new"] = new() { Title = "Independent new title", Content = CompleteText("independent unsaved new card"), Dirty = true },
            [""] = new() { Key = "", ComponentId = "$new", Title = "Saved literal-ID card", Content = CompleteText("independent saved literal card"), Dirty = true } },
        LayoutDrafts = new() { ["appearance"] = new() { PlacementId = "appearance", X = 117.5, Y = 83.25, Dirty = true } },
        Discussion = new(CompleteText("new discussion")), Connector = new("card", "$new", CompleteText("new connector label")),
        Plan = new() { Title = "Newer project plan", BaseRevision = 7, Items = [new() { Id = "step", Title = "Complete step", Description = CompleteText("new plan"), AcceptanceCriteria = CompleteText("new criteria") }] },
        Checkpoint = new() { Objective = CompleteText("resume objective"), NextActions = CompleteText("next actions") },
        Delivery = new() { ProjectName = "New project name", FeatureName = "Newer feature name" }
    };
    private static WorkspaceBrowserDraft Clear(WorkspaceBrowserDraft draft, WorkspacePendingCommand pending)
        => (WorkspaceBrowserDraft)typeof(IdeaWorkspace).GetMethod("ClearSavedSection", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [draft, pending])!;
    private static string CompleteText(string label) => $"\r\n  ## {label} Ω 漢字 🧭  \r\n" + new string('文', 10_000) + "\r\nFINAL COMPLETE REQUIREMENT  \r\n";
}
