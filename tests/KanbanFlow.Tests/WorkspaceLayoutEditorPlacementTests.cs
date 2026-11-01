using System.Reflection;
using System.Text.Json;
using KanbanFlow.Components.Workspaces;
using KanbanFlow.Models.IdeaWorkspaces;
using KanbanFlow.Services.IdeaWorkspaces;
using Microsoft.AspNetCore.Components;

namespace KanbanFlow.Tests;

public sealed class WorkspaceLayoutEditorPlacementTests
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly WorkspaceLayoutDraft First = new() { PlacementId = "appearance-a", BaseRevision = 12, X = 20, Y = 40, Order = 1, ZIndex = 2, Dirty = true };
    private static readonly WorkspaceLayoutDraft Second = new() { PlacementId = "appearance-b", BaseRevision = 12, X = 120, Y = 140, Width = 340, Height = 240, Order = 7, ZIndex = 3, Dirty = true };

    [Theory]
    [InlineData("X")]
    [InlineData("Y")]
    [InlineData("Width")]
    [InlineData("Height")]
    [InlineData("Order")]
    [InlineData("Layer")]
    [InlineData("Parent")]
    [InlineData("Apply")]
    [InlineData("Reapply")]
    [InlineData("Remove")]
    [InlineData("Add")]
    public async Task QueuedPlacementCallback_IgnoresOldAppearance_AndCurrentCallbackStillWorks(string control)
    {
        var fixture = new Fixture();
        var first = control == "Reapply" ? First with { BaseRevision = 11 } : First;
        var second = control == "Reapply" ? Second with { BaseRevision = 11 } : Second;
        fixture.Receive(first);
        var queued = Callback(fixture.Editor, control, first.PlacementId);

        fixture.Receive(second);
        await queued();

        Assert.Empty(fixture.Changes);
        Assert.Empty(fixture.Commands);
        Assert.Empty(fixture.Reapplied);
        Assert.Same(second, Property<WorkspaceLayoutDraft>(fixture.Editor, "editing"));
        await Callback(fixture.Editor, control, second.PlacementId)();

        var expected = control switch
        {
            "X" => second with { X = 42.5 }, "Y" => second with { Y = 42.5 },
            "Width" => second with { Width = 42.5 }, "Height" => second with { Height = 42.5 },
            "Order" => second with { Order = 42 }, "Layer" => second with { ZIndex = 42 },
            "Parent" => second with { ParentId = "frame-appearance" }, _ => null
        };
        if (expected is not null)
        {
            Assert.Equal(expected, Assert.Single(fixture.Changes));
            Assert.Empty(fixture.Commands);
            Assert.Empty(fixture.Reapplied);
        }
        else if (control == "Reapply")
        {
            Assert.Equal([second.PlacementId], fixture.Reapplied);
            Assert.Empty(fixture.Changes);
            Assert.Empty(fixture.Commands);
        }
        else
        {
            Assert.Empty(fixture.Changes);
            Assert.Empty(fixture.Reapplied);
            var command = Assert.Single(fixture.Commands);
            Assert.Equal(control switch { "Apply" => "move_components", "Remove" => "remove_placement", _ => "add_placement" }, command.Action);
            Assert.Equal(12, command.ExpectedRevision);
            Assert.Equal(control == "Add" ? null : "layout:" + second.PlacementId, command.DraftSection);
            if (control == "Apply")
            {
                var placement = Assert.Single(command.Payload.GetProperty("placements").EnumerateArray());
                Assert.Equal(second.PlacementId, placement.GetProperty("placementId").GetString());
                Assert.Equal(second.X, placement.GetProperty("x").GetDouble());
                Assert.Equal(second.Y, placement.GetProperty("y").GetDouble());
                Assert.Equal(second.Width, placement.GetProperty("width").GetDouble());
                Assert.Equal(second.Height, placement.GetProperty("height").GetDouble());
                Assert.Equal(JsonValueKind.Null, placement.GetProperty("parentId").ValueKind);
                Assert.Equal(second.Order, placement.GetProperty("order").GetInt32());
                Assert.Equal(second.ZIndex, placement.GetProperty("zIndex").GetInt32());
            }
            else if (control == "Remove") Assert.Equal(second.PlacementId, command.Payload.GetProperty("placementId").GetString());
            else
            {
                var placement = command.Payload.GetProperty("placement");
                Assert.Equal("shared-card", placement.GetProperty("componentId").GetString());
                Assert.Equal(152, placement.GetProperty("x").GetInt32());
                Assert.Equal(152, placement.GetProperty("y").GetInt32());
                Assert.Equal(320, placement.GetProperty("width").GetInt32());
                Assert.Equal(220, placement.GetProperty("height").GetInt32());
                Assert.Equal(6, placement.GetProperty("zIndex").GetInt32());
            }
        }
    }

    [Fact]
    public async Task AddAppearance_RejectsCallbacksAcrossEmptySelection_AndAllowsTheCurrentEmptySelection()
    {
        var fixture = new Fixture();
        fixture.Receive(null);
        var withoutSelection = Callback(fixture.Editor, "Add", null);
        fixture.Receive(Second);

        await withoutSelection();
        Assert.Empty(fixture.Commands);
        var withSelection = Callback(fixture.Editor, "Add", Second.PlacementId);
        await withSelection();
        Assert.Equal("add_placement", Assert.Single(fixture.Commands).Action);

        fixture.Receive(null);
        await withSelection();
        Assert.Single(fixture.Commands);
        await Callback(fixture.Editor, "Add", null)();
        Assert.Equal(2, fixture.Commands.Count);
        Assert.All(fixture.Commands, command => Assert.Equal("shared-card", command.Payload.GetProperty("placement").GetProperty("componentId").GetString()));
        Assert.Empty(fixture.Changes);
        Assert.Empty(fixture.Reapplied);
    }

    [Fact]
    public async Task AppearanceSelector_CanSwitchBetweenPlacementsOfTheSameComponent()
    {
        var fixture = new Fixture();
        fixture.Receive(First);
        await (Task)Invoke(fixture.Editor, "SelectPlacement", new ChangeEventArgs { Value = Second.PlacementId })!;
        fixture.Receive(Second);
        await (Task)Invoke(fixture.Editor, "SelectPlacement", new ChangeEventArgs { Value = First.PlacementId })!;

        Assert.Equal([new WorkspaceSelection("shared-card", Second.PlacementId), new WorkspaceSelection("shared-card", First.PlacementId)], fixture.Selections);
        Assert.Empty(fixture.Changes);
        Assert.Empty(fixture.Commands);
    }

    private static Func<Task> Callback(WorkspaceLayoutEditor editor, string control, string? placementId)
        => control switch
        {
            "X" or "Y" or "Width" or "Height" => () => (Task)Invoke(editor, "NumberChanged", new ChangeEventArgs { Value = "42.5" }, control, placementId)!,
            "Order" or "Layer" => () => (Task)Invoke(editor, control + "Changed", new ChangeEventArgs { Value = "42" }, placementId)!,
            "Parent" => () => (Task)Invoke(editor, "ParentChanged", new ChangeEventArgs { Value = "frame-appearance" }, placementId)!,
            _ => () => (Task)Invoke(editor, control, placementId)!
        };

    private static object? Invoke(object target, string name, params object?[] args)
        => (target.GetType().GetMethod(name, Flags) ?? throw new Xunit.Sdk.XunitException($"Missing method {name}")).Invoke(target, args);
    private static void Set(object target, string name, object? value)
        => (target.GetType().GetProperty(name, Flags) ?? throw new Xunit.Sdk.XunitException($"Missing property {name}")).SetValue(target, value);
    private static T Property<T>(object target, string name)
        => (T)(target.GetType().GetProperty(name, Flags) ?? throw new Xunit.Sdk.XunitException($"Missing property {name}")).GetValue(target)!;

    private sealed class Fixture
    {
        public WorkspaceLayoutEditor Editor { get; } = new();
        public List<WorkspaceLayoutDraft> Changes { get; } = [];
        public List<WorkspaceUiCommand> Commands { get; } = [];
        public List<string> Reapplied { get; } = [];
        public List<WorkspaceSelection> Selections { get; } = [];

        public Fixture()
        {
            var source = new IdeaWorkspaceSource("idea", 11, "Placement race", "Source", null, null, null, null, null, null, default, default);
            var document = new IdeaWorkspaceDocument { Components = [new() { Id = "shared-card" }, new() { Id = "frame", Type = "frame" }],
                Placements = [new() { Id = First.PlacementId, ComponentId = "shared-card", ZIndex = 2 },
                    new() { Id = Second.PlacementId, ComponentId = "shared-card", ZIndex = 3 }, new() { Id = "frame-appearance", ComponentId = "frame", ZIndex = 5 }] };
            Set(Editor, "Context", new IdeaWorkspaceContext("idea:11", "idea", 11, source.Title, 12, source, source, false, null, false,
                "/ideas/workspace/idea/11", null, null, default, 42, document));
            Set(Editor, "ComponentId", "shared-card");
            Set(Editor, "OnDraftChanged", EventCallback.Factory.Create<WorkspaceLayoutDraft>(this, (WorkspaceLayoutDraft value) => { Changes.Add(value); }));
            Set(Editor, "OnCommand", EventCallback.Factory.Create<WorkspaceUiCommand>(this, (WorkspaceUiCommand value) => { Commands.Add(value); }));
            Set(Editor, "OnReapply", EventCallback.Factory.Create<string>(this, (string value) => { Reapplied.Add(value); }));
            Set(Editor, "OnSelect", EventCallback.Factory.Create<WorkspaceSelection>(this, (WorkspaceSelection value) => { Selections.Add(value); }));
        }

        public void Receive(WorkspaceLayoutDraft? value) { Set(Editor, "Draft", value); Invoke(Editor, "OnParametersSet"); }
    }
}
