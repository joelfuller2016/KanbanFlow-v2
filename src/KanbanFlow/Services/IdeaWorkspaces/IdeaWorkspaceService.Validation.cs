using System.Text.Json;
using KanbanFlow.Models.IdeaWorkspaces;

namespace KanbanFlow.Services.IdeaWorkspaces;

public sealed partial class IdeaWorkspaceService
{
    private static void ValidateCommandFields(string action, JsonElement payload)
    {
        string[]? allowed = action switch
        {
            "create_component" => ["component", "placement"],
            "update_component" => ["componentId", "type", "title", "content", "url", "taskId"],
            "remove_component" => ["componentId"],
            "add_placement" => ["placement"],
            "remove_placement" => ["placementId"],
            "move_components" => ["placements"],
            "create_connector" => ["connector"],
            "remove_connector" => ["connectorId"],
            "add_comment" => ["componentId", "text", "replyToId"],
            "save_checkpoint" => ["clientId", "sessionId", "objective", "focusedComponentIds", "completedChanges", "openQuestions", "nextActions", "lastAcknowledgedActivityId", "actor", "updatedAt"],
            "propose_plan" => ["title", "items", "expectedProjectPlanRevision"],
            "accept_plan" => ["planRevisionId", "expectedProjectPlanRevision"],
            "link_project" => ["projectId", "expectedCurrentProjectId"],
            "create_project" => ["name"],
            "publish_plan" => ["expectedProjectPlanRevision", "itemIds", "featureId", "featureName"],
            _ => null
        };
        if (allowed is not null) ValidateFields(payload, allowed);
    }

    private static void ValidateFields(JsonElement value, IReadOnlyList<string> allowed)
    {
        foreach (var property in value.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                throw Invalid("unknown_payload_field", $"Field '{property.Name}' is not supported by this operation. It was not saved.");
    }

    private static string NewIdentifier(string? value = null)
        => string.IsNullOrEmpty(value) ? Guid.NewGuid().ToString("N") : value;

    private static void ValidateIdentifier(string? value, string field, int maximum = 200)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl) || value.Any(char.IsWhiteSpace))
            throw Invalid("invalid_identifier", $"{field} must be a nonblank identifier of at most {maximum} characters, without whitespace/control characters.");
    }

    private static void ValidateActor(string? actor)
    {
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 200 || actor.Any(char.IsControl))
            throw Invalid("invalid_actor", "Actor must be a nonblank label of at most 200 characters, without control characters.");
    }

    internal static void ValidateJsonProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw Invalid("duplicate_property", $"Payload repeats property '{property.Name}'.");
                ValidateJsonProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) ValidateJsonProperties(item);
        }
    }

    private static IdeaWorkspaceComponent FindComponent(IdeaWorkspaceDocument document, string id)
    {
        ValidateIdentifier(id, "componentId");
        return document.Components.SingleOrDefault(c => c.Id == id)
            ?? throw NotFound("component_not_found", $"Component '{id}' is not in this workspace.");
    }

    private static IdeaWorkspacePlacement FindPlacement(IdeaWorkspaceDocument document, string id)
    {
        ValidateIdentifier(id, "placementId");
        return document.Placements.SingleOrDefault(p => p.Id == id)
            ?? throw NotFound("placement_not_found", $"Placement '{id}' is not in this workspace.");
    }

    private static void ValidateComponent(IdeaWorkspaceComponent component)
    {
        ValidateIdentifier(component.Id, "component.id");
        if (component.Type is null || !ComponentTypes.Contains(component.Type, StringComparer.Ordinal))
            throw Invalid("invalid_component_type", "Component type is not supported by this workspace.");
        if (string.IsNullOrWhiteSpace(component.Title) || component.Title.Length > 500)
            throw Invalid("invalid_component_title", "Component title must contain text and be at most 500 characters.");
        if (component.Content is null)
            throw Invalid("invalid_component_content", "Component content cannot be null; use empty text to clear it.");
        if (component.Url is not null &&
            (!Uri.TryCreate(component.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
            throw Invalid("invalid_component_url", "Component URL must be an absolute http or https URL.");
    }

    private static void ValidateDocument(IdeaWorkspaceDocument document)
    {
        var components = new Dictionary<string, IdeaWorkspaceComponent>(StringComparer.Ordinal);
        foreach (var component in document.Components)
        {
            ValidateComponent(component);
            if (!components.TryAdd(component.Id, component))
                throw Conflict("component_exists", $"Component '{component.Id}' already exists.");
        }
        var placements = new Dictionary<string, IdeaWorkspacePlacement>(StringComparer.Ordinal);
        foreach (var placement in document.Placements)
        {
            ValidateIdentifier(placement.Id, "placement.id");
            if (!placements.TryAdd(placement.Id, placement))
                throw Conflict("placement_exists", $"Placement '{placement.Id}' already exists.");
            if (!components.ContainsKey(placement.ComponentId))
                throw NotFound("component_not_found", "A placement refers to a component outside this workspace.");
            if (!double.IsFinite(placement.X) || !double.IsFinite(placement.Y) ||
                !double.IsFinite(placement.Width) || !double.IsFinite(placement.Height) ||
                placement.Width <= 0 || placement.Height <= 0)
                throw Invalid("invalid_layout", "Placement coordinates must be finite, and width/height must be positive.");
        }
        foreach (var placement in document.Placements)
        {
            if (placement.ParentId is not { } parentId) continue;
            if (!placements.TryGetValue(parentId, out var parent) || components[parent.ComponentId].Type != "frame")
                throw Invalid("invalid_parent", "A parent must name a frame placement in this workspace.");
            var seen = new HashSet<string>(StringComparer.Ordinal) { placement.Id };
            var current = parent;
            while (true)
            {
                if (!seen.Add(current.Id))
                    throw Invalid("parent_cycle", "Frame grouping cannot contain a parent cycle.");
                if (current.ParentId is null) break;
                if (!placements.TryGetValue(current.ParentId, out var next))
                    throw Invalid("invalid_parent", "A parent must name a frame placement in this workspace.");
                current = next;
            }
        }
        ValidateUnique(document.Connectors.Select(c => c.Id), "connector");
        ValidateUnique(document.Comments.Select(c => c.Id), "comment");
        ValidateUnique(document.PlanRevisions.Select(p => p.Id), "plan revision");
        foreach (var connector in document.Connectors)
            if (!components.ContainsKey(connector.FromComponentId) || !components.ContainsKey(connector.ToComponentId))
                throw Invalid("invalid_connector", "Connector endpoints must exist in this workspace.");
        foreach (var plan in document.PlanRevisions) ValidatePlan(plan);
    }

    private static void ValidateUnique(IEnumerable<string> ids, string kind)
    {
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            ValidateIdentifier(id, kind + ".id");
            if (!unique.Add(id))
                throw Conflict("duplicate_identifier", $"A {kind} with ID '{id}' already exists.");
        }
    }

    private static void ValidatePlan(IdeaWorkspacePlanRevision plan)
    {
        if (plan.Items is null)
            throw Invalid("invalid_plan", "Plan items cannot be null.");
        ValidateUnique(plan.Items.Select(i => i.Id), "plan item");
        var items = plan.Items.ToDictionary(i => i.Id, StringComparer.Ordinal);
        var origins = new HashSet<IdeaWorkspacePlanItemProvenance>();
        foreach (var item in plan.Items)
        {
            if (string.IsNullOrWhiteSpace(item.Title) || item.Title.Length > 500 || item.Description is null ||
                item.AcceptanceCriteria is null || item.DependsOn is null || item.AcceptanceCriteria.Any(v => v is null))
                throw Invalid("invalid_plan_item", "Plan items need a nonblank title (at most 500 characters), text and nonnull criterion/dependency lists.");
            if (item.Provenance is not null && !origins.Add(item.Provenance))
                throw Invalid("duplicate_provenance", "The same canonical item cannot occur twice in a proposal. Keep one item per provenance identity.");
            if (item.DependsOn.Distinct(StringComparer.Ordinal).Count() != item.DependsOn.Count)
                throw Invalid("duplicate_dependency", "A prerequisite may occur only once in an item's dependency list.");
        }
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var complete = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<(IdeaWorkspacePlanItem Item, int DependencyIndex)>();
        foreach (var item in plan.Items)
        {
            if (complete.Contains(item.Id)) continue;
            visiting.Add(item.Id);
            pending.Push((item, 0));
            while (pending.TryPop(out var current))
            {
                if (current.DependencyIndex == current.Item.DependsOn.Count)
                {
                    visiting.Remove(current.Item.Id);
                    complete.Add(current.Item.Id);
                    continue;
                }
                pending.Push((current.Item, current.DependencyIndex + 1));
                var child = current.Item.DependsOn[current.DependencyIndex];
                if (child is null) throw Invalid("missing_dependency", "A dependency cannot be null.");
                if (!items.TryGetValue(child, out var dependency))
                    throw Invalid("missing_dependency", "A plan dependency must name an item in the same revision.");
                if (complete.Contains(child)) continue;
                if (!visiting.Add(child)) throw Invalid("dependency_cycle", "Plan dependencies cannot contain a cycle.");
                pending.Push((dependency, 0));
            }
        }
    }
}
