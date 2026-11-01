using KanbanFlow.Components.Workspaces;
using KanbanFlow.Services.IdeaWorkspaces;

namespace KanbanFlow.Components.Pages;

public partial class IdeaWorkspace
{
    private long detailSequence;
    private string SelectedContentKey => WorkspaceContentDraft.KeyForComponent(draft.SelectedComponentId);
    private WorkspaceContentDraft SelectedContent => draft.ContentDrafts.GetValueOrDefault(SelectedContentKey)
        ?? new() { BaseRevision = context?.Revision ?? 0 };
    private WorkspaceLayoutDraft? SelectedLayout => draft.SelectedPlacementId is { } id ? draft.LayoutDrafts.GetValueOrDefault(id) : null;
    private IReadOnlySet<string> DirtyIds => draft.ContentDrafts.Values.Where(d => d.Dirty && d.ComponentId is not null).Select(d => d.ComponentId!).ToHashSet(StringComparer.Ordinal);
    private int DirtyCount => draft.ContentDrafts.Values.Count(d => d.Dirty) + draft.LayoutDrafts.Values.Count(d => d.Dirty) + (draft.Plan.BaseRevision.HasValue ? 1 : 0) + (draft.Discussion.Text.Length > 0 ? 1 : 0);

    private WorkspaceBrowserDraft DraftFromCheckpoint() => DraftFromCheckpointFor(clientId, sessionId);

    private WorkspaceBrowserDraft DraftFromCheckpointFor(string checkpointClientId, string checkpointSessionId)
    {
        var checkpoint = context!.Document.Checkpoints.FirstOrDefault(c => c.ClientId == checkpointClientId && c.SessionId == checkpointSessionId)
            ?? context.Document.Checkpoints.Where(c => c.ClientId == checkpointClientId).MaxBy(c => c.UpdatedAt);
        var id = checkpoint?.FocusedComponentIds.FirstOrDefault(c => context.Document.Components.Any(component => component.Id == c))
            ?? context.Document.Components.FirstOrDefault()?.Id;
        return new() { SelectedComponentId = id, Checkpoint = checkpoint is null ? new() : WorkspaceCheckpointDraft.From(checkpoint) };
    }

    private void EnsureSelectedDrafts()
    {
        if (context is null) return;
        if (draft.SelectedComponentId is { } selected && !context.Document.Components.Any(c => c.Id == selected)
            && (!draft.ContentDrafts.TryGetValue(WorkspaceContentDraft.KeyForComponent(selected), out var missing) || !missing.Dirty))
            draft = draft with { SelectedComponentId = null, SelectedPlacementId = null };
        var contents = new Dictionary<string, WorkspaceContentDraft>(draft.ContentDrafts);
        if (!contents.ContainsKey(SelectedContentKey))
        {
            var component = context.Document.Components.FirstOrDefault(c => c.Id == draft.SelectedComponentId);
            contents[SelectedContentKey] = component is null ? new() { BaseRevision = context.Revision }
                : WorkspaceContentDraft.From(component, context.Revision);
        }
        var placementId = draft.SelectedPlacementId;
        if (!context.Document.Placements.Any(p => p.Id == placementId && p.ComponentId == draft.SelectedComponentId))
            placementId = context.Document.Placements.FirstOrDefault(p => p.ComponentId == draft.SelectedComponentId)?.Id;
        var layouts = new Dictionary<string, WorkspaceLayoutDraft>(draft.LayoutDrafts);
        if (placementId is not null && !layouts.ContainsKey(placementId))
            layouts[placementId] = WorkspaceLayoutDraft.From(context.Document.Placements.First(p => p.Id == placementId), context.Revision);
        draft = draft with { ContentDrafts = contents, LayoutDrafts = layouts, SelectedPlacementId = placementId };
    }

    private void UpdateCleanDrafts()
    {
        if (context is null) return;
        var contents = new Dictionary<string, WorkspaceContentDraft>(draft.ContentDrafts);
        foreach (var (id, value) in contents.ToArray())
        {
            if (value.Dirty) continue;
            var component = context.Document.Components.FirstOrDefault(c => c.Id == value.ComponentId);
            if (component is not null) contents[id] = WorkspaceContentDraft.From(component, context.Revision);
            else if (id == "$new") contents[id] = value with { BaseRevision = context.Revision };
        }
        var layouts = new Dictionary<string, WorkspaceLayoutDraft>(draft.LayoutDrafts);
        foreach (var p in context.Document.Placements)
            if (layouts.TryGetValue(p.Id, out var old) && !old.Dirty) layouts[p.Id] = WorkspaceLayoutDraft.From(p, context.Revision);
        draft = draft with { ContentDrafts = contents, LayoutDrafts = layouts };
    }

    private async Task<bool> PersistDraft(WorkspaceUiScope scope)
    {
        if (!IsCurrent(scope)) return false;
        var snapshot = draft;
        browserCache[scope.WorkspaceId] = snapshot;
        if (browser is null || !Ready) return false;
        var version = ++persistenceVersion;
        try
        {
            await browser.SaveDraftAsync(scope.WorkspaceId, snapshot, version);
            if (IsCurrent(scope) && version == persistenceVersion)
            {
                storageHealthy = true;
                storageError = null;
            }
            return true;
        }
        catch (Exception ex)
        {
            if (IsCurrent(scope) && version == persistenceVersion)
            {
                storageHealthy = false;
                storageError = $"The browser draft could not be stored: {ex.Message}. Your current buffer is retained in this open view. Writes are paused until browser storage succeeds.";
            }
            return false;
        }
    }

    private async Task Select(WorkspaceUiScope scope, WorkspaceSelection selection)
    {
        if (!IsCurrent(scope)) return;
        draft = draft with { SelectedComponentId = selection.ComponentId, SelectedPlacementId = selection.PlacementId };
        EnsureSelectedDrafts();
        await ReadSelectedDetail(scope);
        if (IsCurrent(scope)) await PersistDraft(scope);
    }
    private Task NewComponent(WorkspaceUiScope scope) => Select(scope, new(null));

    private async Task ReadSelectedDetail(WorkspaceUiScope scope)
    {
        if (!IsCurrent(scope)) return;
        var sequence = ++detailSequence;
        selectedDetail = null;
        if (context is null || draft.SelectedComponentId is null || !context.Document.Components.Any(c => c.Id == draft.SelectedComponentId)) return;
        var id = draft.SelectedComponentId;
        try
        {
            var detail = await Workspaces.GetComponentAsync(scope.WorkspaceId, id, lifetime.Token);
            if (IsCurrent(scope) && id == draft.SelectedComponentId && sequence == detailSequence) selectedDetail = detail;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (IdeaWorkspaceException ex) { if (IsCurrent(scope) && id == draft.SelectedComponentId && sequence == detailSequence) error = ex.Message; }
        catch (Exception ex)
        {
            if (IsCurrent(scope) && id == draft.SelectedComponentId && sequence == detailSequence)
                error = $"Component details could not be loaded: {ex.Message}. Your unsaved draft is retained; select the component again to retry.";
        }
    }

    private async Task ContentChanged(WorkspaceUiScope scope, WorkspaceContentDraft value)
    {
        if (!IsCurrent(scope)) return;
        draft = draft with { ContentDrafts = new(draft.ContentDrafts) { [value.Key] = value } };
        await PersistDraft(scope);
    }
    private async Task LayoutChanged(WorkspaceUiScope scope, WorkspaceLayoutDraft value)
    {
        if (!IsCurrent(scope)) return;
        draft = draft with { LayoutDrafts = new(draft.LayoutDrafts) { [value.PlacementId] = value } };
        await PersistDraft(scope);
    }
    private async Task ReapplyContent(WorkspaceUiScope scope, string key)
    {
        if (!IsCurrent(scope) || context is null || !draft.ContentDrafts.TryGetValue(key, out var value)) return;
        var current = context.Document.Components.FirstOrDefault(c => c.Id == value.ComponentId);
        if (value.ComponentId is not null && current is null)
        {
            if (key != "$new" && draft.ContentDrafts.TryGetValue("$new", out var newCard) && newCard.Dirty)
            {
                error = "Save your existing unsaved new card before recovering this one. Use Add component to return to that draft. Both complete drafts have been retained.";
                return;
            }
            var contents = new Dictionary<string, WorkspaceContentDraft>(draft.ContentDrafts);
            contents.Remove(key);
            contents["$new"] = value with { Key = "$new", ComponentId = null, BaseRevision = context.Revision };
            draft = draft with { SelectedComponentId = null, SelectedPlacementId = null, ContentDrafts = contents };
            await PersistDraft(scope);
        }
        else await ContentChanged(scope, value with { BaseRevision = context.Revision, OriginalContent = current?.Content ?? "", OriginalTitle = current?.Title ?? "" });
    }
    private async Task ReapplyLayout(WorkspaceUiScope scope, string key)
    {
        if (!IsCurrent(scope) || context is null || !draft.LayoutDrafts.TryGetValue(key, out var value)) return;
        if (!context.Document.Placements.Any(p => p.Id == key)) { error = "This appearance was removed. Add another appearance to retain the component."; return; }
        await LayoutChanged(scope, value with { BaseRevision = context.Revision });
    }
    private async Task DiscussionChanged(WorkspaceUiScope scope, WorkspaceDiscussionDraft value) { if (!IsCurrent(scope)) return; draft = draft with { Discussion = value }; await PersistDraft(scope); }
    private async Task ConnectorChanged(WorkspaceUiScope scope, WorkspaceConnectorDraft value) { if (!IsCurrent(scope)) return; draft = draft with { Connector = value }; await PersistDraft(scope); }
    private async Task PlanChanged(WorkspaceUiScope scope, WorkspacePlanDraft value) { if (!IsCurrent(scope)) return; draft = draft with { Plan = value }; await PersistDraft(scope); }
    private async Task CheckpointChanged(WorkspaceUiScope scope, WorkspaceCheckpointDraft value) { if (!IsCurrent(scope)) return; draft = draft with { Checkpoint = value }; await PersistDraft(scope); }
}
