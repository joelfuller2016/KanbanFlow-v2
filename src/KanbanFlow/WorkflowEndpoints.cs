using KanbanFlow.Models.Workflows;
using KanbanFlow.Services;
using KanbanFlow.Services.Workflows;

namespace KanbanFlow;

public static class WorkflowEndpoints
{
    public static void MapWorkflowEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/workflows").WithTags("Request workflows");
        group.MapGet("/", async (int? projectId, IWorkspaceService service, CancellationToken ct) =>
            TypedResults.Ok(await service.RequestsAsync(projectId, ct)))
            .WithName("ListWorkflows").WithSummary("List request workflows, optionally for one existing project.");
        group.MapGet("/{id:guid}", async (Guid id, IWorkspaceService service, CancellationToken ct) =>
            await Context(service, id, ct)).WithName("GetWorkflow").Produces<WorkflowContext>().Produces(404)
            .WithSummary("Read original request, brief and plan revisions, live tasks, comments, repositories and results.");
        group.MapGet("/idea/{ideaId:int}", async (int ideaId, IWorkspaceService service, CancellationToken ct) =>
        {
            var request = await service.GetForIdeaAsync(ideaId, ct);
            return request is null ? Results.NotFound() : await Context(service, request.Id, ct);
        }).WithName("GetIdeaWorkflow").Produces<WorkflowContext>().Produces(404)
            .WithSummary("Find the request belonging to an existing idea. Use adopt_idea for older ideas without a workflow.");
        group.MapGet("/task/{taskId:int}", async (int taskId, IWorkspaceService service, CancellationToken ct) =>
        {
            var request = await service.GetForTaskAsync(taskId, ct);
            return request is null ? Results.NotFound() : await Context(service, request.Id, ct);
        }).WithName("GetTaskWorkflow").Produces<WorkflowContext>().Produces(404)
            .WithSummary("Read the parent request and workflow context of a real board task.");
        group.MapPost("/commands", async (WorkflowCommandInput input, IWorkspaceService service, WorkflowOptions options, CancellationToken ct) =>
        {
            try { return Results.Ok(await service.ExecuteAsync(input, options, ct)); }
            catch (WorkflowException ex) { return Problem(ex.Code == "not_found" ? 404 : ex.Code == "conflict" ? 409 : 422, ex.Message); }
            catch (ValidationException ex) { return Problem(422, ex.Message); }
            catch (DefinitionOfDoneNotMetException ex) { return Problem(422, ex.Message); }
            catch (ProjectNotFoundException ex) { return Problem(404, ex.Message); }
            catch (IdeaNotFoundException ex) { return Problem(404, ex.Message); }
        }).WithName("ExecuteWorkflowCommand").Produces<WorkflowCommandResult>().ProducesProblem(404).ProducesProblem(409).ProducesProblem(422)
            .WithSummary("Apply a workflow operation with an actor, retry key and expected request version.")
            .WithDescription("Operations: capture, adopt_idea, create_project, register_repository, attach_project, save_brief, save_plan, publish_plan, add_message, set_task_state, report_result, accept_result, conclude, archive. See docs/request-workflow.md for fields and examples. Existing task editing remains available through the existing task API.");
    }

    private static async Task<IResult> Context(IWorkspaceService service, Guid id, CancellationToken ct) =>
        await service.ContextAsync(id, ct) is { } context ? Results.Ok(context) : Results.NotFound();
    private static IResult Problem(int status, string message) => Results.Problem(statusCode: status,
        title: status == 404 ? "Workflow not found" : status == 409 ? "Workflow conflict" : "Workflow could not be saved",
        detail: message);
}
