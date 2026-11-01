using KanbanFlow.Components.Workspaces;
using KanbanFlow.Services.IdeaWorkspaces;
using System.Text.Json;

namespace KanbanFlow.Components.Pages;

public partial class IdeaWorkspace
{
    private async Task RunCommand(WorkspaceUiScope scope, WorkspaceUiCommand request)
    {
        if (!IsCurrent(scope) || ControlsDisabled || context is null ||
            (request.WorkspaceId is not null && request.WorkspaceId != scope.WorkspaceId)) return;
        var command = new IdeaWorkspaceCommand(Guid.NewGuid().ToString("N"), request.ExpectedRevision ?? context.Revision,
            request.Action, request.Payload.Clone(), "browser:" + clientId);
        var section = request.Action is "publish_plan" or "link_project" or "create_project" ? "delivery" : request.DraftSection;
        var sectionDraft = DraftSectionValue(draft, section);
        var pending = new WorkspacePendingCommand(command, section,
            sectionDraft is null ? null : JsonSerializer.SerializeToElement(sectionDraft, WorkspaceUiCommand.Json));
        draft = draft with { Pending = pending };
        await ExecutePending(scope, pending);
    }

    private Task RetryPending(WorkspaceUiScope scope) => !IsCurrent(scope) || busy || draft.Pending is not { } pending
        ? Task.CompletedTask : ExecutePending(scope, pending);

    private async Task ExecutePending(WorkspaceUiScope scope, WorkspacePendingCommand pending)
    {
        if (!IsCurrent(scope) || busy) return;
        // Capture source scope and lock navigation before the first async persistence boundary.
        busy = true;
        error = notice = null;
        try
        {
            // Every send, including a retry after quota failure, first stores the identical envelope.
            if (!await PersistDraft(scope)) return;
            var result = await Workspaces.ExecuteAsync(scope.WorkspaceId, pending.Command, lifetime.Token);
            if (!IsCurrent(scope)) return;
            // Retain the retry envelope until the complete post-save context is available.
            var refreshed = await Workspaces.GetAsync(scope.WorkspaceId, lifetime.Token);
            if (!IsCurrent(scope)) return;
            draft = ClearSavedSection(draft with { Pending = null }, pending);
            if (result.Action == "remove_component") draft = draft with { SelectedComponentId = null, SelectedPlacementId = null };
            else if (result.ComponentId is not null) draft = draft with { SelectedComponentId = result.ComponentId, SelectedPlacementId = result.PlacementId ?? draft.SelectedPlacementId };
            context = refreshed;
            if (result.Action is "publish_plan" or "link_project" or "create_project")
            {
                publicationPreview = null;
                publicationRequest = null;
                previewSequence++;
                draft = draft with { Delivery = CompleteDeliveryDraft(draft.Delivery, pending, context.ProjectId) };
            }
            latestContext = null;
            ReconcileDeliveryDraft();
            UpdateCleanDrafts();
            EnsureSelectedDrafts();
            await ReadSelectedDetail(scope);
            if (!IsCurrent(scope)) return;
            await PersistDraft(scope);
            if (!IsCurrent(scope)) return;
            notice = $"Saved {result.Action.Replace('_', ' ')} at revision {result.Revision}{(result.Replayed ? " (recovered prior result)" : "")}.";
            if (result.Publication is { } publication)
                notice += $" {publication.NewTaskCount} new tasks; {publication.ExistingTaskCount} already linked tasks preserved.";
            if (result.Action == "create_project")
            {
                try { await LoadCatalog(); }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
                catch (Exception catalogError)
                {
                    if (IsCurrent(scope)) notice += $" The project was saved, but refreshing the workspace picker failed: {catalogError.Message}. Reopen the workspace to retry the picker.";
                }
            }
        }
        catch (IdeaWorkspaceException ex)
        {
            if (!IsCurrent(scope)) return;
            draft = draft with { Pending = null };
            error = $"{ex.Code} ({ex.StatusCode}): {ex.Message}";
            await PersistDraft(scope);
            if (ex.StatusCode == 409 && IsCurrent(scope))
            {
                try
                {
                    var latest = await Workspaces.GetAsync(scope.WorkspaceId, lifetime.Token);
                    if (IsCurrent(scope)) latestContext = latest;
                }
                catch (Exception refreshError) { if (IsCurrent(scope)) error += $" Refresh failed: {refreshError.Message}"; }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (IsCurrent(scope)) error = $"The result is uncertain: {ex.Message}. The exact pending operation is retained for retry.";
        }
        finally { busy = false; }
    }

    private static object? DraftSectionValue(WorkspaceBrowserDraft value, string? section)
    {
        if (section?.StartsWith("content:", StringComparison.Ordinal) == true)
            return value.ContentDrafts.GetValueOrDefault(section[8..]);
        if (section?.StartsWith("layout:", StringComparison.Ordinal) == true)
            return value.LayoutDrafts.GetValueOrDefault(section[7..]);
        return section switch { "discussion" => value.Discussion, "connector" => value.Connector, "plan" => value.Plan, "delivery" => value.Delivery, _ => null };
    }

    private static WorkspaceDeliveryDraft CompleteDeliveryDraft(WorkspaceDeliveryDraft current, WorkspacePendingCommand pending, int? projectId)
    {
        WorkspaceDeliveryDraft? sent = null;
        if (pending.DraftSection == "delivery" && pending.DraftAtSend is { ValueKind: JsonValueKind.Object } snapshot)
        {
            try { sent = snapshot.Deserialize<WorkspaceDeliveryDraft>(WorkspaceUiCommand.Json); }
            catch (JsonException) { } // Legacy/incompatible acknowledgement metadata cannot authorize clearing authored fields.
        }
        var associationChanged = !current.AssociationKnown || current.AssociationProjectId != projectId;
        var published = pending.Command.Action == "publish_plan" && sent is { AssociationKnown: true }
            && current.AssociationKnown && sent.AssociationProjectId == projectId && current.AssociationProjectId == projectId;
        var sameItems = sent is not null && (current.SelectedItemIds is null ? sent.SelectedItemIds is null
            : sent.SelectedItemIds is not null && current.SelectedItemIds.SequenceEqual(sent.SelectedItemIds));
        return current with {
            AssociationKnown = true, AssociationProjectId = projectId,
            SelectedItemIds = associationChanged || (published && sameItems) ? null : current.SelectedItemIds,
            FeatureId = associationChanged || (published && current.FeatureId == sent!.FeatureId) ? null : current.FeatureId,
            FeatureName = published && current.FeatureName == sent!.FeatureName ? "" : current.FeatureName };
    }

    private static WorkspaceBrowserDraft ClearSavedSection(WorkspaceBrowserDraft value, WorkspacePendingCommand pending)
    {
        var section = pending.DraftSection;
        if (pending.DraftAtSend is not { } saved || JsonSerializer.SerializeToElement(DraftSectionValue(value, section), WorkspaceUiCommand.Json).GetRawText() != saved.GetRawText())
            return value; // No send snapshot or a newer edit arrived; keep the current buffer.
        if (section?.StartsWith("content:", StringComparison.Ordinal) == true)
        {
            var content = new Dictionary<string, WorkspaceContentDraft>(value.ContentDrafts);
            content.Remove(section[8..]);
            return value with { ContentDrafts = content };
        }
        if (section?.StartsWith("layout:", StringComparison.Ordinal) == true)
        {
            var layouts = new Dictionary<string, WorkspaceLayoutDraft>(value.LayoutDrafts);
            layouts.Remove(section[7..]);
            return value with { LayoutDrafts = layouts };
        }
        return section switch { "discussion" => value with { Discussion = new() }, "connector" => value with { Connector = new() }, "plan" => value with { Plan = new() }, _ => value };
    }
}
