using System.ComponentModel;
using KanbanFlow.Models.Workflows;
using KanbanFlow.Services;
using KanbanFlow.Services.Workflows;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace KanbanFlow.Mcp;

[McpServerToolType]
public static class WorkspaceMcpTools
{
    [McpServerTool(Name = "list_requests"), Description("List request workflows in the existing board; optionally filter by projectId. Use get_request_context before making a versioned change.")]
    public static async Task<IReadOnlyList<RequestSummary>> ListRequests(WorkspaceService service, int? projectId = null, CancellationToken cancellationToken = default) =>
        await service.RequestsAsync(projectId, cancellationToken);

    [McpServerTool(Name = "get_request_context"), Description("Get the preserved original ask, improved brief and plan revisions, existing idea/project/task IDs, repositories/files, task statuses/comments, reported evidence and conclusions. Current=false marks historical evidence. Links open the same human board.")]
    public static async Task<WorkflowContext> GetRequestContext(WorkspaceService service, Guid requestId, CancellationToken cancellationToken = default) =>
        await service.ContextAsync(requestId, cancellationToken) ?? throw new McpException("Request not found. Use list_requests.");

    [McpServerTool(Name = "get_task_workflow"), Description("Get a real board task's parent request, plan, repository/file references, comments, results and human board links. For older unlinked tasks, use get_task and get_project_context.")]
    public static async Task<WorkflowContext> GetTaskWorkflow(WorkspaceService service, int taskId, CancellationToken cancellationToken = default)
    {
        var request = await service.GetForTaskAsync(taskId, cancellationToken)
            ?? throw new McpException("This task has no request workflow. Its existing board and task API remain available.");
        return await service.ContextAsync(request.Id, cancellationToken)
            ?? throw new McpException("Request context not found.");
    }

    [McpServerTool(Name = "workflow_command")]
    [Description("Write to the same workflow the human manages. input.operation: capture (capture object), adopt_idea (ideaId), create_project (name), register_repository (projectId,name,path), attach_project (projectId), save_brief (brief), save_plan (tasks; for published work also taskBaselines copied from taskId/editToken in the original context read), publish_plan (planId), add_message (kind,text), set_task_state (itemId,status), report_result (itemId,evidence), accept_result (itemId,evidenceId,checkedCriterionIds), conclude (text,limitations), archive (archived). Always provide actor and a unique key. Except the first four operations, also provide requestId and expectedVersion from get_request_context. Retry the identical payload with the same key after a lost response. Refresh on conflict. Task drafts need stable GUID ids; dependencies reference draft ids. For MCP use schema enum names such as Todo and Medium; the REST command uses existing numeric enum values. Reports are caller observations, not proof of execution. No Codex process is launched.")]
    public static async Task<WorkflowCommandResult> WorkflowCommand(WorkspaceService service, WorkflowOptions options,
        WorkflowCommandInput input, CancellationToken cancellationToken = default)
    {
        try { return await service.ExecuteAsync(input, options, cancellationToken); }
        catch (WorkflowException ex) { throw new McpException($"{ex.Code}: {ex.Message}"); }
        catch (ValidationException ex) { throw new McpException(ex.Message); }
        catch (DefinitionOfDoneNotMetException ex) { throw new McpException(ex.Message); }
        catch (ProjectNotFoundException ex) { throw new McpException(ex.Message); }
        catch (IdeaNotFoundException ex) { throw new McpException(ex.Message); }
    }
}
