using System.Reflection;
using System.Text.Json;
using KanbanFlow.Components.Workspaces;
using KanbanFlow.Models.IdeaWorkspaces;

namespace KanbanFlow.Tests;

public sealed class IdeaWorkspaceCanvasTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void NestedPlacements_PreserveWorldCoordinatesBoundsLayersAndFirstAppearanceCenters()
    {
        var document = new IdeaWorkspaceDocument
        {
            Components = [new() { Id = "frame", Type = "frame" }, new() { Id = "note", Title = "Authored card", Content = "  Full content Ω\r\n" }],
            Placements =
            [
                new() { Id = "leaf", ComponentId = "note", ParentId = "inner", X = 15, Y = 25, Order = 0, ZIndex = -9 },
                new() { Id = "second", ComponentId = "note", X = 1600, Y = 20, Width = 80, Height = 50, Order = 3 },
                new() { Id = "inner", ComponentId = "frame", ParentId = "root", X = 20, Y = 30, Width = 500, Height = 400, Order = 5, ZIndex = 99 },
                new() { Id = "root", ComponentId = "frame", X = -120, Y = -70, Width = 2000, Height = 1000, Order = 2, ZIndex = 10 },
                new() { Id = "other", ComponentId = "note", X = 300, Y = 600, Width = 100, Height = 100, Order = 1, ZIndex = 20 }
            ]
        };
        var before = JsonSerializer.Serialize(document);
        var canvas = new WorkspaceCanvas();

        Receive(canvas, document);

        Assert.Equal((-85d, -15d), Invoke<(double, double)>(canvas, "World", document.Placements[0]));
        Assert.Equal((215d, 185d), Invoke<(double, double)?>(canvas, "Center", "note"));
        Assert.Null(Invoke<(double, double)?>(canvas, "Center", "unplaced"));
        Assert.Equal(160, Property<double>(canvas, "OriginX"));
        Assert.Equal(110, Property<double>(canvas, "OriginY"));
        Assert.Equal(2140, Property<double>(canvas, "CanvasWidth"));
        Assert.Equal(1140, Property<double>(canvas, "CanvasHeight"));
        Assert.Equal(new[] { "leaf", "other", "root", "second", "inner" }, Field<IdeaWorkspacePlacement[]>(canvas, "orderedPlacements").Select(p => p.Id));
        var layers = Field<Dictionary<string, int>>(canvas, "effectiveLayers");
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, new[] { layers["second"], layers["root"], layers["inner"], layers["leaf"], layers["other"] });
        Assert.Equal("--card-x:75px;--card-y:95px;--card-width:280px;--card-height:180px;--card-layer:4", Invoke<string>(canvas, "PlacementStyle", document.Placements[0]));
        Assert.Equal(before, JsonSerializer.Serialize(document));
    }

    [Fact]
    public void NewParameterDocument_RebuildsCoordinatesAndRemovesPriorComponentAndPlacementLookups()
    {
        var canvas = new WorkspaceCanvas();
        Receive(canvas, new()
        {
            Components = [new() { Id = "old" }],
            Placements = [new() { Id = "old-place", ComponentId = "old", X = -5000, Y = -6000 }]
        });

        Receive(canvas, new()
        {
            Components = [new() { Id = "current" }],
            Placements = [new() { Id = "current-place", ComponentId = "current", X = 1500, Y = 1000, Width = 300, Height = 200 }]
        });

        Assert.Equal(40, Property<double>(canvas, "OriginX"));
        Assert.Equal(40, Property<double>(canvas, "OriginY"));
        Assert.Equal(1940, Property<double>(canvas, "CanvasWidth"));
        Assert.Equal(1340, Property<double>(canvas, "CanvasHeight"));
        Assert.Null(Invoke<(double, double)?>(canvas, "Center", "old"));
        Assert.Equal((1690d, 1140d), Invoke<(double, double)?>(canvas, "Center", "current"));
        Assert.Equal("current", Assert.Single(Field<Dictionary<string, IdeaWorkspaceComponent>>(canvas, "componentsById")).Key);
        Assert.Equal("current-place", Assert.Single(Field<Dictionary<string, IdeaWorkspacePlacement>>(canvas, "placementsById")).Key);
        Assert.Equal("current-place", Assert.Single(Field<Dictionary<string, int>>(canvas, "effectiveLayers")).Key);
    }

    [Fact]
    public void EmptyCanvas_UsesMinimumBoundsAndHasNoStaleGeometry()
    {
        var canvas = new WorkspaceCanvas();
        Receive(canvas, new()
        {
            Components = [new() { Id = "old" }],
            Placements = [new() { Id = "old-place", ComponentId = "old", X = -5000, Y = 3000 }]
        });

        Receive(canvas, new());

        Assert.Equal(40, Property<double>(canvas, "OriginX"));
        Assert.Equal(40, Property<double>(canvas, "OriginY"));
        Assert.Equal(1400, Property<double>(canvas, "CanvasWidth"));
        Assert.Equal(850, Property<double>(canvas, "CanvasHeight"));
        Assert.Empty(Field<Dictionary<string, (double X, double Y)>>(canvas, "worldPositions"));
        Assert.Empty(Field<IdeaWorkspacePlacement[]>(canvas, "orderedPlacements"));
        Assert.Null(Invoke<(double, double)?>(canvas, "Center", "old"));
    }

    private static void Receive(WorkspaceCanvas canvas, IdeaWorkspaceDocument document)
    {
        typeof(WorkspaceCanvas).GetProperty(nameof(WorkspaceCanvas.Document))!.SetValue(canvas, document);
        Invoke<object?>(canvas, "OnParametersSet");
    }

    private static T Invoke<T>(WorkspaceCanvas canvas, string name, params object[] arguments)
        => (T)typeof(WorkspaceCanvas).GetMethod(name, PrivateInstance)!.Invoke(canvas, arguments)!;

    private static T Property<T>(WorkspaceCanvas canvas, string name)
        => (T)typeof(WorkspaceCanvas).GetProperty(name, PrivateInstance)!.GetValue(canvas)!;

    private static T Field<T>(WorkspaceCanvas canvas, string name)
        => (T)typeof(WorkspaceCanvas).GetField(name, PrivateInstance)!.GetValue(canvas)!;
}
