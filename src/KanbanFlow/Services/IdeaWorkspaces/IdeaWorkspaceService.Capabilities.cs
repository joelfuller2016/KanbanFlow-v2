namespace KanbanFlow.Services.IdeaWorkspaces;

public sealed partial class IdeaWorkspaceService
{
    public IdeaWorkspaceCapabilities GetCapabilities()
    {
        var components = ComponentTypes.ToDictionary(type => type, type => new IdeaWorkspacePropertyDescriptor(
            type == "task-reference" ? ["id", "type", "title", "content", "url", "taskId"] : ["id", "type", "title", "content", "url"],
            type == "task-reference" ? ["type", "title", "content", "url", "taskId"] : ["type", "title", "content", "url"],
            type == "task-reference" ? ["type", "title", "taskId"] : type == "note" ? ["title"] : ["type", "title"],
            new Dictionary<string, string>
            {
                ["id"] = "Optional on creation; generated when omitted. Unique in the workspace, at most 200 characters, without whitespace/control characters.",
                ["type"] = $"Use '{type}' for this component schema. Generic creation defaults to note when type is omitted.",
                ["title"] = "Nonblank text, at most 500 characters.",
                ["content"] = "Text, default empty. Empty clears; null is invalid. Authored whitespace and full text are preserved.",
                ["url"] = "Optional absolute http/https URL. Null clears it; empty URL is invalid.",
                ["taskId"] = type == "task-reference"
                    ? "Positive existing task ID belonging to the linked project; current task data is read from Tasks."
                    : "Must be absent/null for this component type."
            }, ["createdBy", "createdAt", "updatedAt"]), StringComparer.Ordinal);
        var placement = new IdeaWorkspacePropertyDescriptor(
            ["id", "componentId", "parentId", "x", "y", "width", "height", "zIndex", "order"],
            ["parentId", "x", "y", "width", "height", "zIndex", "order"], ["componentId"],
            new Dictionary<string, string>
            {
                ["id"] = "Optional on creation, generated when omitted. Independent of content/component ID.",
                ["componentId"] = "Existing component in this workspace; automatically supplied by create_component.",
                ["parentId"] = "Frame PLACEMENT ID in the same workspace, or null for the canvas. Parent cycles are rejected.",
                ["x/y"] = "Finite numbers. Relative to parent placement, or canvas-relative at the root; defaults 0/0.",
                ["width/height"] = "Positive finite numbers; defaults 280/180.",
                ["zIndex/order"] = "32-bit integers; defaults 0/0. Order is also used for accessible reading."
            }, []);
        var actions = new Dictionary<string, IdeaWorkspaceActionDescriptor>(StringComparer.Ordinal)
        {
            ["create_workspace"] = new(["title", "body", "plan", "tags", "priority", "projectId"], [], ["operationId", "actor"],
                "Create an idea with a title OR open an existing project by projectId. Project opens cannot contain nonnull source-edit fields. Body/plan omitted or null stay null; empty and whitespace are preserved. Retries use a global creation receipt."),
            ["create_component"] = Action(["component", "placement"], ["component"], "Adds typed content and a separate placement. Placement is optional and defaults are discoverable above."),
            ["update_component"] = Action(["componentId", "type", "title", "content", "url", "taskId"], ["componentId"], "Omitted fields are unchanged. Content empty clears, content null fails; URL null clears. Validate the resulting type and task reference."),
            ["remove_component"] = Action(["componentId"], ["componentId"], "Explicitly removes content/placements/connectors; complete prior text remains in audit. Move child placements out of a frame first."),
            ["add_placement"] = Action(["placement"], ["placement"], "Places existing workspace content again; does not copy the content record."),
            ["remove_placement"] = Action(["placementId"], ["placementId"], "Removes an appearance only; component content remains. Reparent children before removing their parent frame appearance."),
            ["move_components"] = Action(["placements"], ["placements"], "Nonempty atomic update list. Each entry requires placementId and may set parentId/x/y/width/height/zIndex/order. IDs may occur only once in a batch. Child coordinates are parent-relative."),
            ["create_connector"] = Action(["connector"], ["connector"], "Connector requires fromComponentId/toComponentId. Semantic label: related, supports, answers, depends-on, contradicts, leads-to. A drawn connector does not create a task dependency."),
            ["remove_connector"] = Action(["connectorId"], ["connectorId"], "Removes an existing connector in this workspace."),
            ["add_comment"] = Action(["componentId", "text", "replyToId"], ["text"], "Nonblank complete text. A null/omitted componentId anchors a workspace thread. Replies stay on the parent's anchor."),
            ["save_checkpoint"] = Action(["clientId", "sessionId", "objective", "focusedComponentIds", "completedChanges", "openQuestions", "nextActions", "lastAcknowledgedActivityId"], ["clientId", "sessionId"], "Per-client/session checkpoint. Lists default empty; text is retained exactly; focus IDs must exist and activity position cannot exceed workspace history."),
            ["propose_plan"] = Action(["title", "items", "expectedProjectPlanRevision"], ["title", "items"], "Creates a new draft. Items have id/title/description/acceptanceCriteria/dependsOn. Dependencies must be same-revision IDs and acyclic. Publication fields are server-owned."),
            ["accept_plan"] = Action(["planRevisionId", "expectedProjectPlanRevision"], ["planRevisionId"], "Accepts a draft while retaining prior versions. Linked-idea acceptance requires the canonical project plan workflow and is not silently accepted locally.")
        };
        return new IdeaWorkspaceCapabilities(1, ComponentTypes, actions.Keys.ToArray(),
            DefaultPageSize, MaximumPageSize, (int)SnapshotLifetime.TotalSeconds,
            "ParentId names a frame placement; child X/Y are parent-relative.",
            "Summary endpoints explicitly omit body text. Context/detail preserve complete authored text; unknown requested fields fail visibly.")
        {
            ComponentProperties = components,
            PlacementProperties = placement,
            ActionPayloads = actions
        };
    }

    private static IdeaWorkspaceActionDescriptor Action(string[] fields, string[] required, string rules)
        => new(fields, required, ["operationId", "expectedRevision", "actor"], rules);
}
