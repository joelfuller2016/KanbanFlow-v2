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
    private readonly HashSet<string> restoredBrowserWorkspaces = [];
    private readonly Dictionary<string, WorkspaceBrowserDraftRecovery> draftRecoveries = [];
    private readonly List<IdeaWorkspaceSummary> catalog = [];
    private IReadOnlyList<string> componentTypes = [];
    private string workspaceId = "", clientId = "", sessionId = "";
    private string? error, notice, storageError;
    private string? storedDraftRaw, storedDraftReadError;
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
        storedDraftRaw = storedDraftReadError = null;
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
            RestoreDraft(scope, session);
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
            try
            {
                var raw = await browser!.ReadStoredDraftAsync(scope.WorkspaceId);
                if (IsCurrent(scope)) storedDraftRaw = raw;
            }
            catch (Exception readError) { if (IsCurrent(scope)) storedDraftReadError = readError.Message; }
            if (!IsCurrent(scope)) return;
            StateHasChanged();
        }
        finally { if (restoringGeneration == scope.Generation) restoringGeneration = -1; }
    }

    private void RestoreDraft(WorkspaceUiScope scope, WorkspaceBrowserSession session)
    {
        var cached = browserCache.GetValueOrDefault(scope.WorkspaceId);
        if (cached is not null && session.Draft is not null && !restoredBrowserWorkspaces.Contains(scope.WorkspaceId))
        {
            cached.Validate();
            if (HasAuthoredState(cached))
            {
                clientId = "";
                storageHealthy = false;
                throw new System.Text.Json.JsonException("New edits were entered before the existing browser draft could be restored. Preserve the original stored draft before continuing; neither draft has been replaced.");
            }
            cached = null;
        }
        var restored = cached ?? session.Draft ?? DraftFromCheckpointFor(session.ClientId, session.SessionId);
        restored.Validate();
        var normalized = restored.NormalizeContentIdentity();
        clientId = session.ClientId;
        sessionId = session.SessionId;
        draft = normalized;
        ReconcileDeliveryDraft();
        UpdateCleanDrafts();
        EnsureSelectedDrafts();
        browserCache[scope.WorkspaceId] = draft;
        restoredBrowserWorkspaces.Add(scope.WorkspaceId);
    }

    private static bool HasAuthoredState(WorkspaceBrowserDraft value)
        => value.ContentDrafts.Values.Any(content => content.Dirty) || value.LayoutDrafts.Values.Any(layout => layout.Dirty)
            || value.Pending is not null || value.Discussion.Text.Length > 0
            || value.Connector.FromComponentId.Length > 0 || value.Connector.ToComponentId.Length > 0 || value.Connector.Label != "related"
            || value.Plan.Title.Length > 0 || value.Plan.Items.Count > 0 || value.Plan.BaseRevision.HasValue || value.Plan.BaseProjectPlanRevision.HasValue
            || value.Checkpoint.Objective.Length > 0 || value.Checkpoint.CompletedChanges.Length > 0 || value.Checkpoint.OpenQuestions.Length > 0
            || value.Checkpoint.NextActions.Length > 0 || value.Checkpoint.FocusedComponentIds.Count > 0
            || value.Checkpoint.OriginalCompletedChanges is not null || value.Checkpoint.OriginalOpenQuestions is not null || value.Checkpoint.OriginalNextActions is not null
            || value.Delivery.ProjectName.Length > 0 || value.Delivery.FeatureName.Length > 0 || value.Delivery.TargetProjectId.HasValue
            || value.Delivery.SelectedItemIds is not null || value.Delivery.FeatureId.HasValue;

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
                var observed = context;
                if (observed is null || busy || !Ready) continue;
                var scope = CurrentScope;
                try
                {
                    var next = await ReadPolledContext(scope, observed);
                    if (next is not null)
                        await InvokeAsync(() => { if (TryApplyPolledContext(scope, observed, next)) StateHasChanged(); });
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    if (!IsCurrent(scope) || !ReferenceEquals(context, observed)) continue;
                    await InvokeAsync(() => { if (IsCurrent(scope) && ReferenceEquals(context, observed)) { error = $"Checking for external changes failed: {ex.Message}. Use Refresh saved context to retry."; StateHasChanged(); } });
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private async Task<IdeaWorkspaceContext?> ReadPolledContext(WorkspaceUiScope scope, IdeaWorkspaceContext observed)
    {
        if (!IsCurrent(scope) || !ReferenceEquals(context, observed)) return null;
        var next = await Workspaces.GetAsync(scope.WorkspaceId, lifetime.Token);
        if (!IsCurrent(scope) || !ReferenceEquals(context, observed)) return null;
        return next.Revision != observed.Revision || next.Source != observed.Source ||
            next.ProjectPlanRevision != observed.ProjectPlanRevision || next.ProjectSource != observed.ProjectSource ||
            next.ProjectMissing != observed.ProjectMissing ? next : null;
    }

    private bool TryApplyPolledContext(WorkspaceUiScope scope, IdeaWorkspaceContext observed, IdeaWorkspaceContext next)
    {
        if (!IsCurrent(scope) || !ReferenceEquals(context, observed)) return false;
        latestContext = next;
        return true;
    }

    private async Task Refresh(WorkspaceUiScope scope)
    {
        var observed = context;
        if (!IsCurrent(scope) || busy || observed is null) return;
        try
        {
            var refreshed = await Workspaces.GetAsync(scope.WorkspaceId, lifetime.Token);
            if (!IsCurrent(scope) || !ReferenceEquals(context, observed)) return;
            latestContext = refreshed;
            await AdoptLatest(scope);
        }
        catch (Exception ex) { if (IsCurrent(scope) && ReferenceEquals(context, observed)) error = ex.Message; }
    }

    private async Task AdoptLatest(WorkspaceUiScope scope)
    {
        if (!IsCurrent(scope) || latestContext is null || latestContext.WorkspaceId != scope.WorkspaceId) return;
        var projectChanged = context?.ProjectId != latestContext.ProjectId;
        context = latestContext;
        latestContext = null;
        ReconcileDeliveryDraft(projectChanged);
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
        if (!IsCurrent(scope) || busy) return;
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

    private async Task RecoverStoredDraft(WorkspaceUiScope scope)
    {
        var recoveryBrowser = browser;
        if (!IsCurrent(scope) || busy || recoveryBrowser is null) return;
        busy = true;
        try
        {
            draft.Validate();
            var preserved = await recoveryBrowser.PreserveAndClearDraftAsync(scope.WorkspaceId);
            if (!IsCurrent(scope)) return;
            draftRecoveries[scope.WorkspaceId] = preserved;
            browserCache[scope.WorkspaceId] = draft;
            restoredBrowserWorkspaces.Add(scope.WorkspaceId);
            storedDraftRaw = storedDraftReadError = null;
            clientId = sessionId = "";
            storageError = error = null;
            restoredGeneration = -1;
            if (ReferenceEquals(browser, recoveryBrowser)) browser = null;
            await recoveryBrowser.DisposeAsync();
            notice = "The original stored draft was preserved. Your current unsaved edits are retained while browser storage is restored; the preserved text remains available below.";
        }
        catch (Exception ex)
        {
            if (IsCurrent(scope)) storageError = $"The original browser draft could not be preserved: {ex.Message}. Your current draft has been retained.";
        }
        finally { busy = false; }
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
