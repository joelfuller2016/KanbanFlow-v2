using KanbanFlow.Services.IdeaWorkspaces;
using Microsoft.AspNetCore.Components;

namespace KanbanFlow.Components.Workspaces;

public partial class WorkspaceDeliveryPanel
{
    [Parameter, EditorRequired] public IdeaWorkspaceContext Context { get; set; } = default!;
    [Parameter, EditorRequired] public WorkspaceDeliveryDraft Draft { get; set; } = default!;
    [Parameter] public IReadOnlyList<IdeaWorkspaceSummary> Projects { get; set; } = [];
    [Parameter] public IdeaWorkspacePublicationPreview? Preview { get; set; }
    [Parameter] public bool Busy { get; set; }
    [Parameter] public bool PreviewBusy { get; set; }
    [Parameter] public string? Error { get; set; }
    [Parameter] public EventCallback<WorkspaceDeliveryDraft> OnDraftChanged { get; set; }
    [Parameter] public EventCallback<IdeaWorkspacePublicationPreviewRequest> OnPreview { get; set; }
    [Parameter] public EventCallback OnPublish { get; set; }
    [Parameter] public EventCallback<WorkspaceUiCommand> OnCommand { get; set; }
    private readonly WorkspaceDraftInput<WorkspaceDeliveryDraft> input = new(new());
    private IReadOnlyList<IdeaWorkspaceFeatureOption> features = [];
    private int? featureProjectId;
    private WorkspaceDeliveryDraft editing => input.Value;
    protected override void OnParametersSet()
    {
        input.Receive(Draft, Context.WorkspaceId, (Context.Revision, Context.ProjectPlanRevision));
        if (featureProjectId != Context.ProjectId) { features = []; featureProjectId = Context.ProjectId; }
        if (Preview is not null) features = Preview.Features;
    }
    private Task Change(WorkspaceDeliveryDraft value) => input.ChangeAsync(value, OnDraftChanged);
    private Task TargetChanged(ChangeEventArgs e) => Change(editing with { TargetProjectId = int.TryParse(e.Value?.ToString(), out var id) ? id : null });
    private Task ProjectNameChanged(ChangeEventArgs e) => Change(editing with { ProjectName = e.Value?.ToString() ?? "" });
    private Task FeatureChanged(ChangeEventArgs e) => Change(editing with { FeatureId = int.TryParse(e.Value?.ToString(), out var id) ? id : null, FeatureName = "" });
    private Task FeatureNameChanged(ChangeEventArgs e) => Change(editing with { FeatureName = e.Value?.ToString() ?? "", FeatureId = null });
    private bool IsSelected(string id) => editing.SelectedItemIds is null || editing.SelectedItemIds.Contains(id);
    private Task SelectionChanged(string id, bool selected)
    {
        var ids = editing.SelectedItemIds ?? Context.AcceptedPlan!.Items.Select(i => i.Id).ToList();
        return Change(editing with { SelectedItemIds = selected ? [.. ids.Where(i => i != id), id] : [.. ids.Where(i => i != id)] });
    }
    private Task Link() => OnCommand.InvokeAsync(WorkspaceUiCommand.Create("link_project", new { projectId = editing.TargetProjectId, expectedCurrentProjectId = Context.ProjectId }, Context.Revision));
    private Task CreateProject() => OnCommand.InvokeAsync(WorkspaceUiCommand.Create("create_project", new { name = string.IsNullOrWhiteSpace(editing.ProjectName) ? null : editing.ProjectName }, Context.Revision));
    private Task PreviewSelection() => OnPreview.InvokeAsync(new(Context.Revision, Context.ProjectPlanRevision!.Value,
        editing.SelectedItemIds?.ToArray(), editing.FeatureId, string.IsNullOrWhiteSpace(editing.FeatureName) ? null : editing.FeatureName));
    private Task Publish() => OnPublish.InvokeAsync();
}
