using System.Text.Json;
using KanbanFlow.Data;
using KanbanFlow.Models.Workflows;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Services.Workflows;

public sealed partial class WorkspaceService
{
    public async Task<WorkflowContext?> ContextAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        // A context and its edit tokens describe one consistent graph. Reserve the SQLite
        // writer while collecting the split-query graph so a token cannot describe newer
        // fields than the client actually received. The read transaction is disposed here.
        await using var snapshot = await db.Database.BeginTransactionAsync(ct);
        var request = await Graph(db).AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct);
        if (request is null) return null;
        var hashes = new Dictionary<int, string>();
        var editTokens = new Dictionary<int, string>();
        foreach (var item in request.WorkItems)
        {
            hashes[item.TaskItemId] = await TaskStateHashAsync(db, item.TaskItemId, ct);
            editTokens[item.TaskItemId] = await PublicationStateHashAsync(db, item.TaskItemId, ct);
        }
        var contextHash = ConclusionHash(request, hashes);
        var repositories = await RepositoriesAsync(request.ProjectId, ct);
        return new(request.Id, request.IdeaId, request.ProjectId, request.FeatureId, request.Title,
            request.Idea.Status.ToString(), request.Version, request.Archived,
            request.Briefs.MaxBy(b => b.Number)?.IntentHash != IntentHash(request), $"/ideas?idea={request.IdeaId}",
            request.ProjectId is int projectId ? $"/project/{projectId}?request={request.Id}" : null,
            request.Messages.OrderBy(m => m.CreatedAt).ThenBy(m => m.Id).Select(m => new MessageContext(m.Id, m.Kind, m.Text, m.Author, m.Source, m.CreatedAt)).ToArray(),
            request.Briefs.OrderBy(b => b.Number).Select(b => new BriefContext(b.Id, b.Number, b.Outcome, b.Scope, b.Constraints, b.Acceptance, b.Author, b.CreatedAt)).ToArray(),
            request.Plans.OrderBy(p => p.Number).Select(p => new PlanContext(p.Id, p.Number, p.BriefRevisionId, Drafts(p), p.CreatedAt, p.PublishedAt)).ToArray(),
            request.WorkItems.OrderBy(w => w.Task.SortOrder).Select(w => new WorkflowTaskContext(w.Id, w.DraftId, w.TaskItemId, w.Task.FeatureId,
                w.Task.Name, w.Task.Description, w.Task.Status.ToString(), w.Task.Priority.ToString(), w.Task.Assignee, w.Task.ClaimedBy,
                $"/project/{w.Task.Feature.ProjectId}?task={w.TaskItemId}", w.SpecificationVersion, JsonSerializer.Deserialize<TaskDraft>(w.SpecificationJson)!,
                w.Task.AcceptanceCriteria.OrderBy(c => c.SortOrder).Select(c => new WorkflowCheck(c.Id, c.Text, c.Completed)).ToArray(),
                w.Task.Subtasks.OrderBy(c => c.SortOrder).Select(c => new WorkflowCheck(c.Id, c.Text, c.Completed)).ToArray(),
                w.Task.Dependencies.Select(d => d.DependsOnTaskItemId).Order().ToArray(),
                w.Task.Comments.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).Select(c => new WorkflowComment(c.Id, c.Author, c.Type.ToString(), c.Text, c.CreatedAt)).ToArray(),
                w.Evidence.OrderBy(e => e.CreatedAt).Select(e => new EvidenceContext(e.Id, e.Summary, e.Checks, e.CodeReference, e.Author,
                    e.CreatedAt, e.AcceptedAt, e.AcceptedBy, EvidenceMatches(w, e, hashes[w.TaskItemId]))).ToArray(), editTokens[w.TaskItemId])).ToArray(),
            request.Results.OrderBy(r => r.CreatedAt).Select(r => new ResultContext(r.Id, r.Conclusion, r.Limitations, r.Author, r.CreatedAt,
                r.ContextHash == contextHash)).ToArray(),
            repositories.Select(r => new RepositoryContext(r.Id, r.ProjectId, r.Name, r.LocalPath)).ToArray());
    }

    private static string ConclusionHash(WorkRequest request, IReadOnlyDictionary<int, string> hashes) => Hash(new
    {
        Intent = IntentHash(request),
        Brief = request.Briefs.MaxBy(b => b.Number)?.Id,
        Plan = request.Plans.MaxBy(p => p.Number)?.Id,
        Tasks = request.WorkItems.OrderBy(w => w.TaskItemId).Select(w => new
        {
            w.TaskItemId, w.SpecificationHash, w.SpecificationVersion, w.Task.Status,
            State = hashes[w.TaskItemId], Checks = w.Task.AcceptanceCriteria.OrderBy(c => c.Id).Select(c => c.Completed),
            Accepted = w.Evidence.Where(e => e.AcceptedAt != null).OrderBy(e => e.Id).Select(e => e.Id)
        })
    });

    private static string IntentHash(WorkRequest request) => Hash(request.Messages
        .Where(m => m.Kind is "original" or "clarification" or "decision").OrderBy(m => m.Id)
        .Select(m => new { m.Id, m.Kind, m.Text }));

    public async Task<WorkflowCommandResult> ExecuteAsync(WorkflowCommandInput input, WorkflowOptions options, CancellationToken ct = default)
    {
        if (input is null) throw Invalid("Command is required.");
        Required(input.Actor, "Actor", 200);
        Required(input.Key, "Operation key", 100);
        T Need<T>(T? value, string name) where T : struct => value ?? throw Invalid($"{name} is required.");
        string Text(string? value, string name) => value ?? throw Invalid($"{name} is required.");
        var actor = input.Actor; var key = input.Key;
        if (input.Operation == "capture") return Reply(await CaptureAsync(input.Capture ?? throw Invalid("Capture is required."), actor, key, ct));
        if (input.Operation == "adopt_idea") return Reply(await AdoptIdeaAsync(Need(input.IdeaId, "IdeaId"), actor, key, ct));
        if (input.Operation == "create_project") return new(null, null, await CreateProjectAsync(Text(input.Name, "Name"), actor, key, ct));
        if (input.Operation == "register_repository") return new(null, null, await RegisterRepositoryAsync(Need(input.ProjectId, "ProjectId"),
            Text(input.Name, "Name"), Text(input.Path, "Path"), options.ProjectsRoot, actor, key, ct));
        var id = Need(input.RequestId, "RequestId"); var version = Need(input.ExpectedVersion, "ExpectedVersion");
        return Reply(await (input.Operation switch
        {
            "attach_project" => AttachProjectAsync(id, version, Need(input.ProjectId, "ProjectId"), actor, key, ct),
            "save_brief" => SaveBriefAsync(id, version, input.Brief ?? throw Invalid("Brief is required."), actor, key, ct),
            "save_plan" => SavePlanAsync(id, version, input.Tasks ?? throw Invalid("Tasks are required."), actor, key, ct, input.TaskBaselines),
            "publish_plan" => PublishPlanAsync(id, version, Need(input.PlanId, "PlanId"), actor, key, ct),
            "add_message" => AddMessageAsync(id, version, Text(input.Kind, "Kind"), Text(input.Text, "Text"), actor, key, ct),
            "set_task_state" => SetTaskStateAsync(id, version, Need(input.ItemId, "ItemId"), Need(input.Status, "Status"), actor, key, ct),
            "report_result" => RecordEvidenceAsync(id, version, Need(input.ItemId, "ItemId"), input.Evidence ?? throw Invalid("Evidence is required."), actor, key, ct),
            "accept_result" => AcceptEvidenceAsync(id, version, Need(input.ItemId, "ItemId"), Need(input.EvidenceId, "EvidenceId"), input.CheckedCriterionIds ?? throw Invalid("CheckedCriterionIds are required."), actor, key, ct),
            "conclude" => ConcludeAsync(id, version, Text(input.Text, "Text"), input.Limitations ?? string.Empty, actor, key, ct),
            "archive" => ArchiveAsync(id, version, Need(input.Archived, "Archived"), actor, key, ct),
            _ => throw Invalid("Unknown workflow operation. Read the workflow API reference for supported operations.")
        }));
    }
    private static WorkflowCommandResult Reply(MutationReceipt receipt) => new(receipt.RequestId, receipt.Version);
}
