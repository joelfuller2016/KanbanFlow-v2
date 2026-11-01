using KanbanFlow.Services.IdeaWorkspaces;
using KanbanFlow.Components.Workspaces;

namespace KanbanFlow.Components.Pages;

public partial class IdeaGallery : IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private bool disposed;
    private IdeaWorkspacePage<IdeaWorkspaceSummary>? page;
    private readonly List<IdeaWorkspaceSummary> items = [];
    private string? query, kind, status, error;
    private bool showCreate, busy, interactiveReady;
    private string? activeQuery, activeKind, activeStatus;

    protected override Task OnInitializedAsync() => Reload();

    protected override Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender) { interactiveReady = true; StateHasChanged(); }
        return Task.CompletedTask;
    }

    private async Task Reload()
    {
        if (busy) return;
        busy = true;
        error = null;
        try
        {
            var newActiveQuery = string.IsNullOrWhiteSpace(query) ? null : query;
            var newActiveKind = string.IsNullOrEmpty(kind) ? null : kind;
            var newActiveStatus = string.IsNullOrEmpty(status) ? null : status;
            var loaded = await Workspaces.ListAsync(newActiveKind, newActiveStatus, newActiveQuery, ct: lifetime.Token);
            if (disposed) return;
            activeQuery = newActiveQuery;
            activeKind = newActiveKind;
            activeStatus = newActiveStatus;
            page = loaded;
            items.Clear();
            items.AddRange(page.Items);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (!disposed) error = ex.Message; }
        finally { busy = false; }
    }

    private async Task More()
    {
        if (busy || page?.NextCursor is null) return;
        busy = true;
        error = null;
        try
        {
            var loaded = await Workspaces.ListAsync(activeKind, activeStatus, activeQuery, page.NextCursor, ct: lifetime.Token);
            if (disposed) return;
            page = loaded;
            items.AddRange(page.Items);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (!disposed) error = ex.Message; }
        finally { busy = false; }
    }

    private async Task Create(IdeaWorkspaceCreate request)
    {
        if (busy) return;
        busy = true;
        error = null;
        try
        {
            var created = await Workspaces.CreateAsync(request, lifetime.Token);
            if (!disposed) Navigation.NavigateTo(WorkspaceUiText.Url(created.WorkspaceId));
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (!disposed) error = ex.Message; }
        finally { busy = false; }
    }

    public void Dispose()
    {
        disposed = true;
        lifetime.Cancel();
        lifetime.Dispose();
    }
}
