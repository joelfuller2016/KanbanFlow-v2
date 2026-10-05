using KanbanFlow.Models.Workflows;

namespace KanbanFlow.Services.Workflows;

public interface IWorkspaceService
{
    Task<List<RequestSummary>> RequestsAsync(int? projectId = null, CancellationToken ct = default);
    Task<WorkRequest?> GetForIdeaAsync(int ideaId, CancellationToken ct = default);
    Task<WorkRequest?> GetForTaskAsync(int taskId, CancellationToken ct = default);
    Task<WorkflowContext?> ContextAsync(Guid id, CancellationToken ct = default);
    Task<WorkflowCommandResult> ExecuteAsync(WorkflowCommandInput input, WorkflowOptions options, CancellationToken ct = default);
}
