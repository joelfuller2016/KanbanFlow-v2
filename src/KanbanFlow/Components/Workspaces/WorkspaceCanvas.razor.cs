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

    protected override void OnParametersSet()
    {
        effectiveLayers.Clear();
        void AddGroup(string? parentId)
        {
            foreach (var placement in Document.Placements.Where(p => p.ParentId == parentId)
                .OrderBy(p => p.ZIndex).ThenBy(p => p.Order).ThenBy(p => p.Id, StringComparer.Ordinal))
            {
                if (!effectiveLayers.TryAdd(placement.Id, effectiveLayers.Count + 1)) continue;
                AddGroup(placement.Id);
            }
        }
        AddGroup(null);
    }

    private static string N(double value) => value.ToString(CultureInfo.InvariantCulture);
    private (double X, double Y) World(IdeaWorkspacePlacement placement)
    {
        var x = placement.X;
        var y = placement.Y;
        var visited = new HashSet<string> { placement.Id };
        var parentId = placement.ParentId;
        while (parentId is not null && visited.Add(parentId))
        {
            var parent = Document.Placements.FirstOrDefault(p => p.Id == parentId);
            if (parent is null) break;
            x += parent.X;
            y += parent.Y;
            parentId = parent.ParentId;
        }
        return (x, y);
    }
    private (double X, double Y)? Center(string componentId)
    {
        var placement = Document.Placements.FirstOrDefault(p => p.ComponentId == componentId);
        if (placement is null) return null;
        var world = World(placement);
        return (world.X + OriginX + placement.Width / 2, world.Y + OriginY + placement.Height / 2);
    }
    private double OriginX => 40 - Math.Min(0, Document.Placements.Select(p => World(p).X).DefaultIfEmpty().Min());
    private double OriginY => 40 - Math.Min(0, Document.Placements.Select(p => World(p).Y).DefaultIfEmpty().Min());
    private double CanvasWidth => Math.Max(1400, Document.Placements.Select(p => World(p).X + p.Width + OriginX + 100).DefaultIfEmpty().Max());
    private double CanvasHeight => Math.Max(850, Document.Placements.Select(p => World(p).Y + p.Height + OriginY + 100).DefaultIfEmpty().Max());
    // Dynamic geometry is expressed as CSS properties; presentation remains in the isolated stylesheet.
    private string CanvasStyle => $"--canvas-width:{N(CanvasWidth)}px;--canvas-height:{N(CanvasHeight)}px";
    private string PlacementStyle(IdeaWorkspacePlacement p)
    {
        var world = World(p);
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
