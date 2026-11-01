using KanbanFlow.Services.IdeaWorkspaces;
using KanbanFlow.Data;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Components.Workspaces;

public partial class WorkspaceDeliveryPanel : IDisposable
{
    [Inject] private IDbContextFactory<AppDbContext> DbFactory { get; set; } = default!;
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
    private readonly CancellationTokenSource lifetime = new();
    private readonly WorkspaceDraftInput<WorkspaceDeliveryDraft> input = new(new());
    private IReadOnlyList<IdeaWorkspaceFeatureOption> features = [];
    private (string WorkspaceId, int? ProjectId, long? PlanRevision, bool Missing) featureScope;
    private Task? featureRead;
    private long featureReadSequence;
    private bool featuresLoaded, featuresBusy, disposed;
    private string? featuresError;
    private IdeaWorkspacePublicationPreview? appliedFeaturePreview;
    private (string WorkspaceId, int? ProjectId, long? PlanRevision, bool Missing) CurrentFeatureScope
        => (Context.WorkspaceId, Context.ProjectId, Context.ProjectPlanRevision, Context.ProjectMissing);
    private WorkspaceDeliveryDraft editing => input.Value;
    protected override void OnParametersSet()
    {
        input.Receive(Draft, Context.WorkspaceId, (Context.Revision, Context.ProjectId, Context.ProjectPlanRevision));
        if (featureScope != CurrentFeatureScope)
        {
            featureScope = CurrentFeatureScope;
            features = [];
            featuresLoaded = featuresBusy = false; featuresError = null; featureRead = null; featureReadSequence++;
            appliedFeaturePreview = null;
        }
        if (Preview is not null && Preview.WorkspaceId == Context.WorkspaceId && Preview.ProjectId == Context.ProjectId
            && Preview.Revision == Context.Revision && Preview.ProjectPlanRevision == Context.ProjectPlanRevision
            && !ReferenceEquals(Preview, appliedFeaturePreview))
        {
            appliedFeaturePreview = Preview;
            features = Preview.Features;
            featuresLoaded = true; featuresBusy = false; featuresError = null;
            featureRead = null; featureReadSequence++;
        }
    }
    protected override Task OnParametersSetAsync() => disposed || Context.ProjectId is null || Context.ProjectMissing || featuresLoaded
        ? Task.CompletedTask : featureRead ??= ReadFeatures();
    private Task ReloadFeatures()
    {
        if (disposed || featuresBusy || Context.ProjectId is null || Context.ProjectMissing) return Task.CompletedTask;
        featuresLoaded = false;
        return featureRead = ReadFeatures();
    }
    private async Task ReadFeatures()
    {
        var scope = CurrentFeatureScope;
        if (scope.ProjectId is not { } projectId || scope.Missing || disposed) return;
        var sequence = ++featureReadSequence;
        featuresBusy = true;
        featuresError = null;
        try
        {
            await using var db = await DbFactory.CreateDbContextAsync(lifetime.Token);
            var loaded = await db.Features.AsNoTracking().Where(feature => feature.ProjectId == projectId)
                .OrderBy(feature => feature.Name).ThenBy(feature => feature.Id)
                .Select(feature => new IdeaWorkspaceFeatureOption(feature.Id, feature.Name)).ToListAsync(lifetime.Token);
            if (!FeatureReadIsCurrent(scope, sequence)) return;
            features = loaded;
            featuresLoaded = true;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception)
        {
            if (FeatureReadIsCurrent(scope, sequence))
                featuresError = "Project feature choices could not be loaded. Reload the choices to retry.";
        }
        finally { if (FeatureReadIsCurrent(scope, sequence)) featuresBusy = false; }
    }
    private bool FeatureReadIsCurrent((string WorkspaceId, int? ProjectId, long? PlanRevision, bool Missing) scope, long sequence)
        => !disposed && sequence == featureReadSequence && scope == CurrentFeatureScope;
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        featureReadSequence++;
        lifetime.Cancel();
        lifetime.Dispose();
    }
    private Task Change(WorkspaceDeliveryDraft value) => input.ChangeAsync(value, OnDraftChanged);
    private Task TargetChanged(ChangeEventArgs e) => Change(editing with { TargetProjectId = int.TryParse(e.Value?.ToString(), out var id) ? id : null });
    private Task ProjectNameChanged(ChangeEventArgs e) => Change(editing with { ProjectName = e.Value?.ToString() ?? "" });
    private Task FeatureChanged(ChangeEventArgs e)
    {
        var selected = e.Value?.ToString();
        if (string.IsNullOrEmpty(selected)) return Change(editing with { FeatureId = null, FeatureName = "" });
        if (!int.TryParse(selected, out var id) || !features.Any(feature => feature.Id == id)) return Task.CompletedTask;
        return Change(editing with { FeatureId = id, FeatureName = "" });
    }
    private Task FeatureChangedFromProject(int? renderedProjectId, ChangeEventArgs e)
        => renderedProjectId == Context.ProjectId ? FeatureChanged(e) : Task.CompletedTask;
    private Task FeatureNameChanged(ChangeEventArgs e) => Change(editing with { FeatureName = e.Value?.ToString() ?? "", FeatureId = null });
    private bool IsSelected(string id) => editing.SelectedItemIds is null || editing.SelectedItemIds.Contains(id);
    private Task SelectionChanged(string id, bool selected)
    {
        if (Context.AcceptedPlan is not { } plan || !plan.Items.Any(item => item.Id == id)) return Task.CompletedTask;
        var ids = editing.SelectedItemIds ?? plan.Items.Select(i => i.Id).ToList();
        return Change(editing with { SelectedItemIds = selected ? [.. ids.Where(i => i != id), id] : [.. ids.Where(i => i != id)] });
    }
    private Task SelectionChangedFromProject(int? renderedProjectId, string id, bool selected)
        => renderedProjectId == Context.ProjectId ? SelectionChanged(id, selected) : Task.CompletedTask;
    private Task Link() => OnCommand.InvokeAsync(WorkspaceUiCommand.Create("link_project", new { projectId = editing.TargetProjectId, expectedCurrentProjectId = Context.ProjectId }, Context.Revision));
    private Task CreateProject() => OnCommand.InvokeAsync(WorkspaceUiCommand.Create("create_project", new { name = string.IsNullOrWhiteSpace(editing.ProjectName) ? null : editing.ProjectName }, Context.Revision));
    private Task PreviewSelection() => OnPreview.InvokeAsync(new(Context.Revision, Context.ProjectPlanRevision!.Value,
        editing.SelectedItemIds?.ToArray(), editing.FeatureId, string.IsNullOrWhiteSpace(editing.FeatureName) ? null : editing.FeatureName));
    private Task Publish() => OnPublish.InvokeAsync();
}
