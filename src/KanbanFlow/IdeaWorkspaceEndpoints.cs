using KanbanFlow.Services.IdeaWorkspaces;

namespace KanbanFlow;

public static partial class IdeaWorkspaceEndpoints
{
    public static void MapIdeaWorkspaceEndpoints(this WebApplication app)
    {
        MapIdeaWorkspaceWriteEndpoints(app);
        app.MapGet("/api/workspace-capabilities", (IIdeaWorkspaceService service)
            => Results.Ok(service.GetCapabilities()));
        app.MapGet("/api/workspaces", (IIdeaWorkspaceService service, string? kind,
            string? status, string? q, string? cursor, int? limit, CancellationToken ct)
            => Read(() => service.ListAsync(kind, status, q, cursor, limit, ct)));
        app.MapGet("/api/workspaces/{workspaceId}/context", (string workspaceId,
            IIdeaWorkspaceService service, CancellationToken ct)
            => Read(() => service.GetAsync(workspaceId, ct)));
        app.MapGet("/api/workspaces/{workspaceId}/components", (string workspaceId,
            IIdeaWorkspaceService service, string? cursor, int? limit, CancellationToken ct)
            => Read(() => service.ListComponentsAsync(workspaceId, cursor, limit, ct)));
        app.MapGet("/api/workspaces/{workspaceId}/components/{componentId}", (string workspaceId,
            string componentId, IIdeaWorkspaceService service, CancellationToken ct)
            => Read(() => service.GetComponentAsync(workspaceId, componentId, ct)));
        app.MapGet("/api/workspaces/{workspaceId}/changes", (string workspaceId,
            IIdeaWorkspaceService service, string? cursor, int? afterActivityId, int? limit, CancellationToken ct)
            => Read(() => service.GetChangesAsync(workspaceId, cursor, afterActivityId, limit, ct)));
    }

    private static async Task<IResult> Read<T>(Func<Task<T>> operation)
    {
        try
        {
            return Results.Ok(await operation());
        }
        catch (IdeaWorkspaceException ex)
        {
            return Results.Json(new { code = ex.Code, message = ex.Message, status = ex.StatusCode }, statusCode: ex.StatusCode);
        }
    }
}
