using KanbanFlow.Services.IdeaWorkspaces;
using KanbanFlow.Components.Workspaces;

namespace KanbanFlow.Components.Pages;

public partial class IdeaGallery
{
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
            activeQuery = string.IsNullOrWhiteSpace(query) ? null : query;
            activeKind = string.IsNullOrEmpty(kind) ? null : kind;
            activeStatus = string.IsNullOrEmpty(status) ? null : status;
            page = await Workspaces.ListAsync(activeKind, activeStatus, activeQuery);
            items.Clear();
            items.AddRange(page.Items);
        }
        catch (Exception ex) { error = ex.Message; }
        finally { busy = false; }
    }

    private async Task More()
    {
        if (busy || page?.NextCursor is null) return;
        busy = true;
        try
        {
            page = await Workspaces.ListAsync(activeKind, activeStatus, activeQuery, page.NextCursor);
            items.AddRange(page.Items);
        }
        catch (Exception ex) { error = ex.Message; }
        finally { busy = false; }
    }

    private async Task Create(IdeaWorkspaceCreate request)
    {
        if (busy) return;
        busy = true;
        error = null;
        try
        {
            var created = await Workspaces.CreateAsync(request);
            Navigation.NavigateTo(WorkspaceUiText.Url(created.WorkspaceId));
        }
        catch (Exception ex) { error = ex.Message; }
        finally { busy = false; }
    }
}
