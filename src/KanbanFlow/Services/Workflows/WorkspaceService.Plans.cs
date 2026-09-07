using System.Text.Json;
using KanbanFlow.Models;
using KanbanFlow.Models.Workflows;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Services.Workflows;

public sealed partial class WorkspaceService
{
    public Task<MutationReceipt> SavePlanAsync(Guid id, int expected, TaskDraft[] drafts, string actor, string key, CancellationToken ct = default,
        IReadOnlyDictionary<int, string>? expectedTaskStates = null) =>
        ChangeAsync(id, expected, actor, "plan.save", key, new { drafts, expectedTaskStates }, async (db, request) =>
        {
            var brief = request.Briefs.MaxBy(b => b.Number) ?? throw Invalid("Save the improved brief before drafting tasks.");
            ValidatePlan(drafts);
            var repositoryIds = drafts.Where(d => d.RepositoryId is not null).Select(d => d.RepositoryId!.Value).Distinct().ToArray();
            if (await db.ProjectRepositories.CountAsync(r => repositoryIds.Contains(r.Id) && r.ProjectId == request.ProjectId, ct) != repositoryIds.Length)
                throw Invalid("Every repository must belong to this request's project.");
            if (request.WorkItems.Any(w => !drafts.Any(d => d.Id == w.DraftId)))
                throw Invalid("Published tasks must stay in the plan. Revise them or archive the request; task retirement needs a scope decision.");
            var baseline = new Dictionary<int, string>();
            if (request.WorkItems.Count > 0 && (expectedTaskStates is null
                || !request.WorkItems.Select(w => w.TaskItemId).ToHashSet().SetEquals(expectedTaskStates.Keys)))
                throw Invalid("Include taskBaselines from each current task's editToken when revising published work.");
            foreach (var item in request.WorkItems)
            {
                var currentHash = await PublicationStateHashAsync(db, item.TaskItemId, ct);
                if (expectedTaskStates![item.TaskItemId] != currentHash)
                    throw new WorkflowException("conflict", $"Task #{item.TaskItemId} changed on the board while this plan was being edited. Refresh, review its current details and combine your draft before saving.");
                baseline[item.TaskItemId] = currentHash;
            }
            request.Plans.Add(new PlanVersion { RequestId = id, Number = request.Plans.Count + 1, BriefRevisionId = brief.Id,
                DraftJson = JsonSerializer.Serialize(drafts), TaskBaselineJson = JsonSerializer.Serialize(baseline), CreatedAt = Now });
        }, ct);

    public Task<MutationReceipt> PublishPlanAsync(Guid id, int expected, Guid planId, string actor, string key, CancellationToken ct = default) =>
        ChangeAsync(id, expected, actor, "plan.publish", key, new { planId }, async (db, request) =>
        {
            var plan = request.Plans.SingleOrDefault(p => p.Id == planId) ?? throw Missing("Plan");
            if (plan.PublishedAt is not null) return;
            if (request.Plans.MaxBy(p => p.Number)!.Id != planId) throw Invalid("Publish the latest plan version.");
            if (request.Briefs.MaxBy(b => b.Number)!.Id != plan.BriefRevisionId) throw Invalid("The brief has changed. Save a new plan version before publishing.");
            if (request.Briefs.MaxBy(b => b.Number)!.IntentHash != IntentHash(request))
                throw Invalid("Review the latest clarifications and decisions, then save a new brief and plan before publishing.");
            if (request.ProjectId is null) throw Invalid("Attach this request to a project before publishing tasks.");
            var drafts = Drafts(plan);
            ValidatePlan(drafts);
            if (drafts.Length == 0) throw Invalid("Add at least one task before publishing.");
            var repositoryIds = drafts.Where(d => d.RepositoryId is not null).Select(d => d.RepositoryId!.Value).Distinct().ToArray();
            if (await db.ProjectRepositories.CountAsync(r => repositoryIds.Contains(r.Id) && r.ProjectId == request.ProjectId, ct) != repositoryIds.Length)
                throw Invalid("A draft repository no longer belongs to this request's project. Update the plan before publishing.");
            var baseline = JsonSerializer.Deserialize<Dictionary<int, string>>(plan.TaskBaselineJson)!;
            foreach (var item in request.WorkItems)
            {
                if (item.Task.Feature.ProjectId != request.ProjectId) throw Invalid($"Task #{item.TaskItemId} moved to another project. Resolve its request mapping before publishing.");
                var draft = drafts.Single(d => d.Id == item.DraftId);
                if (item.SpecificationHash != Hash(draft) && (!baseline.TryGetValue(item.TaskItemId, out var savedHash)
                    || savedHash != await PublicationStateHashAsync(db, item.TaskItemId, ct)))
                    throw new WorkflowException("conflict", $"Task #{item.TaskItemId} changed on the board after this plan was saved. Review its current fields and save a revised plan before publishing.");
            }
            Feature? feature = request.FeatureId is int featureId ? await db.Features.FindAsync([featureId], ct) : null;
            feature ??= new Feature { Name = request.Title, ProjectId = request.ProjectId.Value, CreatedAt = Now, UpdatedAt = Now };
            if (feature.Id == 0) db.Features.Add(feature);
            await db.SaveChangesWithAuditAsync(actor, ct);
            request.FeatureId = feature.Id;
            var changed = new List<PlannedTask>();
            for (var index = 0; index < drafts.Length; index++)
            {
                var draft = drafts[index];
                var hash = Hash(draft);
                var item = request.WorkItems.SingleOrDefault(w => w.DraftId == draft.Id);
                if (item is not null && item.SpecificationHash == hash) { item.Task.SortOrder = index; continue; }
                if (item?.Task.Status == WorkStatus.InProgress) throw Invalid("Move active work out of In progress before adopting a changed specification.");
                if (item is null)
                {
                    var task = await TaskService.CreateInContextAsync(db,
                        new TaskCreate(feature.Id, draft.Title, draft.Description, WorkStatus.Todo, draft.Priority, draft.Assignee, index), actor, ct);
                    item = new PlannedTask { RequestId = id, DraftId = draft.Id, Task = task, Plan = plan, SpecificationHash = hash, SpecificationJson = JsonSerializer.Serialize(draft) };
                    request.WorkItems.Add(item);
                }
                else
                {
                    item.SpecificationVersion++;
                    db.AcceptanceCriteria.RemoveRange(item.Task.AcceptanceCriteria);
                    item.Task.AcceptanceCriteria.Clear();
                }
                item.Plan = plan;
                item.SpecificationHash = hash;
                item.SpecificationJson = JsonSerializer.Serialize(draft);
                await TaskService.UpdateInContextAsync(db, item.Task.Id,
                    new TaskUpdate(draft.Title, draft.Description, WorkStatus.Todo, draft.Priority, draft.Assignee, index), actor, ct);
                item.Task.AcceptanceCriteria.AddRange(draft.Acceptance.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Select((text, order) => new AcceptanceCriterion { Text = text, SortOrder = order, CreatedAt = Now, UpdatedAt = Now }));
                changed.Add(item);
            }
            await InvalidateDependentsAsync(db, request, changed.Select(w => w.TaskItemId).Where(taskId => taskId != 0).ToHashSet(), actor, ct);
            // IDs become available here; the surrounding transaction still owns every write and receipt.
            await db.SaveChangesWithAuditAsync(actor, ct);
            foreach (var item in changed)
            {
                var oldEdges = await db.TaskDependencies.Where(d => d.TaskItemId == item.TaskItemId).ToListAsync(ct);
                db.TaskDependencies.RemoveRange(oldEdges);
            }
            await db.SaveChangesWithAuditAsync(actor, ct);
            var resultingEdges = await db.TaskDependencies.AsNoTracking().Where(d => d.ProjectId == request.ProjectId).ToListAsync(ct);
            foreach (var item in changed)
            {
                var draft = drafts.Single(d => d.Id == item.DraftId);
                foreach (var dependency in draft.Dependencies)
                {
                    var blocker = request.WorkItems.Single(w => w.DraftId == dependency).TaskItemId;
                    if (TaskDependencyService.WouldCreateCycle(item.TaskItemId, blocker, resultingEdges))
                        throw Invalid("This plan conflicts with dependencies edited on the board and would create a cycle. Revise the plan.");
                    var edge = new TaskDependency { ProjectId = request.ProjectId.Value, TaskItemId = item.TaskItemId,
                        DependsOnTaskItemId = blocker, CreatedAt = Now };
                    resultingEdges.Add(edge);
                    db.TaskDependencies.Add(edge);
                }
            }
            plan.PublishedAt = Now;
        }, ct);

    public static void ValidatePlan(TaskDraft[]? drafts)
    {
        if (drafts is null || drafts.Length > 100) throw Invalid("A plan supports up to 100 tasks.");
        if (drafts.Any(d => d is null || d.Id == Guid.Empty) || drafts.Select(d => d.Id).Distinct().Count() != drafts.Length)
            throw Invalid("Each task draft needs a distinct non-empty identity.");
        var lookup = drafts.ToDictionary(d => d.Id);
        foreach (var draft in drafts)
        {
            Required(draft.Title, "Task title", 200);
            Bounded(draft.Description, "Task description", 10000);
            Required(draft.Acceptance, "Task acceptance checks", 10000);
            Bounded(draft.FileReferences, "File references", 10000);
            Bounded(draft.Assignee, "Assignee", 200);
            if (!Enum.IsDefined(draft.Priority)) throw Invalid("Select a valid priority.");
            RepositoryPaths.ValidateReferences(draft.FileReferences);
            if (draft.FileReferences.Length > 0 && draft.RepositoryId is null) throw Invalid("Select a repository for file references.");
            if (draft.Dependencies is null || draft.Dependencies.Distinct().Count() != draft.Dependencies.Length || draft.Dependencies.Any(d => d == draft.Id || !lookup.ContainsKey(d)))
                throw Invalid("Dependencies must identify other distinct tasks in this plan.");
        }
        var visited = new HashSet<Guid>();
        var active = new HashSet<Guid>();
        bool Visit(Guid id)
        {
            if (active.Contains(id)) return false;
            if (!visited.Add(id)) return true;
            active.Add(id);
            foreach (var dependency in lookup[id].Dependencies) if (!Visit(dependency)) return false;
            active.Remove(id);
            return true;
        }
        if (drafts.Any(d => !Visit(d.Id))) throw Invalid("Task dependencies contain a cycle.");
    }
}
