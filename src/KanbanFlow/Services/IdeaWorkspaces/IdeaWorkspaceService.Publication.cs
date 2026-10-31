using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KanbanFlow.Data;
using KanbanFlow.Models;
using KanbanFlow.Models.IdeaWorkspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Services.IdeaWorkspaces;

public sealed partial class IdeaWorkspaceService
{
    public async Task<IdeaWorkspacePublicationPreview> PreviewPublicationAsync(string workspaceId,
        IdeaWorkspacePublicationPreviewRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = IdeaWorkspaceKey.Parse(workspaceId);
        ValidatePublicationRequest(request);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.Database.OpenConnectionAsync(ct);
        if (db.Database.GetDbConnection() is not SqliteConnection connection)
            throw new InvalidOperationException("Idea workspaces require the application's SQLite provider.");
        await using var sqliteTransaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: true);
        await using var transaction = await db.Database.UseTransactionAsync(sqliteTransaction, ct);
        var state = await db.Set<IdeaWorkspaceState>().AsNoTracking().SingleOrDefaultAsync(s => s.WorkspaceId == workspaceId, ct);
        var source = await ReadSourceAsync(db, key, ct);
        if (state is null && source is null) throw NotFound("workspace_not_found", $"Workspace '{workspaceId}' does not exist.");
        var revision = state?.Revision ?? 0;
        if (revision != request.ExpectedRevision)
            throw Conflict("revision_conflict", $"Workspace revision is {revision}; expected {request.ExpectedRevision}. Reload before previewing publication.");
        var projectId = key.Kind == "project" ? key.SourceId : state?.LinkedProjectId ?? source?.ProjectId;
        if (projectId is null) throw Conflict("project_required", "Link the idea to a project before previewing task publication.");
        var projectSource = await ReadSourceAsync(db, new("project", projectId.Value), ct)
            ?? throw NotFound("project_not_found", "The target project was removed. It is not silently recreated.");
        var canonical = key.Kind == "project" ? state : await db.Set<IdeaWorkspaceState>().AsNoTracking()
            .SingleOrDefaultAsync(s => s.WorkspaceId == $"project:{projectId}", ct);
        var canonicalRevision = canonical?.Revision ?? 0;
        CheckProjectPlanRevision(canonicalRevision, request.ExpectedProjectPlanRevision);
        var document = canonical is null ? CreateDocument(projectSource) : Deserialize<IdeaWorkspaceDocument>(canonical.DocumentJson);
        var preview = await BuildPublicationPreviewAsync(db, workspaceId, revision, projectId.Value, canonicalRevision, document, request, ct);
        await sqliteTransaction.CommitAsync(ct);
        return preview;
    }

    private static void ValidatePublicationRequest(IdeaWorkspacePublicationPreviewRequest request)
    {
        if (request.ExpectedRevision < 0 || request.ExpectedProjectPlanRevision < 0)
            throw Invalid("invalid_revision", "Publication revisions must be nonnegative.");
        if (request.FeatureId is <= 0) throw Invalid("invalid_feature", "featureId must be a positive integer.");
        if (request.FeatureId is not null && request.FeatureName is not null)
            throw Invalid("invalid_feature_selection", "Choose an existing featureId or a new featureName, not both.");
        if (request.FeatureName is not null && (string.IsNullOrWhiteSpace(request.FeatureName) || request.FeatureName.Trim().Length > 500))
            throw Invalid("invalid_feature_name", "A feature name must contain text and be at most 500 characters.");
        if (request.ItemIds is not null)
        {
            if (request.ItemIds.Count == 0) throw Invalid("invalid_selection", "Select at least one accepted plan item, or omit itemIds to select all.");
            foreach (var id in request.ItemIds) ValidateIdentifier(id, "itemIds");
            if (request.ItemIds.Distinct(StringComparer.Ordinal).Count() != request.ItemIds.Count)
                throw Invalid("invalid_selection", "A plan item may occur only once in itemIds.");
        }
    }

    private static async Task<IdeaWorkspacePublicationPreview> BuildPublicationPreviewAsync(AppDbContext db,
        string workspaceId, long revision, int projectId, long canonicalRevision, IdeaWorkspaceDocument document,
        IdeaWorkspacePublicationPreviewRequest request, CancellationToken ct)
    {
        ValidatePublicationRequest(request);
        var plan = CurrentCanonicalPlan(document, projectId)
            ?? throw Conflict("accepted_plan_required", "Accept a canonical project plan before publishing tasks.");
        ValidatePlan(plan);
        var selectedIds = request.ItemIds?.ToHashSet(StringComparer.Ordinal) ?? plan.Items.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        if (selectedIds.Count == 0) throw Invalid("invalid_selection", "The accepted plan has no items to publish.");
        foreach (var id in selectedIds)
            if (!plan.Items.Any(i => i.Id == id)) throw NotFound("plan_item_not_found", $"Accepted canonical item '{id}' does not exist in this project plan.");
        var features = await db.Features.AsNoTracking().Where(f => f.ProjectId == projectId)
            .OrderBy(f => f.SortOrder).ThenBy(f => f.Id).ToListAsync(ct);
        var mappedTaskIds = document.Publications.Select(p => p.TaskId).Distinct().ToArray();
        var tasks = await db.Tasks.AsNoTracking().Where(t => mappedTaskIds.Contains(t.Id)).Include(t => t.Feature).ToListAsync(ct);
        var taskById = tasks.ToDictionary(t => t.Id);
        IdeaWorkspaceTaskReference? Existing(IdeaWorkspacePlanItem item)
        {
            var origin = item.Provenance!;
            if (origin.ProjectId != projectId || origin.CanonicalItemId != item.Id)
                throw Conflict("invalid_publication_mapping", "The canonical item has inconsistent server provenance.");
            var mapping = FindPublication(document, origin);
            if (mapping is null) return null;
            if (mapping.CanonicalItemId != item.Id || !taskById.TryGetValue(mapping.TaskId, out var task) ||
                task.Feature.ProjectId != projectId || features.All(f => f.Id != mapping.FeatureId))
                throw Conflict("publication_target_missing", $"The published target for item '{item.Id}' is missing or belongs to another project. It will not be recreated.");
            if (mapping.SpecificationHash != PublicationSpecificationHash(item))
                throw Conflict("published_specification_changed", $"Published item '{item.Id}' has a changed specification. Add a new plan item for new work; existing human-edited tasks are not overwritten.");
            return new(task.Id, task.Name, task.Description, task.Status.ToString(), task.Priority.ToString(),
                task.Assignee, task.FeatureId, projectId, task.UpdatedAt);
        }
        var previewItems = new List<IdeaWorkspacePublicationPreviewItem>();
        foreach (var item in plan.Items.Where(i => selectedIds.Contains(i.Id)))
        {
            var existing = Existing(item);
            foreach (var dependencyId in item.DependsOn)
            {
                var dependency = plan.Items.Single(i => i.Id == dependencyId);
                if (!selectedIds.Contains(dependencyId) && Existing(dependency) is null)
                    throw Invalid("missing_publication_dependency", $"Item '{item.Id}' requires unpublished prerequisite '{dependencyId}'. Include it in itemIds before publishing.");
            }
            if (existing is null && item.AcceptanceCriteria.Any(string.IsNullOrWhiteSpace))
                throw Invalid("invalid_acceptance_criterion", $"Item '{item.Id}' has an empty acceptance criterion. Keep each criterion nonblank before publication.");
            previewItems.Add(new(item.Id, item.Provenance!.SourceWorkspaceId, item.Provenance.SourceItemId,
                item.Title, item.Description, item.AcceptanceCriteria.ToArray(), item.DependsOn.ToArray(), existing is null, existing));
        }
        var featureId = request.FeatureId;
        if (featureId is null && request.FeatureName is null)
            featureId = document.Publications.FirstOrDefault(p => p.ProjectId == projectId)?.FeatureId;
        var feature = featureId is null ? null : features.SingleOrDefault(f => f.Id == featureId);
        if (featureId is not null && feature is null)
            throw Conflict("publication_target_missing", "The selected or previously published feature is missing or belongs to another project. Select an existing feature explicitly; it is not recreated.");
        var featureName = feature?.Name ?? request.FeatureName?.Trim() ?? plan.Title.Trim();
        return new(workspaceId, revision, projectId, canonicalRevision, plan.Id, plan.Title, featureId, featureName,
            features.Select(f => new IdeaWorkspaceFeatureOption(f.Id, f.Name)).ToArray(), previewItems,
            previewItems.Count(i => i.WillCreate), previewItems.Count(i => !i.WillCreate));
    }

    private static async Task PublishDeliveryPlanAsync(WorkspaceWrites writes, WorkspaceWrite source, CancellationToken ct)
    {
        if (source.ProjectId is not { } projectId) throw Conflict("project_required", "Link this idea to a project before publishing tasks.");
        var canonical = (await writes.CaptureProjectAsync(projectId, required: true, ct))!;
        var payload = writes.Command.Payload;
        var request = new IdeaWorkspacePublicationPreviewRequest(writes.Command.ExpectedRevision, RequiredProjectPlanRevision(payload),
            DeliveryField(payload, "itemIds") is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } ids ? Read<List<string>>(ids) : null,
            DeliveryField(payload, "featureId") is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } feature ? IntValue(feature, "featureId") : null,
            OptionalString(payload, "featureName"));
        CheckProjectPlanRevision(canonical.OriginalRevision, request.ExpectedProjectPlanRevision);
        var preview = await BuildPublicationPreviewAsync(writes.Db, source.Key.WorkspaceId, source.OriginalRevision,
            projectId, canonical.OriginalRevision, canonical.Document, request, ct);
        var plan = canonical.Document.PlanRevisions.Single(p => p.Id == canonical.Document.AcceptedPlanRevisionId);
        foreach (var item in plan.Items) item.Provenance ??= new(projectId, $"project:{projectId}", item.Id, item.Id);
        var featureId = preview.FeatureId;
        if (preview.NewTaskCount > 0 && featureId is null)
        {
            var maxOrder = await writes.Db.Features.Where(f => f.ProjectId == projectId && f.ParentFeatureId == null)
                .Select(f => (int?)f.SortOrder).MaxAsync(ct) ?? -1;
            var created = new Feature { ProjectId = projectId, Name = preview.FeatureName,
                SortOrder = maxOrder == int.MaxValue ? int.MaxValue : maxOrder + 1,
                CreatedAt = writes.Now, UpdatedAt = writes.Now };
            writes.Db.Features.Add(created);
            await writes.Db.SaveChangesAsync(ct);
            featureId = created.Id;
            writes.Db.ActivityEvents.Add(new ActivityEvent { ProjectId = projectId, EntityType = "Feature", EntityId = created.Id,
                Actor = writes.Command.Actor, EventType = "FeatureCreated", BeforeJson = null,
                AfterJson = Serialize(new { created.Id, created.ProjectId, created.Name, created.Description, created.SortOrder, created.ParentFeatureId, created.CreatedAt, created.UpdatedAt }),
                Message = "Feature created from accepted workspace plan", CreatedAt = writes.Now });
        }
        var resultItems = new List<IdeaWorkspacePublishedItem>();
        var newTasks = new Dictionary<string, TaskItem>(StringComparer.Ordinal);
        foreach (var itemPreview in preview.Items)
        {
            var item = plan.Items.Single(i => i.Id == itemPreview.CanonicalItemId);
            var mapping = FindPublication(canonical.Document, item.Provenance!);
            if (mapping is null)
            {
                TaskItem task;
                try { task = await TaskService.CreateInContextAsync(writes.Db, new(featureId!.Value, item.Title, item.Description), writes.Command.Actor, ct); }
                catch (ValidationException ex) { throw Invalid("invalid_task_specification", ex.Message); }
                newTasks.Add(item.Id, task);
                for (var index = 0; index < item.AcceptanceCriteria.Count; index++)
                    writes.Db.AcceptanceCriteria.Add(new AcceptanceCriterion { TaskItemId = task.Id, Text = item.AcceptanceCriteria[index],
                        SortOrder = index, CreatedAt = writes.Now, UpdatedAt = writes.Now });
                mapping = new() { ProjectId = projectId, SourceWorkspaceId = item.Provenance!.SourceWorkspaceId,
                    SourceItemId = item.Provenance.SourceItemId, CanonicalItemId = item.Id, TaskId = task.Id,
                    FeatureId = task.FeatureId, SpecificationHash = PublicationSpecificationHash(item), Actor = writes.Command.Actor, CreatedAt = writes.Now };
                canonical.Document.Publications.Add(mapping);
            }
            item.PublishedTaskId = mapping.TaskId;
            item.PublishedFeatureId = mapping.FeatureId;
            resultItems.Add(new(item.Id, mapping.SourceWorkspaceId, mapping.SourceItemId, mapping.TaskId, mapping.FeatureId, itemPreview.WillCreate));
        }
        var edges = await writes.Db.TaskDependencies.Where(d => d.ProjectId == projectId).ToListAsync(ct);
        foreach (var (itemId, task) in newTasks)
        {
            var item = plan.Items.Single(i => i.Id == itemId);
            foreach (var dependencyId in item.DependsOn)
            {
                var dependency = plan.Items.Single(i => i.Id == dependencyId);
                var prerequisite = FindPublication(canonical.Document, dependency.Provenance!)
                    ?? throw Invalid("missing_publication_dependency", $"Prerequisite '{dependencyId}' was not published.");
                if (TaskDependencyService.WouldCreateCycle(task.Id, prerequisite.TaskId, edges))
                    throw Invalid("dependency_cycle", "Publishing these dependencies would create a task cycle.");
                var edge = new TaskDependency { ProjectId = projectId, TaskItemId = task.Id, DependsOnTaskItemId = prerequisite.TaskId,
                    Type = TaskDependencyType.DependsOn, CreatedAt = writes.Now };
                writes.Db.TaskDependencies.Add(edge);
                edges.Add(edge);
            }
        }
        await writes.Db.SaveChangesAsync(ct);
        foreach (var (itemId, task) in newTasks)
        {
            writes.Db.ActivityEvents.Add(new ActivityEvent { ProjectId = projectId, EntityType = "Task", EntityId = task.Id,
                Actor = writes.Command.Actor, EventType = "WorkspacePlanTaskPublished", BeforeJson = null,
                AfterJson = Serialize(new { task.Id, task.FeatureId, task.Name, task.Description, task.Status, task.Priority, task.SortOrder,
                    Criteria = writes.Db.ChangeTracker.Entries<AcceptanceCriterion>().Where(e => e.Entity.TaskItemId == task.Id).Select(e => new { e.Entity.Id, e.Entity.Text, e.Entity.Completed, e.Entity.SortOrder }).ToArray(),
                    Dependencies = edges.Where(e => e.TaskItemId == task.Id).Select(e => new { e.Id, e.DependsOnTaskItemId, e.Type }).ToArray(),
                    Provenance = plan.Items.Single(i => i.Id == itemId).Provenance }),
                Message = "Accepted plan item published to Kanban", CreatedAt = writes.Now });
        }
        canonical.Changed = true;
        writes.Publication = new(projectId, preview.NewTaskCount, preview.ExistingTaskCount, resultItems);
    }

    private static string PublicationSpecificationHash(IdeaWorkspacePlanItem item)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(new
        {
            Name = item.Title.Trim(), item.Description, item.AcceptanceCriteria,
            DependsOn = item.DependsOn.OrderBy(id => id, StringComparer.Ordinal).ToArray()
        }))));
}
