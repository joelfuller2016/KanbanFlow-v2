using System.Text.Json;
using System.Text.Json.Serialization;
using KanbanFlow.Services.IdeaWorkspaces;

namespace KanbanFlow;

public static partial class IdeaWorkspaceEndpoints
{
    private static readonly JsonSerializerOptions WorkspaceRequestJson = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private static void MapIdeaWorkspaceWriteEndpoints(WebApplication app)
    {
        app.MapPost("/api/workspaces", (HttpRequest request, IIdeaWorkspaceService service, CancellationToken ct)
            => Read(async () => await service.CreateAsync(await ReadRequestAsync<IdeaWorkspaceCreate>(request, ct), ct)))
            .Accepts<IdeaWorkspaceCreate>("application/json").Produces<IdeaWorkspaceCreateResult>();
        app.MapPost("/api/workspaces/{workspaceId}/commands", (string workspaceId, HttpRequest request,
            IIdeaWorkspaceService service, CancellationToken ct)
            => Read(async () => await service.ExecuteAsync(workspaceId, await ReadRequestAsync<IdeaWorkspaceCommand>(request, ct), ct)))
            .Accepts<IdeaWorkspaceCommand>("application/json").Produces<IdeaWorkspaceMutationResult>();

        MapWorkspaceWrite(app, "POST", "/api/workspaces/{workspaceId}/components", "create_component", ["component", "placement"]);
        MapWorkspaceWrite(app, "PATCH", "/api/workspaces/{workspaceId}/components/{componentId}", "update_component",
            ["type", "title", "content", "url", "taskId"], "componentId");
        MapWorkspaceWrite(app, "DELETE", "/api/workspaces/{workspaceId}/components/{componentId}", "remove_component", [], "componentId");
        MapWorkspaceWrite(app, "POST", "/api/workspaces/{workspaceId}/placements", "add_placement", ["placement"]);
        MapWorkspaceWrite(app, "DELETE", "/api/workspaces/{workspaceId}/placements/{placementId}", "remove_placement", [], "placementId");
        MapWorkspaceWrite(app, "POST", "/api/workspaces/{workspaceId}/layout-operations", "move_components", ["placements"]);
        MapWorkspaceWrite(app, "POST", "/api/workspaces/{workspaceId}/connectors", "create_connector", ["connector"]);
        MapWorkspaceWrite(app, "DELETE", "/api/workspaces/{workspaceId}/connectors/{connectorId}", "remove_connector", [], "connectorId");
        MapWorkspaceWrite(app, "POST", "/api/workspaces/{workspaceId}/comments", "add_comment", ["componentId", "text", "replyToId"]);
        MapWorkspaceWrite(app, "POST", "/api/workspaces/{workspaceId}/checkpoints", "save_checkpoint",
            ["clientId", "sessionId", "objective", "focusedComponentIds", "completedChanges", "openQuestions", "nextActions", "lastAcknowledgedActivityId"]);
        MapWorkspaceWrite(app, "POST", "/api/workspaces/{workspaceId}/plan-revisions", "propose_plan",
            ["title", "items", "expectedProjectPlanRevision"]);
        MapWorkspaceWrite(app, "POST", "/api/workspaces/{workspaceId}/plan-revisions/{planRevisionId}/accept", "accept_plan",
            ["expectedProjectPlanRevision"], "planRevisionId");
    }

    private static void MapWorkspaceWrite(WebApplication app, string method, string route, string action,
        string[] payloadFields, string? routeField = null)
    {
        app.MapMethods(route, [method], (string workspaceId, HttpRequest request,
            IIdeaWorkspaceService service, CancellationToken ct)
            => Read(async () => await service.ExecuteAsync(workspaceId,
                await ReadConvenienceCommandAsync(request, action, payloadFields, routeField, ct), ct)))
            .Accepts<JsonElement>("application/json").Produces<IdeaWorkspaceMutationResult>();
    }

    private static async Task<T> ReadRequestAsync<T>(HttpRequest request, CancellationToken ct)
    {
        var envelope = await ReadEnvelopeAsync(request, ct);
        try
        {
            return envelope.Deserialize<T>(WorkspaceRequestJson)
                ?? throw RequestError("invalid_payload", "The request cannot be null.");
        }
        catch (JsonException)
        {
            throw RequestError("invalid_payload", "The request has an unknown field, a missing required field, or an invalid field value.");
        }
    }

    private static async Task<IdeaWorkspaceCommand> ReadConvenienceCommandAsync(HttpRequest request,
        string action, string[] payloadFields, string? routeField, CancellationToken ct)
    {
        var envelope = await ReadEnvelopeAsync(request, ct);
        var allowed = payloadFields.Concat(["operationId", "expectedRevision", "actor"])
            .Concat(routeField is null ? [] : new[] { routeField }).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var field in envelope.EnumerateObject())
            if (!allowed.Contains(field.Name))
                throw RequestError("unknown_payload_field", $"Field '{field.Name}' is not supported by this operation. It was not saved.");
        var operationId = RequiredText(envelope, "operationId");
        var actor = RequiredText(envelope, "actor");
        var revision = Find(envelope, "expectedRevision");
        if (revision is null || revision.Value.ValueKind != JsonValueKind.Number || !revision.Value.TryGetInt64(out var expectedRevision))
            throw RequestError("invalid_revision", "expectedRevision is required and must be a 64-bit integer.");
        var pathId = routeField is null ? null : request.RouteValues[routeField]?.ToString();
        if (routeField is not null && Find(envelope, routeField) is { } bodyId &&
            (bodyId.ValueKind != JsonValueKind.String || bodyId.GetString() != pathId))
            throw RequestError("target_mismatch", "The body target differs from the target in the URL.");

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var field in envelope.EnumerateObject())
                if (payloadFields.Contains(field.Name, StringComparer.OrdinalIgnoreCase)) field.WriteTo(writer);
            if (routeField is not null) writer.WriteString(routeField, pathId);
            writer.WriteEndObject();
        }
        using var payload = JsonDocument.Parse(buffer.ToArray());
        return new IdeaWorkspaceCommand(operationId, expectedRevision, action, payload.RootElement.Clone(), actor);
    }

    private static async Task<JsonElement> ReadEnvelopeAsync(HttpRequest request, CancellationToken ct)
    {
        if (!request.HasJsonContentType())
            throw RequestError("invalid_content_type", "Workspace writes require application/json.");
        JsonElement root;
        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw RequestError("invalid_json", "The request must contain a valid JSON object.");
        }
        if (root.ValueKind != JsonValueKind.Object)
            throw RequestError("invalid_payload", "The request must be a JSON object.");
        IdeaWorkspaceService.ValidateJsonProperties(root);
        var bodyActor = Find(root, "actor");
        if (bodyActor is { } value && value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            throw RequestError("invalid_actor", "actor must be a text label or null.");
        var actor = request.Headers.TryGetValue("X-KanbanFlow-Actor", out var header)
            ? header.ToString() : bodyActor is { ValueKind: JsonValueKind.String } text ? text.GetString()! : "api";
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var field in root.EnumerateObject())
                if (!field.Name.Equals("actor", StringComparison.OrdinalIgnoreCase)) field.WriteTo(writer);
            writer.WriteString("actor", actor);
            writer.WriteEndObject();
        }
        using var normalized = JsonDocument.Parse(buffer.ToArray());
        return normalized.RootElement.Clone();
    }

    private static string RequiredText(JsonElement envelope, string name)
    {
        var field = Find(envelope, name);
        return field is { ValueKind: JsonValueKind.String } text ? text.GetString()!
            : throw RequestError("missing_field", $"{name} is required and must be text.");
    }

    private static JsonElement? Find(JsonElement envelope, string name)
    {
        foreach (var field in envelope.EnumerateObject())
            if (field.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return field.Value;
        return null;
    }

    private static IdeaWorkspaceException RequestError(string code, string message) => new(400, code, message);
}
