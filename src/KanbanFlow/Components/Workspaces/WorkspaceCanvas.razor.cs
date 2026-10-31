using System.Globalization;
using System.Text.Json;
using KanbanFlow.Models.IdeaWorkspaces;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace KanbanFlow.Components.Workspaces;

public partial class WorkspaceCanvas
{
    [Parameter, EditorRequired] public string WorkspaceId { get; set; } = "";
    [Parameter, EditorRequired] public IdeaWorkspaceDocument Document { get; set; } = default!;
    [Parameter] public long Revision { get; set; }
    [Parameter] public string? SelectedPlacementId { get; set; }
    [Parameter] public bool Busy { get; set; }
    [Parameter] public EventCallback<WorkspaceSelection> OnSelect { get; set; }
    [Parameter] public EventCallback<WorkspaceUiCommand> OnCommand { get; set; }
    private ElementReference canvasElement;
    private WorkspaceCanvasInterop? interop;
    private DotNetObjectReference<WorkspaceCanvas>? dotNet;
    private readonly Dictionary<string, int> effectiveLayers = [];
    private readonly Dictionary<string, IdeaWorkspaceComponent> componentsById = [];
    private readonly Dictionary<string, IdeaWorkspacePlacement> placementsById = [];
    private readonly Dictionary<string, IdeaWorkspacePlacement> firstPlacementByComponentId = [];
    private readonly Dictionary<string, (double X, double Y)> worldPositions = [];
    private IdeaWorkspacePlacement[] orderedPlacements = [];
    private double OriginX { get; set; }
    private double OriginY { get; set; }
    private double CanvasWidth { get; set; }
    private double CanvasHeight { get; set; }

    protected override void OnParametersSet()
    {
        effectiveLayers.Clear();
        componentsById.Clear();
        placementsById.Clear();
        firstPlacementByComponentId.Clear();
        worldPositions.Clear();
        foreach (var component in Document.Components) componentsById.Add(component.Id, component);
        foreach (var placement in Document.Placements)
        {
            placementsById.Add(placement.Id, placement);
            firstPlacementByComponentId.TryAdd(placement.ComponentId, placement);
        }
        foreach (var placement in Document.Placements)
        {
            var path = new List<IdeaWorkspacePlacement>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var current = placement;
            (double X, double Y) world;
            while (!worldPositions.TryGetValue(current.Id, out world))
            {
                if (!visited.Add(current.Id)) throw new InvalidOperationException("Workspace canvas placements contain a parent cycle.");
                path.Add(current);
                if (current.ParentId is null) break;
                current = placementsById[current.ParentId];
            }
            for (var index = path.Count - 1; index >= 0; index--)
            {
                current = path[index];
                world = (world.X + current.X, world.Y + current.Y);
                worldPositions.Add(current.Id, world);
            }
        }
        OriginX = 40 - Math.Min(0, worldPositions.Values.Select(p => p.X).DefaultIfEmpty().Min());
        OriginY = 40 - Math.Min(0, worldPositions.Values.Select(p => p.Y).DefaultIfEmpty().Min());
        CanvasWidth = Math.Max(1400, Document.Placements.Select(p => worldPositions[p.Id].X + p.Width + OriginX + 100).DefaultIfEmpty().Max());
        CanvasHeight = Math.Max(850, Document.Placements.Select(p => worldPositions[p.Id].Y + p.Height + OriginY + 100).DefaultIfEmpty().Max());
        orderedPlacements = Document.Placements.OrderBy(p => p.Order).ThenBy(p => p.Id, StringComparer.Ordinal).ToArray();

        var groups = Document.Placements.OrderBy(p => p.ZIndex).ThenBy(p => p.Order)
            .ThenBy(p => p.Id, StringComparer.Ordinal).ToLookup(p => p.ParentId);
        var pending = new Stack<IdeaWorkspacePlacement>(groups[null].Reverse());
        while (pending.TryPop(out var layerPlacement))
        {
            if (!effectiveLayers.TryAdd(layerPlacement.Id, effectiveLayers.Count + 1)) continue;
            foreach (var child in groups[layerPlacement.Id].Reverse()) pending.Push(child);
        }
    }

    private static string N(double value) => value.ToString(CultureInfo.InvariantCulture);
    private (double X, double Y) World(IdeaWorkspacePlacement placement)
        => worldPositions[placement.Id];
    private (double X, double Y)? Center(string componentId)
    {
        if (!firstPlacementByComponentId.TryGetValue(componentId, out var placement)) return null;
        var world = World(placement);
        return (world.X + OriginX + placement.Width / 2, world.Y + OriginY + placement.Height / 2);
    }
    // Dynamic geometry is expressed as CSS properties; presentation remains in the isolated stylesheet.
    private string CanvasStyle => $"--canvas-width:{N(CanvasWidth)}px;--canvas-height:{N(CanvasHeight)}px";
    private string PlacementStyle(IdeaWorkspacePlacement p)
    {
        var world = worldPositions[p.Id];
        return $"--card-x:{N(world.X + OriginX)}px;--card-y:{N(world.Y + OriginY)}px;--card-width:{N(p.Width)}px;--card-height:{N(p.Height)}px;--card-layer:{effectiveLayers.GetValueOrDefault(p.Id, 1)}";
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        interop = new(JS);
        dotNet = DotNetObjectReference.Create(this);
        await interop.InitializeAsync(canvasElement, dotNet);
    }

    [JSInvokable]
    public async Task ApplyCanvasLayout(string sourceWorkspaceId, JsonElement placements, long expectedRevision)
    {
        await InvokeAsync(async () =>
        {
            if (Busy || sourceWorkspaceId != WorkspaceId) return;
            await OnCommand.InvokeAsync(new("move_components", JsonSerializer.SerializeToElement(new { placements }, WorkspaceUiCommand.Json), expectedRevision, WorkspaceId: sourceWorkspaceId));
            StateHasChanged();
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (interop is not null) await interop.DisposeAsync();
        dotNet?.Dispose();
    }
}
