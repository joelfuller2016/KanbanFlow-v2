using KanbanFlow.Components.Shared;
using KanbanFlow.Models;

namespace KanbanFlow.Components.Pages;

public partial class ProjectView
{
    // Kept only for this project visit. Navigating never writes task/workflow records.
    private readonly List<OutlineItem> outlineHistory = [];
    private int outlineHistoryIndex = -1;
    private int outlineRevealRequest;
    private bool outlineFocusSelection;
    private bool outlineSortablesRefreshPending;

    private OutlineItem? CurrentOutlineItem => outlineHistoryIndex >= 0 &&
        OutlineItemExists(outlineHistory[outlineHistoryIndex]) ? outlineHistory[outlineHistoryIndex] : null;

    private bool OutlineItemExists(OutlineItem item) => item.Kind switch
    {
        OutlineItemKind.Feature => project?.Features.Any(f => f.Id == item.Id) == true,
        OutlineItemKind.Task => allTasks.Any(t => t.Id == item.Id),
        OutlineItemKind.Subtask => allTasks.Any(t => t.Subtasks.Any(s => s.Id == item.Id)),
        _ => false
    };

    private void SelectOutlineItem(OutlineItem item)
    {
        if (!OutlineItemExists(item)) return;
        if (CurrentOutlineItem != item)
        {
            outlineHistory.RemoveRange(outlineHistoryIndex + 1, outlineHistory.Count - outlineHistoryIndex - 1);
            outlineHistory.Add(item);
            outlineHistoryIndex = outlineHistory.Count - 1;
        }
        RevealOutlineItem(item);
    }

    private int FindOutlineHistoryIndex(int direction)
    {
        for (var index = outlineHistoryIndex + direction; index >= 0 && index < outlineHistory.Count; index += direction)
        {
            if (OutlineItemExists(outlineHistory[index])) return index;
        }
        return -1;
    }

    private void NavigateOutline(int direction)
    {
        var index = FindOutlineHistoryIndex(direction);
        if (index < 0) return;
        outlineHistoryIndex = index;
        // Keep keyboard focus on the header button so repeated navigation stays easy.
        RevealOutlineItem(outlineHistory[index]);
    }

    private void RevealOutlineItem(OutlineItem item, bool focus = false)
    {
        if (!OutlineItemExists(item)) return;
        var task = item.Kind switch
        {
            OutlineItemKind.Task => allTasks.First(t => t.Id == item.Id),
            OutlineItemKind.Subtask => allTasks.First(t => t.Subtasks.Any(s => s.Id == item.Id)),
            _ => null
        };
        if (item.Kind == OutlineItemKind.Subtask && task is not null) expandedTasks.Add(task.Id);
        int? featureId = task?.FeatureId ?? item.Id;
        var visited = new HashSet<int>();
        while (featureId is int id && visited.Add(id))
        {
            var feature = project?.Features.FirstOrDefault(f => f.Id == id);
            if (feature is null) break;
            // Revealing a feature needs its ancestors, not its own children.
            if (item.Kind != OutlineItemKind.Feature || id != item.Id)
                outlineSortablesRefreshPending |= expandedFeatures.Add(id);
            featureId = feature.ParentFeatureId;
        }
        outlineFocusSelection = focus;
        outlineRevealRequest++;
    }

    private async Task ShowInOutline()
    {
        // The panel releases its focus trap before invoking this callback.
        await OnDetailClose();
        pendingDetailFocusReturnTaskId = null;
        outlineOpen = true;
        if (CurrentOutlineItem is { } item) RevealOutlineItem(item, focus: true);
    }

    private void OpenOutlineTask(TaskItem task)
    {
        SelectOutlineItem(new(OutlineItemKind.Task, task.Id));
        OpenEditModalForTask(task);
    }

    private void OpenOutlineSubtask(Subtask subtask)
    {
        var task = allTasks.FirstOrDefault(t => t.Id == subtask.TaskItemId);
        if (task is null) return;
        SelectOutlineItem(new(OutlineItemKind.Subtask, subtask.Id));
        OpenEditModalForTask(task);
    }
}
