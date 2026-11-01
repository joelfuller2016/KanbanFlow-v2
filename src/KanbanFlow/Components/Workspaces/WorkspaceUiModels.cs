using System.Text.Json;
using KanbanFlow.Models.IdeaWorkspaces;
using KanbanFlow.Services.IdeaWorkspaces;

namespace KanbanFlow.Components.Workspaces;

public sealed record WorkspaceSelection(string? ComponentId, string? PlacementId = null);
public readonly record struct WorkspaceUiScope(string WorkspaceId, int Generation);
public sealed record WorkspaceUiCommand(string Action, JsonElement Payload,
    long? ExpectedRevision = null, string? DraftSection = null, string? WorkspaceId = null)
{
    public static WorkspaceUiCommand Create(string action, object payload, long? revision = null, string? section = null)
        => new(action, JsonSerializer.SerializeToElement(payload, Json), revision, section);

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

public sealed record WorkspaceContentDraft
{
    public string Key { get; init; } = "$new";
    public string? ComponentId { get; init; }
    public long BaseRevision { get; init; }
    public string Type { get; init; } = "note";
    public string Title { get; init; } = "";
    public string Content { get; init; } = "";
    public string? Url { get; init; }
    public int? TaskId { get; init; }
    public string OriginalTitle { get; init; } = "";
    public string OriginalContent { get; init; } = "";
    public bool Dirty { get; init; }

    public static string KeyForComponent(string? componentId) => componentId switch { null => "$new", "$new" => "", _ => componentId };

    public static WorkspaceContentDraft From(IdeaWorkspaceComponent component, long revision)
        => new() { Key = KeyForComponent(component.Id), ComponentId = component.Id, BaseRevision = revision,
            Type = component.Type, Title = component.Title, Content = component.Content,
            Url = component.Url, TaskId = component.TaskId,
            OriginalTitle = component.Title, OriginalContent = component.Content };
}

public sealed record WorkspaceLayoutDraft
{
    public string PlacementId { get; init; } = "";
    public long BaseRevision { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; } = 320;
    public double Height { get; init; } = 220;
    public string? ParentId { get; init; }
    public int Order { get; init; }
    public int ZIndex { get; init; }
    public bool Dirty { get; init; }
    public static WorkspaceLayoutDraft From(IdeaWorkspacePlacement placement, long revision)
        => new() { PlacementId = placement.Id, BaseRevision = revision,
            X = placement.X, Y = placement.Y, Width = placement.Width, Height = placement.Height,
            ParentId = placement.ParentId, Order = placement.Order, ZIndex = placement.ZIndex };
}

public sealed record WorkspaceDiscussionDraft(string Text = "", string? ComponentId = null, string? ReplyToId = null);
public sealed record WorkspaceConnectorDraft(string FromComponentId = "", string ToComponentId = "", string Label = "related");
public sealed record WorkspacePlanItemDraft
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string AcceptanceCriteria { get; init; } = "";
    public List<string> DependsOn { get; init; } = [];
    public List<string>? OriginalAcceptanceCriteria { get; init; }
    public IdeaWorkspacePlanItemProvenance? Provenance { get; init; }
}
public sealed record WorkspacePlanDraft
{
    public string Title { get; init; } = "";
    public List<WorkspacePlanItemDraft> Items { get; init; } = [];
    public long? BaseRevision { get; init; }
    public long? BaseProjectPlanRevision { get; init; }
}
public sealed record WorkspaceCheckpointDraft
{
    public string Objective { get; init; } = "";
    public string CompletedChanges { get; init; } = "";
    public string OpenQuestions { get; init; } = "";
    public string NextActions { get; init; } = "";
    public List<string> FocusedComponentIds { get; init; } = [];
    public List<string>? OriginalCompletedChanges { get; init; }
    public List<string>? OriginalOpenQuestions { get; init; }
    public List<string>? OriginalNextActions { get; init; }

    public static WorkspaceCheckpointDraft From(IdeaWorkspaceCheckpoint checkpoint) => new()
    {
        Objective = checkpoint.Objective, CompletedChanges = string.Join('\n', checkpoint.CompletedChanges),
        OpenQuestions = string.Join('\n', checkpoint.OpenQuestions), NextActions = string.Join('\n', checkpoint.NextActions),
        FocusedComponentIds = [.. checkpoint.FocusedComponentIds], OriginalCompletedChanges = [.. checkpoint.CompletedChanges],
        OriginalOpenQuestions = [.. checkpoint.OpenQuestions], OriginalNextActions = [.. checkpoint.NextActions]
    };
}
public sealed record WorkspacePendingCommand(IdeaWorkspaceCommand Command, string? DraftSection, JsonElement? DraftAtSend = null);
public sealed record WorkspaceDeliveryDraft
{
    public bool AssociationKnown { get; init; }
    public int? AssociationProjectId { get; init; }
    public int? TargetProjectId { get; init; }
    public string ProjectName { get; init; } = "";
    public List<string>? SelectedItemIds { get; init; }
    public int? FeatureId { get; init; }
    public string FeatureName { get; init; } = "";
}
public sealed record WorkspaceBrowserDraft
{
    public string? SelectedComponentId { get; init; }
    public string? SelectedPlacementId { get; init; }
    public Dictionary<string, WorkspaceContentDraft> ContentDrafts { get; init; } = [];
    public Dictionary<string, WorkspaceLayoutDraft> LayoutDrafts { get; init; } = [];
    public WorkspaceDiscussionDraft Discussion { get; init; } = new();
    public WorkspaceConnectorDraft Connector { get; init; } = new();
    public WorkspacePlanDraft Plan { get; init; } = new();
    public WorkspaceCheckpointDraft Checkpoint { get; init; } = new();
    public WorkspaceDeliveryDraft Delivery { get; init; } = new();
    public WorkspacePendingCommand? Pending { get; init; }

    public WorkspaceBrowserDraft NormalizeContentIdentity()
    {
        var contents = ContentDrafts;
        if (contents.TryGetValue("$new", out var legacy) && legacy.ComponentId == "$new")
        {
            if (contents.ContainsKey(""))
                throw new JsonException("Two stored browser buffers use the saved component's draft key. Their original contents have been retained for explicit recovery.");
            contents = new Dictionary<string, WorkspaceContentDraft>(contents, contents.Comparer);
            contents.Remove("$new");
            contents[""] = legacy with { Key = "" };
        }
        var pending = Pending;
        if (pending is { DraftSection: "content:$new" } && pending.Command.Action is "update_component" or "remove_component"
            && IsSpecialComponent(pending.Command.Payload))
        {
            var snapshot = pending.DraftAtSend;
            if (snapshot is { ValueKind: JsonValueKind.Object } saved && IsSpecialComponent(saved))
                snapshot = RemapSnapshotKey(saved);
            // A legacy command without its comparison snapshot must never clear a newer buffer.
            else if (snapshot is null) snapshot = JsonSerializer.SerializeToElement<object?>(null);
            pending = pending with { DraftSection = "content:", DraftAtSend = snapshot };
        }
        return ReferenceEquals(contents, ContentDrafts) && ReferenceEquals(pending, Pending)
            ? this : this with { ContentDrafts = contents, Pending = pending };
    }

    private static bool IsSpecialComponent(JsonElement value)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty("componentId", out var id)
            && id.ValueKind == JsonValueKind.String && id.GetString() == "$new";

    private static JsonElement RemapSnapshotKey(JsonElement saved)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            var found = false;
            foreach (var property in saved.EnumerateObject())
            {
                if (property.NameEquals("key")) { writer.WriteString(property.Name, ""); found = true; }
                else property.WriteTo(writer);
            }
            if (!found) writer.WriteString("key", "");
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    public void Validate()
    {
        static void Require(bool valid, string section)
        {
            if (!valid) throw new JsonException($"The stored browser draft has an incompatible {section}. Its original text has been retained.");
        }
        Require(ContentDrafts is not null && LayoutDrafts is not null, "draft collection");
        Require(Discussion is not null && Connector is not null && Plan is not null && Checkpoint is not null && Delivery is not null, "draft section");
        foreach (var (key, value) in ContentDrafts!)
            Require(value is not null && key == value.Key && value.Title is not null && value.Content is not null
                && value.Type is not null && value.OriginalTitle is not null && value.OriginalContent is not null, "content draft");
        foreach (var (key, value) in LayoutDrafts!)
            Require(value is not null && key == value.PlacementId && double.IsFinite(value.X) && double.IsFinite(value.Y)
                && double.IsFinite(value.Width) && double.IsFinite(value.Height), "layout draft");
        Require(Discussion!.Text is not null && Connector!.FromComponentId is not null
            && Connector.ToComponentId is not null && Connector.Label is not null, "discussion or connection draft");
        Require(Plan!.Title is not null && Plan.Items is not null, "plan draft");
        foreach (var item in Plan.Items!)
            Require(item is not null && item.Id is not null && item.Title is not null && item.Description is not null
                && item.AcceptanceCriteria is not null && item.DependsOn is not null && item.DependsOn.All(id => id is not null)
                && (item.OriginalAcceptanceCriteria is null || item.OriginalAcceptanceCriteria.All(line => line is not null)), "plan item");
        Require(Plan.Items!.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() == Plan.Items.Count, "plan item identities");
        Require(Checkpoint!.Objective is not null && Checkpoint.CompletedChanges is not null && Checkpoint.OpenQuestions is not null
            && Checkpoint.NextActions is not null && Checkpoint.FocusedComponentIds is not null
            && Checkpoint.FocusedComponentIds.All(id => id is not null)
            && (Checkpoint.OriginalCompletedChanges is null || Checkpoint.OriginalCompletedChanges.All(line => line is not null))
            && (Checkpoint.OriginalOpenQuestions is null || Checkpoint.OriginalOpenQuestions.All(line => line is not null))
            && (Checkpoint.OriginalNextActions is null || Checkpoint.OriginalNextActions.All(line => line is not null)), "checkpoint draft");
        Require(Delivery!.ProjectName is not null && Delivery.FeatureName is not null
            && (Delivery.AssociationProjectId is null || Delivery.AssociationProjectId > 0)
            && (Delivery.SelectedItemIds is null || Delivery.SelectedItemIds.All(id => id is not null)), "delivery draft");
        if (Pending is { } pending)
            Require(pending.Command is { } command && command.OperationId is not null && command.Action is not null
                && command.Actor is not null && command.Payload.ValueKind == JsonValueKind.Object, "pending operation");
    }
}
public sealed record WorkspaceBrowserSession(string ClientId, string SessionId, WorkspaceBrowserDraft? Draft);
public sealed record WorkspaceBrowserDraftRecovery(string? BackupKey, string? Raw);

public static class WorkspaceUiText
{
    public static int NextCanvasLayer(IdeaWorkspaceDocument document)
        => (int)Math.Min(int.MaxValue, 1L + document.Placements.Where(p => p.ParentId is null).Select(p => p.ZIndex).DefaultIfEmpty().Max());
    public static List<string> Lines(string text) => text.Length == 0 ? [] : [.. text.Replace("\r\n", "\n").Split('\n')];
    public static string Url(string workspaceId)
    {
        var key = IdeaWorkspaceKey.Parse(workspaceId);
        return $"/ideas/workspace/{key.Kind}/{key.SourceId}";
    }
}
