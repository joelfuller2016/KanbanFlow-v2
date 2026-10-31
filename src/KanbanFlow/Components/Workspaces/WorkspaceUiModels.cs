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

    public static WorkspaceContentDraft From(IdeaWorkspaceComponent component, long revision)
        => new() { Key = component.Id, ComponentId = component.Id, BaseRevision = revision,
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
}
public sealed record WorkspacePendingCommand(IdeaWorkspaceCommand Command, string? DraftSection, JsonElement? DraftAtSend = null);
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
    public WorkspacePendingCommand? Pending { get; init; }
}
public sealed record WorkspaceBrowserSession(string ClientId, string SessionId, WorkspaceBrowserDraft? Draft);

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
