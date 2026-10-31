using System.Text.Json;
using KanbanFlow.Data;
using KanbanFlow.Models.IdeaWorkspaces;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Services.IdeaWorkspaces;

public sealed partial class IdeaWorkspaceService
{
    private static async Task<(string? ComponentId, string? PlacementId)> ApplyCommandAsync(
        AppDbContext db, IdeaWorkspaceKey key, IdeaWorkspaceDocument document, int? projectId,
        IdeaWorkspaceCommand command, DateTime now, CancellationToken ct)
    {
        var payload = command.Payload;
        ValidateCommandFields(command.Action, payload);
        switch (command.Action)
        {
            case "create_component":
            {
                var component = Read<IdeaWorkspaceComponent>(RequiredObject(payload, "component"));
                component.Id = NewIdentifier(component.Id);
                component.CreatedBy = command.Actor;
                component.CreatedAt = component.UpdatedAt = now;
                ValidateComponent(component);
                if (document.Components.Any(c => c.Id == component.Id))
                    throw Conflict("component_exists", $"Component '{component.Id}' already exists.");
                await ValidateTaskReferenceAsync(db, component, projectId, ct);
                var placementElement = Property(payload, "placement");
                var placement = placementElement is null
                    ? new IdeaWorkspacePlacement()
                    : Read<IdeaWorkspacePlacement>(RequireObject(placementElement.Value, "placement"));
                if (!string.IsNullOrEmpty(placement.ComponentId) && placement.ComponentId != component.Id)
                    throw Invalid("component_mismatch", "A new component's placement must refer to that component.");
                placement.Id = NewIdentifier(placement.Id);
                placement.ComponentId = component.Id;
                document.Components.Add(component);
                document.Placements.Add(placement);
                return (component.Id, placement.Id);
            }
            case "update_component":
            {
                var component = FindComponent(document, RequiredString(payload, "componentId"));
                if (Property(payload, "title") is { } title) component.Title = StringValue(title, "title");
                if (Property(payload, "content") is { } content) component.Content = StringValue(content, "content");
                if (Property(payload, "type") is { } type) component.Type = StringValue(type, "type");
                if (Property(payload, "url") is { } url)
                    component.Url = url.ValueKind == JsonValueKind.Null ? null : StringValue(url, "url");
                if (Property(payload, "taskId") is { } taskId)
                    component.TaskId = taskId.ValueKind == JsonValueKind.Null ? null : IntValue(taskId, "taskId");
                component.UpdatedAt = now;
                ValidateComponent(component);
                await ValidateTaskReferenceAsync(db, component, projectId, ct);
                return (component.Id, null);
            }
            case "remove_component":
            {
                var component = FindComponent(document, RequiredString(payload, "componentId"));
                var componentPlacements = document.Placements.Where(p => p.ComponentId == component.Id)
                    .Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
                if (document.Placements.Any(p => p.ParentId is not null && componentPlacements.Contains(p.ParentId)))
                    throw Conflict("frame_has_children", "Move child placements out of the frame before removing it.");
                document.Components.Remove(component);
                document.Placements.RemoveAll(p => p.ComponentId == component.Id);
                document.Connectors.RemoveAll(c => c.FromComponentId == component.Id || c.ToComponentId == component.Id);
                // Authored discussions remain attached to their historical component ID.
                return (component.Id, null);
            }
            case "add_placement":
            {
                var placement = Read<IdeaWorkspacePlacement>(RequiredObject(payload, "placement"));
                placement.Id = NewIdentifier(placement.Id);
                FindComponent(document, placement.ComponentId);
                document.Placements.Add(placement);
                return (placement.ComponentId, placement.Id);
            }
            case "remove_placement":
            {
                var placement = FindPlacement(document, RequiredString(payload, "placementId"));
                document.Placements.Remove(placement);
                return (placement.ComponentId, placement.Id);
            }
            case "move_components":
            {
                var moves = Property(payload, "placements");
                if (moves is null || moves.Value.ValueKind != JsonValueKind.Array || moves.Value.GetArrayLength() == 0)
                    throw Invalid("invalid_layout", "placements must contain at least one placement update.");
                var changed = new HashSet<string>(StringComparer.Ordinal);
                foreach (var move in moves.Value.EnumerateArray())
                {
                    RequireObject(move, "placement update");
                    ValidateFields(move, ["placementId", "x", "y", "width", "height", "parentId", "zIndex", "order"]);
                    var id = RequiredString(move, "placementId");
                    if (!changed.Add(id))
                        throw Invalid("duplicate_placement", "A layout batch may update each placement only once.");
                    var placement = FindPlacement(document, id);
                    if (Property(move, "x") is { } x) placement.X = DoubleValue(x, "x");
                    if (Property(move, "y") is { } y) placement.Y = DoubleValue(y, "y");
                    if (Property(move, "width") is { } width) placement.Width = DoubleValue(width, "width");
                    if (Property(move, "height") is { } height) placement.Height = DoubleValue(height, "height");
                    if (Property(move, "zIndex") is { } zIndex) placement.ZIndex = IntValue(zIndex, "zIndex");
                    if (Property(move, "order") is { } order) placement.Order = IntValue(order, "order");
                    if (Property(move, "parentId") is { } parent)
                        placement.ParentId = parent.ValueKind == JsonValueKind.Null ? null : StringValue(parent, "parentId");
                }
                return (null, null);
            }
            case "create_connector":
            {
                var connector = Read<IdeaWorkspaceConnector>(RequiredObject(payload, "connector"));
                connector.Id = NewIdentifier(connector.Id);
                FindComponent(document, connector.FromComponentId);
                FindComponent(document, connector.ToComponentId);
                if (connector.FromComponentId == connector.ToComponentId)
                    throw Invalid("self_connector", "A connector must refer to two distinct components.");
                if (connector.Label is not ("related" or "supports" or "answers" or "depends-on" or "contradicts" or "leads-to"))
                    throw Invalid("invalid_connector", "Use a supported semantic connector label.");
                document.Connectors.Add(connector);
                return (null, null);
            }
            case "remove_connector":
            {
                var id = RequiredString(payload, "connectorId");
                var connector = document.Connectors.SingleOrDefault(c => c.Id == id)
                    ?? throw NotFound("connector_not_found", $"Connector '{id}' is not in this workspace.");
                document.Connectors.Remove(connector);
                return (null, null);
            }
            case "add_comment":
            {
                var componentId = OptionalString(payload, "componentId");
                if (componentId is not null) FindComponent(document, componentId);
                var text = RequiredString(payload, "text");
                if (string.IsNullOrWhiteSpace(text))
                    throw Invalid("empty_comment", "A comment cannot be blank.");
                var replyToId = OptionalString(payload, "replyToId");
                if (replyToId is not null)
                {
                    var parent = document.Comments.SingleOrDefault(c => c.Id == replyToId)
                        ?? throw NotFound("comment_not_found", "The parent comment is not in this workspace.");
                    if (parent.ComponentId != componentId)
                        throw Invalid("comment_anchor_mismatch", "A reply must remain on its parent's component or workspace thread.");
                }
                document.Comments.Add(new IdeaWorkspaceComment
                {
                    Id = NewIdentifier(), ComponentId = componentId, ReplyToId = replyToId,
                    Text = text, Actor = command.Actor, CreatedAt = now
                });
                return (componentId, null);
            }
            case "save_checkpoint":
            {
                var checkpoint = Read<IdeaWorkspaceCheckpoint>(payload);
                ValidateIdentifier(checkpoint.ClientId, "clientId", 200);
                ValidateIdentifier(checkpoint.SessionId, "sessionId", 200);
                if (checkpoint.Objective is null || checkpoint.FocusedComponentIds is null ||
                    checkpoint.CompletedChanges is null || checkpoint.OpenQuestions is null || checkpoint.NextActions is null ||
                    checkpoint.CompletedChanges.Any(v => v is null) || checkpoint.OpenQuestions.Any(v => v is null) ||
                    checkpoint.NextActions.Any(v => v is null))
                    throw Invalid("invalid_checkpoint", "Checkpoint text and lists cannot be null.");
                foreach (var id in checkpoint.FocusedComponentIds) FindComponent(document, id);
                var lastActivityId = await db.ActivityEvents
                    .Where(a => a.EntityType == key.AuditEntityType && a.EntityId == key.SourceId)
                    .Select(a => (int?)a.Id).MaxAsync(ct) ?? 0;
                if (checkpoint.LastAcknowledgedActivityId < 0 || checkpoint.LastAcknowledgedActivityId > lastActivityId)
                    throw Invalid("invalid_activity_cursor", "Checkpoint activity position is outside this workspace's saved history.");
                checkpoint.Actor = command.Actor;
                checkpoint.UpdatedAt = now;
                document.Checkpoints.RemoveAll(c => c.ClientId == checkpoint.ClientId && c.SessionId == checkpoint.SessionId);
                document.Checkpoints.Add(checkpoint);
                return (null, null);
            }
            default:
                throw Invalid("unsupported_action", $"Unsupported workspace action '{command.Action}'.");
        }
    }

    private static async Task ValidateTaskReferenceAsync(AppDbContext db, IdeaWorkspaceComponent component, int? projectId, CancellationToken ct)
    {
        if (component.Type != "task-reference")
        {
            if (component.TaskId is not null)
                throw Invalid("invalid_task_reference", "Only task-reference components may contain TaskId.");
            return;
        }
        if (component.TaskId is null or <= 0 || projectId is null)
            throw Invalid("project_required", "A task reference requires a task ID and a linked project.");
        var taskProject = await db.Tasks.AsNoTracking().Where(t => t.Id == component.TaskId)
            .Select(t => (int?)t.Feature.ProjectId).SingleOrDefaultAsync(ct);
        if (taskProject is null)
            throw NotFound("task_not_found", "The referenced task does not exist.");
        if (taskProject != projectId)
            throw Invalid("foreign_task", "The referenced task belongs to another project.");
    }

    private static T Read<T>(JsonElement value) => value.Deserialize<T>(CommandJsonOptions)
        ?? throw Invalid("invalid_payload", "Command data cannot be null.");

    private static JsonElement RequiredObject(JsonElement parent, string name)
        => RequireObject(Property(parent, name) ?? throw Invalid("missing_field", $"'{name}' is required."), name);

    private static JsonElement RequireObject(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object ? value : throw Invalid("invalid_payload", $"'{name}' must be an object.");

    private static string RequiredString(JsonElement parent, string name)
        => StringValue(Property(parent, name) ?? throw Invalid("missing_field", $"'{name}' is required."), name);

    private static string? OptionalString(JsonElement parent, string name)
        => Property(parent, name) is not { } value || value.ValueKind == JsonValueKind.Null ? null : StringValue(value, name);

    private static string StringValue(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.String ? value.GetString()! : throw Invalid("invalid_payload", $"'{name}' must be text.");

    private static int IntValue(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result)
            ? result : throw Invalid("invalid_payload", $"'{name}' must be a 32-bit integer.");

    private static double DoubleValue(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var result) && double.IsFinite(result)
            ? result : throw Invalid("invalid_payload", $"'{name}' must be finite numeric data.");

    private static JsonElement? Property(JsonElement parent, string name)
    {
        foreach (var property in parent.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return null;
    }
}
