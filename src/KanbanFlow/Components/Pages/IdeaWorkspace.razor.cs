using KanbanFlow.Components.Workspaces;
using KanbanFlow.Services.IdeaWorkspaces;
using Microsoft.AspNetCore.Components;

namespace KanbanFlow.Components.Pages;

public partial class IdeaWorkspace
{
    [Parameter] public string Kind { get; set; } = "idea";
    [Parameter] public int SourceId { get; set; }
    [SupplyParameterFromQuery(Name = "view")] public string? RequestedView { get; set; }
    private IdeaWorkspaceContext? context, latestContext;
    private IdeaWorkspaceComponentDetail? selectedDetail;
    private WorkspaceBrowserDraft draft = new();
    private readonly Dictionary<string, WorkspaceBrowserDraft> browserCache = [];
    private readonly List<IdeaWorkspaceSummary> catalog = [];
    private IReadOnlyList<string> componentTypes = [];
    private string workspaceId = "", clientId = "", sessionId = "";
    private string? error, notice, storageError;
    private string view = "canvas", panel = "plan";
    private bool busy, disposed, storageHealthy = true;
    private int generation, restoredGeneration = -1, restoringGeneration = -1;
    private long persistenceVersion;
    private readonly CancellationTokenSource lifetime = new();
    private Task? pollTask;
    private WorkspaceBrowserInterop? browser;
    private bool Ready => context is not null && restoredGeneration == generation && clientId.Length > 0;
    private WorkspaceUiScope CurrentScope => new(workspaceId, generation);
    private bool IsCurrent(WorkspaceUiScope scope) => !disposed && scope.WorkspaceId == workspaceId && scope.Generation == generation;
    private bool ControlsDisabled => !Ready || !storageHealthy || busy || draft.Pending is not null || latestContext is not null;
    private static readonly KeyValuePair<string, string>[] PanelNames = [new("plan", "Plan"), new("delivery", "Project and Kanban"), new("discussion", "Discussion"), new("connections", "Connections"), new("resume", "Resume"), new("source", "Source documents")];

    protected override async Task OnParametersSetAsync()
    {
        if (RequestedView is "plan" or "delivery" or "discussion" or "connections" or "resume" or "source") panel = RequestedView;
        else if (RequestedView is "canvas" or "outline") view = RequestedView;
        var next = $"{Kind.ToLowerInvariant()}:{SourceId}";
        if (next == workspaceId) return;
        var currentGeneration = ++generation;
        workspaceId = next;
        restoredGeneration = -1;
        context = latestContext = null;
        selectedDetail = null;
        ClearDeliveryView();
        error = notice = null;
        draft = browserCache.GetValueOrDefault(next) ?? new();
        try
        {
            var loaded = await Workspaces.GetAsync(next, lifetime.Token);
            if (currentGeneration != generation) return;
            context = loaded;
            componentTypes = Workspaces.GetCapabilities().ComponentTypes;
            if (catalog.Count == 0) await LoadCatalog();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (currentGeneration == generation) error = ex.Message; }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (disposed || context is null || restoredGeneration == generation || restoringGeneration == generation) return;
        var scope = CurrentScope;
        restoringGeneration = scope.Generation;
        try
        {
            browser ??= new(JS);
            var session = await browser.InitializeAsync(scope.WorkspaceId);
            if (!IsCurrent(scope)) return;
            clientId = session.ClientId;
            sessionId = session.SessionId;
            draft = browserCache.GetValueOrDefault(scope.WorkspaceId) ?? session.Draft ?? DraftFromCheckpoint();
            EnsureSelectedDrafts();
            restoredGeneration = scope.Generation;
            await ReadSelectedDetail(scope);
            if (!IsCurrent(scope)) return;
            await PersistDraft(scope);
            if (!IsCurrent(scope)) return;
            pollTask ??= PollForChanges();
            StateHasChanged();
        }
        catch (Exception ex)
        {
            if (!IsCurrent(scope)) return;
            storageHealthy = false;
            storageError = $"Browser draft storage is unavailable: {ex.Message}. Keep this view open to retain any unsaved text.";
            // Do not retry during every render or enable mutations without recoverable operation storage.
            restoredGeneration = scope.Generation;
            clientId = "";
            StateHasChanged();
        }
        finally { if (restoringGeneration == scope.Generation) restoringGeneration = -1; }
    }

    private async Task LoadCatalog()
    {
        var loaded = new List<IdeaWorkspaceSummary>();
        string? cursor = null;
        do
        {
            var page = await Workspaces.ListAsync(cursor: cursor, limit: 500, ct: lifetime.Token);
            loaded.AddRange(page.Items);
            cursor = page.NextCursor;
        } while (cursor is not null);
        catalog.Clear();
        catalog.AddRange(loaded);
    }

    private async Task SwitchWorkspace(WorkspaceUiScope scope, ChangeEventArgs args)
    {
        var key = args.Value?.ToString();
        if (!IsCurrent(scope) || string.IsNullOrEmpty(key) || key == workspaceId || busy) return;
        if (!await PersistDraft(scope) || !IsCurrent(scope) || busy) return;
        Navigation.NavigateTo(WorkspaceUiText.Url(key));
    }

    private async Task PollForChanges()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(12), lifetime.Token);
                if (context is null || busy || !Ready) continue;
                var scope = CurrentScope;
                try
                {
                    var next = await Workspaces.GetAsync(scope.WorkspaceId, lifetime.Token);
                    if (!IsCurrent(scope)) continue;
                    if (next.Revision != context.Revision || next.Source != context.Source ||
                        next.ProjectPlanRevision != context.ProjectPlanRevision || next.ProjectSource != context.ProjectSource ||
                        next.ProjectMissing != context.ProjectMissing)
                        await InvokeAsync(() => { if (IsCurrent(scope)) { latestContext = next; StateHasChanged(); } });
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    await InvokeAsync(() => { if (IsCurrent(scope)) { error = $"Checking for external changes failed: {ex.Message}. Use Refresh saved context to retry."; StateHasChanged(); } });
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private async Task Refresh(WorkspaceUiScope scope)
    {
        if (!IsCurrent(scope) || busy || context is null) return;
        try
        {
            var refreshed = await Workspaces.GetAsync(scope.WorkspaceId, lifetime.Token);
            if (!IsCurrent(scope)) return;
            latestContext = refreshed;
            await AdoptLatest(scope);
        }
        catch (Exception ex) { if (IsCurrent(scope)) error = ex.Message; }
    }

    private async Task AdoptLatest(WorkspaceUiScope scope)
    {
        if (!IsCurrent(scope) || latestContext is null || latestContext.WorkspaceId != scope.WorkspaceId) return;
        context = latestContext;
        latestContext = null;
        UpdateCleanDrafts();
        EnsureSelectedDrafts();
        await ReadSelectedDetail(scope);
        if (!IsCurrent(scope)) return;
        if (inspectedTaskId is { } taskId) await InspectPublishedTask(scope, taskId);
        if (!IsCurrent(scope)) return;
        await PersistDraft(scope);
        if (!IsCurrent(scope)) return;
        notice = "Loaded the latest saved context. Unsaved drafts are retained; compare and reapply any older draft before saving.";
    }

    private async Task RetryDraftStorage(WorkspaceUiScope scope)
    {
        if (!IsCurrent(scope)) return;
        if (clientId.Length == 0)
        {
            var oldBrowser = browser;
            browser = null;
            restoredGeneration = -1;
            if (oldBrowser is not null) await oldBrowser.DisposeAsync();
            return;
        }
        if (await PersistDraft(scope) && IsCurrent(scope)) notice = "The current browser draft and any pending operation are stored. You can continue saving.";
    }

    public async ValueTask DisposeAsync()
    {
        disposed = true;
        await lifetime.CancelAsync();
        if (pollTask is not null) await pollTask;
        if (browser is not null) await browser.DisposeAsync();
        lifetime.Dispose();
    }
}
