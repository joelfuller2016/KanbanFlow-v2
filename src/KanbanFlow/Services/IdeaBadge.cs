using KanbanFlow.Models;

namespace KanbanFlow.Services;

/// <summary>
/// Pure Bootstrap-badge class mappings for the Ideas inbox UI (#180 Phase 4).
/// Extracted to a static, side-effect-free helper so the badge palette can be unit-tested
/// without bUnit (the project has no bUnit; real-SQLite + Playwright is the sanctioned
/// no-fake test pattern). The High-priority badge uses the project's custom
/// <c>bg-orange</c> class (Bootstrap has none) defined in <c>wwwroot/app.css</c>.
/// </summary>
public static class IdeaBadge
{
    /// <summary>Bootstrap badge classes for an <see cref="IdeaStatus"/>.</summary>
    public static string StatusClass(IdeaStatus status) => status switch
    {
        IdeaStatus.New => "bg-primary",
        IdeaStatus.Triaging => "bg-warning text-dark",
        IdeaStatus.Promoted => "bg-success",
        IdeaStatus.Archived => "bg-secondary",
        IdeaStatus.Rejected => "bg-danger",
        _ => "bg-secondary"
    };

    /// <summary>
    /// Bootstrap badge classes for a <see cref="TaskPriority"/>. Matches the app-wide priority
    /// palette used by <c>KanbanCard.razor</c> and <c>ProjectOutline.razor</c> so colours are
    /// consistent everywhere. High uses the project's custom <c>bg-orange</c> (Bootstrap has none).
    /// </summary>
    public static string PriorityClass(TaskPriority priority) => priority switch
    {
        TaskPriority.Critical => "bg-danger",
        TaskPriority.High => "bg-orange text-white",
        TaskPriority.Medium => "bg-warning text-dark",
        TaskPriority.Low => "bg-secondary",
        _ => "bg-secondary"
    };
}
