using System.Text.Json;
using KanbanFlow.Models;
using KanbanFlow.Models.IdeaWorkspaces;
using KanbanFlow.Models.Workflows;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Services.IdeaWorkspaces;

public sealed partial class IdeaWorkspaceService
{
    private static bool IsDeliveryAction(string action)
        => action is "propose_plan" or "accept_plan" or "link_project" or "create_project" or "publish_plan";

    private static async Task<(string? ComponentId, string? PlacementId)> ApplyDeliveryCommandAsync(
        WorkspaceWrites writes, WorkspaceWrite source, CancellationToken ct)
    {
        switch (writes.Command.Action)
        {
            case "link_project":
                await AssociateProjectAsync(writes, source, create: false, ct);
                break;
            case "create_project":
                await AssociateProjectAsync(writes, source, create: true, ct);
                break;
            case "propose_plan":
                await ProposeDeliveryPlanAsync(writes, source, ct);
                break;
            case "accept_plan":
                await AcceptDeliveryPlanAsync(writes, source, ct);
                break;
            case "publish_plan":
                await PublishDeliveryPlanAsync(writes, source, ct);
                break;
        }
        return (null, null);
    }

    private static async Task AssociateProjectAsync(WorkspaceWrites writes, WorkspaceWrite source, bool create, CancellationToken ct)
    {
        if (source.Key.Kind != "idea")
            throw Invalid("invalid_workspace_kind", "Only an idea can be associated with a project. A project keeps its primary project workspace.");
        var idea = await writes.Db.Ideas.SingleOrDefaultAsync(i => i.Id == source.Key.SourceId, ct)
            ?? throw NotFound("idea_not_found", "The idea source was removed. Its saved workspace remains available, but cannot be promoted or linked.");
        var currentProjectId = source.ProjectId;
        var payload = writes.Command.Payload;
        int targetId;
        if (create)
        {
            if (currentProjectId is not null || idea.Status == IdeaStatus.Promoted || idea.PromotedProjectId is not null)
                throw Conflict("project_already_linked", "This idea was already promoted. Use explicit link_project to select another existing project; it is not promoted a second time.");
            var name = OptionalString(payload, "name") ?? idea.Title;
            if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 500)
                throw Invalid("invalid_project_name", "Project name must contain text and be at most 500 characters.");
            // Compose without the legacy promotion helper's Trim(): all authored narrative survives.
            var readme = idea.Body is null ? null : idea.Body;
            if (idea.Plan is not null) readme = (readme is null ? "" : readme + "\n\n") + "## Plan\n\n" + idea.Plan;
            var project = new Project { Name = name.Trim(), Description = idea.Body, Goals = idea.Plan, Readme = readme,
                CreatedAt = writes.Now, UpdatedAt = writes.Now };
            writes.Db.Projects.Add(project);
            await writes.Db.SaveChangesAsync(ct);
            targetId = project.Id;
            writes.Db.ActivityEvents.Add(new ActivityEvent { ProjectId = targetId, EntityType = "Project", EntityId = targetId,
                Actor = writes.Command.Actor, EventType = "ProjectCreatedFromIdea", BeforeJson = null,
                AfterJson = Serialize(await ReadSourceAsync(writes.Db, new("project", targetId), ct)),
                Message = $"Project created from workspace {source.Key.WorkspaceId}", CreatedAt = writes.Now });
        }
        else
        {
            var target = DeliveryField(payload, "projectId");
            targetId = IntValue(target, "projectId");
            if (targetId <= 0) throw Invalid("invalid_project", "projectId must be positive.");
            var expected = DeliveryField(payload, "expectedCurrentProjectId");
            if (expected.ValueKind != JsonValueKind.Undefined)
            {
                var expectedId = expected.ValueKind == JsonValueKind.Null ? (int?)null : IntValue(expected, "expectedCurrentProjectId");
                if (expectedId != currentProjectId)
                    throw Conflict("project_link_conflict", $"The current project link is {currentProjectId?.ToString() ?? "none"}; reload before relinking.");
            }
            else if (currentProjectId is not null && currentProjectId != targetId)
                throw Conflict("project_link_conflict", "Replacing a project link requires explicit expectedCurrentProjectId.");
        }
        var targetWorkspace = await writes.CaptureProjectAsync(targetId, required: true, ct);
        await ValidateAssociationTaskReferencesAsync(writes, source, targetId, ct);
        await SynchronizeWorkflowProjectAsync(writes, idea.Id, targetId, ct);
        var before = source.Source;
        idea.Status = IdeaStatus.Promoted;
        idea.PromotedProjectId = targetId;
        idea.UpdatedAt = writes.Now;
        source.State.LinkedProjectId = targetId;
        source.Source = new("idea", idea.Id, idea.Title, idea.Body, idea.Plan, null, idea.Status.ToString(),
            idea.Tags, idea.Priority?.ToString(), targetId, idea.CreatedAt, idea.UpdatedAt);
        writes.Db.ActivityEvents.Add(new ActivityEvent { ProjectId = targetId, EntityType = "Idea", EntityId = idea.Id,
            Actor = writes.Command.Actor, EventType = create ? "IdeaPromoted" : "IdeaLinkedToProject",
            BeforeJson = Serialize(before), AfterJson = Serialize(source.Source),
            Message = $"Idea workspace associated with project {targetId}", CreatedAt = writes.Now });
        // A new project has no competing delivery plan: carry the already accepted standalone plan.
        if (create && source.Document.AcceptedPlanRevisionId is { } acceptedId)
        {
            var accepted = source.Document.PlanRevisions.Single(p => p.Id == acceptedId);
            MergeAcceptedContribution(writes, source, targetWorkspace!, accepted);
        }
    }

    private static async Task SynchronizeWorkflowProjectAsync(WorkspaceWrites writes, int ideaId, int projectId, CancellationToken ct)
    {
        var workflow = await writes.Db.WorkRequests.SingleOrDefaultAsync(request => request.IdeaId == ideaId, ct);
        if (workflow is null) return; // Uncaptured legacy ideas retain their explicit adoption flow.
        var foreignFeature = workflow.FeatureId is { } featureId && !await writes.Db.Features
            .AnyAsync(feature => feature.Id == featureId && feature.ProjectId == projectId, ct);
        var foreignTask = await writes.Db.Set<PlannedTask>()
            .AnyAsync(item => item.RequestId == workflow.Id && item.Task.Feature.ProjectId != projectId, ct);
        if (foreignFeature || foreignTask)
            throw Conflict("project_link_workflow_conflict", $"Project {projectId} cannot be linked while the request workflow has a feature or published tasks outside that project. Reconcile the existing workflow project and published work before relinking.");
        if (workflow.ProjectId == projectId) return;
        workflow.ProjectId = projectId;
        workflow.Version++;
        workflow.UpdatedAt = writes.Now;
    }

    private static async Task ValidateAssociationTaskReferencesAsync(
        WorkspaceWrites writes, WorkspaceWrite source, int projectId, CancellationToken ct)
    {
        var references = source.Document.Components.Where(c => c.Type == "task-reference").ToArray();
        if (references.Length == 0) return;
        var taskIds = references.Where(c => c.TaskId is not null).Select(c => c.TaskId!.Value).Distinct().ToArray();
        var validTasks = await writes.Db.Tasks.AsNoTracking()
            .Where(t => taskIds.Contains(t.Id) && t.Feature.ProjectId == projectId)
            .Select(t => t.Id).ToHashSetAsync(ct);
        var invalidCards = references.Where(c => c.TaskId is not { } taskId || !validTasks.Contains(taskId))
            .Select(c => c.Id).ToArray();
        if (invalidCards.Length != 0)
            throw Conflict("project_link_task_conflict", $"Project {projectId} cannot be linked while task-reference cards {string.Join(", ", invalidCards.Select(id => $"'{id}'"))} refer to missing tasks or tasks outside that project. Convert these cards to notes and clear taskId, or remove them, before relinking.");
    }

    private static async Task ProposeDeliveryPlanAsync(WorkspaceWrites writes, WorkspaceWrite source, CancellationToken ct)
    {
        var plan = Read<IdeaWorkspacePlanRevision>(writes.Command.Payload);
        plan.Id = NewIdentifier();
        plan.Version = source.Document.PlanRevisions.Count + 1;
        plan.Status = "Draft";
        plan.CreatedBy = writes.Command.Actor;
        plan.CreatedAt = writes.Now;
        plan.AcceptedAt = null;
        plan.AcceptedBy = null;
        if (string.IsNullOrWhiteSpace(plan.Title) || plan.Title.Length > 500 || plan.Items is null)
            throw Invalid("invalid_plan", "A plan needs a nonblank title (at most 500 characters) and an items list.");
        WorkspaceWrite? canonical = writes.FindProject(source.ProjectId);
        if (canonical is null && source.ProjectId is { } id && await writes.Db.Set<IdeaWorkspaceState>().AnyAsync(s => s.WorkspaceId == $"project:{id}", ct))
            canonical = await writes.LoadAsync(new("project", id), ct);
        var canonicalPlan = source.ProjectId is { } projectId && canonical is not null
            ? CurrentCanonicalPlan(canonical.Document, projectId) : null;
        foreach (var item in plan.Items)
        {
            if (item is null) throw Invalid("invalid_plan_item", "Plan items cannot be null.");
            item.Id = NewIdentifier(item.Id);
            ValidateProposedIdentity(item, source.ProjectId, canonical?.Document, canonicalPlan);
        }
        ValidatePlan(plan);
        source.Document.PlanRevisions.Add(plan);
    }

    private static async Task AcceptDeliveryPlanAsync(WorkspaceWrites writes, WorkspaceWrite source, CancellationToken ct)
    {
        var planId = RequiredString(writes.Command.Payload, "planRevisionId");
        var plan = source.Document.PlanRevisions.SingleOrDefault(p => p.Id == planId)
            ?? throw NotFound("plan_not_found", "The proposed plan revision is not in this workspace.");
        if (plan.Status != "Draft") throw Conflict("plan_already_accepted", "This plan revision is already accepted.");
        if (source.ProjectId is not { } projectId)
        {
            SetAcceptedPlan(source.Document, plan, writes.Command.Actor, writes.Now);
            return;
        }
        var canonical = (await writes.CaptureProjectAsync(projectId, required: true, ct))!;
        if (source.Key.Kind == "idea" && DeliveryField(writes.Command.Payload, "expectedProjectPlanRevision").ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            throw Conflict("shared_plan_required", "Linked plan acceptance requires an explicit expectedProjectPlanRevision from the current canonical project context.");
        CheckProjectPlanRevision(canonical.OriginalRevision, RequiredProjectPlanRevision(writes.Command.Payload));
        MergeAcceptedContribution(writes, source, canonical, plan);
    }

    private static void MergeAcceptedContribution(WorkspaceWrites writes, WorkspaceWrite source, WorkspaceWrite canonical,
        IdeaWorkspacePlanRevision proposal)
    {
        var projectId = canonical.Key.SourceId;
        var existing = CurrentCanonicalPlan(canonical.Document, projectId);
        var merged = existing is null ? new List<IdeaWorkspacePlanItem>() : Deserialize<List<IdeaWorkspacePlanItem>>(Serialize(existing.Items));
        var localIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var resolvedIds = new HashSet<string>(StringComparer.Ordinal);
        var resolvedOrigins = new HashSet<(int ProjectId, string WorkspaceId, string ItemId)>();
        var resolutions = new List<(IdeaWorkspacePlanItem Item, IdeaWorkspacePlanItemProvenance Origin)>();
        foreach (var item in proposal.Items)
        {
            ValidateProposedIdentity(item, projectId, canonical.Document, existing);
            var origin = item.Provenance;
            if (origin is null)
            {
                origin = merged.Select(i => i.Provenance!).SingleOrDefault(p => p.SourceWorkspaceId == source.Key.WorkspaceId && p.SourceItemId == item.Id)
                    ?? new(projectId, source.Key.WorkspaceId, item.Id, NewIdentifier());
            }
            if (!resolvedIds.Add(origin.CanonicalItemId) || !resolvedOrigins.Add((origin.ProjectId, origin.SourceWorkspaceId, origin.SourceItemId)))
                throw Conflict("duplicate_canonical_identity", "Two proposed items resolve to the same canonical identity. Keep the copied item once and use a new local ID for new work.");
            resolutions.Add((item, origin));
        }
        // Validate every resolution before modifying the proposal or merging any item.
        foreach (var (item, origin) in resolutions)
        {
            item.Provenance = origin;
            localIds.Add(item.Id, origin.CanonicalItemId);
        }
        foreach (var item in proposal.Items)
        {
            var copy = Deserialize<IdeaWorkspacePlanItem>(Serialize(item));
            copy.Id = localIds[item.Id];
            copy.DependsOn = item.DependsOn.Select(id => localIds[id]).ToList();
            var mapping = FindPublication(canonical.Document, item.Provenance!);
            copy.PublishedTaskId = mapping?.TaskId;
            copy.PublishedFeatureId = mapping?.FeatureId;
            var index = merged.FindIndex(i => i.Id == copy.Id);
            if (index < 0) merged.Add(copy); else merged[index] = copy;
        }
        IdeaWorkspacePlanRevision accepted;
        if (source.Key == canonical.Key)
        {
            proposal.Items = merged;
            accepted = proposal;
        }
        else
        {
            accepted = new() { Id = NewIdentifier(), Version = canonical.Document.PlanRevisions.Count + 1,
                Title = existing?.Title ?? proposal.Title, Items = merged, CreatedBy = writes.Command.Actor,
                CreatedAt = writes.Now, ExpectedProjectPlanRevision = canonical.OriginalRevision };
            canonical.Document.PlanRevisions.Add(accepted);
            SetAcceptedPlan(source.Document, proposal, writes.Command.Actor, writes.Now);
        }
        ValidatePlan(accepted);
        SetAcceptedPlan(canonical.Document, accepted, writes.Command.Actor, writes.Now);
        canonical.Changed = true;
    }

    private static void SetAcceptedPlan(IdeaWorkspaceDocument document, IdeaWorkspacePlanRevision plan, string actor, DateTime now)
    {
        if (document.AcceptedPlanRevisionId is { } old && old != plan.Id)
            document.PlanRevisions.Single(p => p.Id == old).Status = "Superseded";
        plan.Status = "Accepted";
        plan.AcceptedBy = actor;
        plan.AcceptedAt = now;
        document.AcceptedPlanRevisionId = plan.Id;
    }

    private static IdeaWorkspacePlanRevision? CurrentCanonicalPlan(IdeaWorkspaceDocument document, int projectId)
    {
        var stored = document.PlanRevisions.SingleOrDefault(p => p.Id == document.AcceptedPlanRevisionId);
        if (stored is null) return null;
        var plan = Deserialize<IdeaWorkspacePlanRevision>(Serialize(stored));
        // Bounded legacy compatibility: these IDs were already accepted before provenance existed.
        foreach (var item in plan.Items)
            item.Provenance ??= new(projectId, $"project:{projectId}", item.Id, item.Id);
        return plan;
    }

    private static void ValidateProposedIdentity(IdeaWorkspacePlanItem item, int? projectId,
        IdeaWorkspaceDocument? canonical, IdeaWorkspacePlanRevision? canonicalPlan)
    {
        if (item.Provenance is not { } origin)
        {
            if (item.PublishedTaskId is not null || item.PublishedFeatureId is not null)
                throw Invalid("forged_provenance", "Published task/feature IDs require validated canonical provenance; they do not choose a mapping.");
            return;
        }
        if (origin.ProjectId != projectId || canonicalPlan?.Items.Any(i => i.Provenance == origin) != true)
            throw Invalid("forged_provenance", "The item's provenance does not identify an accepted item in the current target project. New items must omit provenance and publication IDs.");
        var mapping = FindPublication(canonical!, origin);
        if ((item.PublishedTaskId is not null && item.PublishedTaskId != mapping?.TaskId) ||
            (item.PublishedFeatureId is not null && item.PublishedFeatureId != mapping?.FeatureId))
            throw Invalid("forged_provenance", "Caller-supplied publication IDs do not match the durable server mapping.");
        item.PublishedTaskId = mapping?.TaskId;
        item.PublishedFeatureId = mapping?.FeatureId;
    }

    private static IdeaWorkspacePublication? FindPublication(IdeaWorkspaceDocument document, IdeaWorkspacePlanItemProvenance origin)
        => document.Publications.SingleOrDefault(p => p.ProjectId == origin.ProjectId &&
            p.SourceWorkspaceId == origin.SourceWorkspaceId && p.SourceItemId == origin.SourceItemId);

    private static JsonElement DeliveryField(JsonElement payload, string name)
        => payload.EnumerateObject().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    private static long RequiredProjectPlanRevision(JsonElement payload)
    {
        var value = DeliveryField(payload, "expectedProjectPlanRevision");
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var revision) || revision < 0)
            throw Invalid("invalid_project_plan_revision", "An explicit nonnegative expectedProjectPlanRevision is required for shared plan acceptance/publication.");
        return revision;
    }

    private static void CheckProjectPlanRevision(long current, long expected)
    {
        if (current != expected)
            throw Conflict("project_plan_revision_conflict", $"The canonical project-plan revision is {current}; expected {expected}. Reload the shared plan before accepting or publishing.");
    }
}
