using KanbanFlow.Components.Workspaces;
using KanbanFlow.Services.IdeaWorkspaces;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Components.Pages;

public partial class IdeaWorkspace
{
    private IdeaWorkspacePublicationPreview? publicationPreview;
    private IdeaWorkspacePublicationPreviewRequest? publicationRequest;
    private string? deliveryError, taskError;
    private bool previewBusy, taskBusy;
    private long previewSequence, taskSequence;
    private int? inspectedTaskId;
    private IdeaWorkspaceTaskReference? inspectedTask;
    private IReadOnlyList<IdeaWorkspaceSummary> ProjectCatalog => catalog.Where(w => w.Kind == "project" && !w.SourceMissing).ToArray();

    private void ReconcileDeliveryDraft(bool associationChanged = false)
    {
        if (context is null) return;
        var delivery = draft.Delivery;
        var changed = delivery.AssociationKnown
            ? delivery.AssociationProjectId != context.ProjectId
            : associationChanged || delivery.SelectedItemIds is not null || delivery.FeatureId is not null;
        draft = draft with { Delivery = delivery with {
            AssociationKnown = true, AssociationProjectId = context.ProjectId,
            SelectedItemIds = changed ? [] : delivery.SelectedItemIds,
            FeatureId = changed ? null : delivery.FeatureId } };
        if (!changed) return;
        ClearDeliveryView();
        deliveryError = "The project association changed or these delivery choices predate project tracking. Item and existing-feature selections were cleared. Your proposed names and all other drafts were retained; review this project and select its plan items before previewing publication.";
    }

    private void ClearDeliveryView()
    {
        publicationPreview = null;
        publicationRequest = null;
        previewBusy = false;
        previewSequence++;
        deliveryError = null;
        inspectedTaskId = null;
        inspectedTask = null;
        taskError = null;
        taskBusy = false;
        taskSequence++;
    }

    private async Task DeliveryChanged(WorkspaceUiScope scope, WorkspaceDeliveryDraft value)
    {
        if (!IsCurrent(scope)) return;
        if (value.AssociationKnown && value.AssociationProjectId != context?.ProjectId)
        {
            deliveryError = "The project association changed before these choices were applied. Review the current project and select its plan items again.";
            return;
        }
        draft = draft with { Delivery = value with { AssociationKnown = true, AssociationProjectId = context?.ProjectId } };
        publicationPreview = null;
        publicationRequest = null;
        previewBusy = false;
        previewSequence++;
        deliveryError = null;
        await PersistDraft(scope);
    }

    private async Task PreviewDelivery(WorkspaceUiScope scope, IdeaWorkspacePublicationPreviewRequest request)
    {
        if (!IsCurrent(scope) || ControlsDisabled || context is null) return;
        var sequence = ++previewSequence;
        var exactRequest = request with { ItemIds = request.ItemIds?.ToArray() };
        previewBusy = true;
        deliveryError = null;
        try
        {
            var preview = await Workspaces.PreviewPublicationAsync(scope.WorkspaceId, exactRequest, lifetime.Token);
            if (!IsCurrent(scope) || sequence != previewSequence) return;
            publicationPreview = preview;
            publicationRequest = exactRequest;
        }
        catch (IdeaWorkspaceException ex)
        {
            if (!IsCurrent(scope) || sequence != previewSequence) return;
            deliveryError = $"{ex.Code} ({ex.StatusCode}): {ex.Message}";
        }
        catch (Exception ex) { if (IsCurrent(scope) && sequence == previewSequence) deliveryError = ex.Message; }
        finally { if (IsCurrent(scope) && sequence == previewSequence) previewBusy = false; }
    }

    private async Task PublishReviewed(WorkspaceUiScope scope)
    {
        if (!IsCurrent(scope) || publicationPreview is null || publicationRequest is not { } request || context is null) return;
        if (publicationPreview.WorkspaceId != scope.WorkspaceId || request.ExpectedRevision != context.Revision ||
            request.ExpectedProjectPlanRevision != context.ProjectPlanRevision)
        {
            deliveryError = "The reviewed context has advanced. Preview the selected tasks again before publishing.";
            return;
        }
        await RunCommand(scope, WorkspaceUiCommand.Create("publish_plan", new { expectedProjectPlanRevision = request.ExpectedProjectPlanRevision,
            itemIds = request.ItemIds, featureId = publicationPreview.FeatureId, featureName = publicationPreview.FeatureId is null ? publicationPreview.FeatureName : null }, request.ExpectedRevision));
    }

    private async Task InspectPublishedTask(WorkspaceUiScope scope, int taskId)
    {
        if (!IsCurrent(scope) || context?.ProjectId is not { } projectId) return;
        var sequence = ++taskSequence;
        inspectedTaskId = taskId;
        inspectedTask = null;
        taskBusy = true;
        taskError = null;
        try
        {
            await using var db = await DbFactory.CreateDbContextAsync(lifetime.Token);
            var task = await db.Tasks.AsNoTracking().Where(t => t.Id == taskId && t.Feature.ProjectId == projectId)
                .Select(t => new { t.Id, t.Name, t.Description, t.Status, t.Priority, t.Assignee, t.FeatureId, t.UpdatedAt })
                .SingleOrDefaultAsync(lifetime.Token);
            if (!IsCurrent(scope) || sequence != taskSequence) return;
            inspectedTask = task is null ? null : new(task.Id, task.Name, task.Description, task.Status.ToString(),
                task.Priority.ToString(), task.Assignee, task.FeatureId, projectId, task.UpdatedAt);
            if (task is null) taskError = "This published task was removed or moved outside the project. It is not recreated.";
        }
        catch (Exception ex) { if (IsCurrent(scope) && sequence == taskSequence) taskError = ex.Message; }
        finally { if (IsCurrent(scope) && sequence == taskSequence) taskBusy = false; }
    }

    private void CloseTaskInspector(WorkspaceUiScope scope)
    {
        if (!IsCurrent(scope)) return;
        inspectedTaskId = null;
        inspectedTask = null;
        taskError = null;
        taskSequence++;
        taskBusy = false;
    }
}
