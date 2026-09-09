using KanbanFlow.Models;
using KanbanFlow.Models.Workflows;
using KanbanFlow.Data;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Services.Workflows;

public sealed partial class WorkspaceService
{
    public Task<MutationReceipt> SetTaskStateAsync(Guid id, int expected, Guid itemId, WorkStatus state, string actor, string key, CancellationToken ct = default) =>
        ChangeAsync(id, expected, actor, "task.state", key, new { itemId, state }, async (db, request) =>
        {
            if (!Enum.IsDefined(state)) throw Invalid("Select a valid task status.");
            var item = request.WorkItems.SingleOrDefault(w => w.Id == itemId) ?? throw Missing("Task");
            if (state == WorkStatus.InProgress && await db.TaskDependencies.AnyAsync(d => d.TaskItemId == item.TaskItemId && d.DependsOnTaskItem.Status != WorkStatus.Done, ct))
                throw Invalid("Complete this task's dependencies before starting it.");
            if (item.Task.Status == WorkStatus.Done && state != WorkStatus.Done)
                await InvalidateDependentsAsync(db, request, [item.TaskItemId], actor, ct);
            await TaskService.ChangeStatusInContextAsync(db, item.TaskItemId, state, actor, ct);
        }, ct);

    public Task<MutationReceipt> RecordEvidenceAsync(Guid id, int expected, Guid itemId, EvidenceDraft evidence, string actor, string key, CancellationToken ct = default) =>
        ChangeAsync(id, expected, actor, "evidence.report", key, new { itemId, evidence }, async (db, request) =>
        {
            var item = request.WorkItems.SingleOrDefault(w => w.Id == itemId) ?? throw Missing("Task");
            if (item.Task.Status == WorkStatus.Done) await InvalidateDependentsAsync(db, request, [item.TaskItemId], actor, ct);
            item.Evidence.Add(new TaskEvidence { WorkItemId = itemId, SpecificationVersion = item.SpecificationVersion, SpecificationHash = item.SpecificationHash,
                TaskStateHash = await TaskStateHashAsync(db, item.TaskItemId, ct),
                Summary = Required(evidence.Summary, "Result summary", 20000), Checks = Required(evidence.Checks, "Observed checks", 20000),
                CodeReference = Required(evidence.CodeReference, "Checked source or document reference", 2000), Author = actor, CreatedAt = Now });
            var report = $"Reported result for task #{item.TaskItemId}\n\n{evidence.Summary}\n\nChecks: {evidence.Checks}\n\nReference: {evidence.CodeReference}";
            request.Messages.Add(new RequestMessage { RequestId = id, Kind = "assistant-report", Text = report, Author = actor, Source = "reported-result", CreatedAt = Now });
            await TaskService.AddCommentInContextAsync(db, item.TaskItemId, report, actor, CommentType.Verification, actor, ct);
            await TaskService.ChangeStatusInContextAsync(db, item.TaskItemId, WorkStatus.Review, actor, ct);
        }, ct);

    public Task<MutationReceipt> AcceptEvidenceAsync(Guid id, int expected, Guid itemId, Guid evidenceId, int[] checkedCriterionIds,
        string actor, string key, CancellationToken ct = default) =>
        ChangeAsync(id, expected, actor, "evidence.accept", key, new { itemId, evidenceId, checkedCriterionIds }, async (db, request) =>
        {
            var item = request.WorkItems.SingleOrDefault(w => w.Id == itemId) ?? throw Missing("Task");
            var evidence = item.Evidence.SingleOrDefault(e => e.Id == evidenceId) ?? throw Missing("Evidence");
            if (!EvidenceMatches(item, evidence, await TaskStateHashAsync(db, item.TaskItemId, ct)))
                throw Invalid("This evidence covers an older specification. Record checks for the current task.");
            var criteria = item.Task.AcceptanceCriteria.Select(c => c.Id).ToHashSet();
            if (checkedCriterionIds is null || criteria.Count == 0 || !criteria.SetEquals(checkedCriterionIds) || criteria.Count != checkedCriterionIds.Length)
                throw Invalid("Review and confirm every current acceptance check before accepting this result.");
            if (await db.TaskDependencies.AnyAsync(d => d.TaskItemId == item.TaskItemId && d.DependsOnTaskItem.Status != WorkStatus.Done, ct))
                throw Invalid("Complete this task's dependencies before accepting the result.");
            foreach (var criterion in item.Task.AcceptanceCriteria) { criterion.Completed = true; criterion.UpdatedAt = Now; }
            evidence.AcceptedAt = Now;
            evidence.AcceptedBy = actor;
            await db.SaveChangesWithAuditAsync(actor, ct);
            await TaskService.ChangeStatusInContextAsync(db, item.TaskItemId, WorkStatus.Done, actor, ct);
        }, ct);

    public Task<MutationReceipt> ConcludeAsync(Guid id, int expected, string conclusion, string limitations, string actor, string key, CancellationToken ct = default) =>
        ChangeAsync(id, expected, actor, "request.conclude", key, new { conclusion, limitations }, async (db, request) =>
        {
            if (request.WorkItems.Count == 0 || request.WorkItems.Any(w => w.Task.Status != WorkStatus.Done))
                throw Invalid("Accept the required task results before recording the completed request conclusion.");
            var latest = request.Plans.MaxBy(p => p.Number);
            if (latest?.PublishedAt is null || latest.BriefRevisionId != request.Briefs.MaxBy(b => b.Number)?.Id
                || request.Briefs.MaxBy(b => b.Number)?.IntentHash != IntentHash(request))
                throw Invalid("The latest brief and plan must be published before concluding the request.");
            if (!Drafts(latest).Select(d => d.Id).ToHashSet().SetEquals(request.WorkItems.Select(w => w.DraftId)))
                throw Invalid("A published task was deleted. Restore the plan's tasks before concluding.");
            var hashes = new Dictionary<int, string>();
            var taskSnapshot = await TaskSnapshot.LoadAsync(db, request.WorkItems.Select(w => w.TaskItemId), ct);
            foreach (var item in request.WorkItems)
            {
                if (item.Task.Feature.ProjectId != request.ProjectId) throw Invalid($"Task #{item.TaskItemId} moved to another project. Resolve its request mapping before concluding.");
                var stateHash = taskSnapshot.StateHash(item.TaskItemId);
                hashes[item.TaskItemId] = stateHash;
                if (!item.Evidence.Any(e => e.AcceptedAt is not null && EvidenceMatches(item, e, stateHash))
                    || item.Task.AcceptanceCriteria.Count == 0 || item.Task.AcceptanceCriteria.Any(c => !c.Completed))
                    throw Invalid($"Task #{item.TaskItemId} needs a current accepted result and completed checks.");
            }
            request.Results.Add(new RequestResult { RequestId = id, RequestVersion = expected + 1,
                ContextHash = ConclusionHash(request, hashes),
                Conclusion = Required(conclusion, "Conclusion", 50000), Limitations = Bounded(limitations, "Limitations", 10000), Author = actor, CreatedAt = Now });
        }, ct);

    private async Task InvalidateDependentsAsync(AppDbContext db, WorkRequest request, HashSet<int> changed, string actor, CancellationToken ct)
    {
        var pending = new Queue<int>(changed);
        while (pending.TryDequeue(out var dependency))
        {
            foreach (var item in request.WorkItems.Where(w => w.Task.Dependencies.Any(d => d.DependsOnTaskItemId == dependency)))
            {
                if (!changed.Add(item.TaskItemId)) continue;
                item.SpecificationVersion++;
                await TaskService.ChangeStatusInContextAsync(db, item.TaskItemId, WorkStatus.Todo, actor, ct);
                foreach (var criterion in item.Task.AcceptanceCriteria) { criterion.Completed = false; criterion.UpdatedAt = Now; }
                pending.Enqueue(item.TaskItemId);
            }
        }
    }

    public static bool EvidenceMatches(PlannedTask item, TaskEvidence evidence, string stateHash) =>
        evidence.SpecificationVersion == item.SpecificationVersion && evidence.SpecificationHash == item.SpecificationHash
        && evidence.TaskStateHash == stateHash;

    // Read the actual task graph, including edits through the existing UI/REST/MCP services.
    // Own status/check ticks are excluded so accepting a report does not invalidate itself.
    // Prerequisite completion and transitive scope ARE included, so reopening or changing
    // a dependency cannot leave a previously accepted report looking current.
    public static async Task<string> TaskStateHashAsync(AppDbContext db, int taskId, CancellationToken ct = default)
    {
        var snapshot = await TaskSnapshot.LoadAsync(db, [taskId], ct);
        return snapshot.StateHash(taskId);
    }
}
