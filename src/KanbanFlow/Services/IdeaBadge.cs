using KanbanFlow.Models;

namespace KanbanFlow.Services;

/// <summary>
/// Pure presentation helpers for the badges and chips on an idea card (#180 Phase 4, #282).
/// Extracted to a static, side-effect-free class so they can be unit-tested without bUnit (the
/// project has no bUnit; real-SQLite + Playwright is the sanctioned no-fake test pattern) — which
/// is the whole reason anything lives here rather than in a <c>.razor</c> <c>@code</c> block, where
/// it would be unreachable from a test. The High-priority badge uses the project's custom
/// <c>bg-orange</c> class (Bootstrap has none) defined in <c>wwwroot/app.css</c>.
/// </summary>
public static class IdeaBadge
{
    /// <summary>Longest tooltip <see cref="PlanPreview"/> will emit before eliding.</summary>
    private const int PlanPreviewMaxLength = 120;

    /// <summary>Tooltip for a plan with no readable content of its own.</summary>
    private const string PlanFallback = "Has a plan";

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

    /// <summary>
    /// The plan's opening line, stripped of markdown chrome and capped, for the Plan chip's tooltip
    /// (#282): <c>"**Problem** Zero agent presence in the UI."</c> → <c>"Problem Zero agent presence
    /// in the UI."</c>
    ///
    /// <para>A heading is a LABEL, not content — on its own it would yield a one-word tooltip
    /// ("Scope") that says nothing — so a heading is held and folded into the first real line that
    /// follows it: <c>"# Scope\nProve the branch renders."</c> → <c>"Scope: Prove the branch
    /// renders."</c></para>
    ///
    /// <para>The most SPECIFIC heading wins. Stacked headings ("# Scope" then "## Sub-scope") used to
    /// pair with <em>each other</em> — the second one was not recognised as a heading because a
    /// heading was already held, so it was treated as the content line and the tooltip came out as
    /// "Scope: Sub-scope": two labels and still no content, on exactly the well-structured plan most
    /// worth previewing. Each heading now simply replaces the last, and the loop keeps going until it
    /// finds a real line. (Gemini, PR #300.)</para>
    ///
    /// <para>A <c>title</c> is a mouse-only affordance; <c>aria-label="Has a plan"</c> carries the
    /// screen reader and the full plan is one Enter away in the modal (accepted gap —
    /// accessibility.md §7).</para>
    /// </summary>
    public static string PlanPreview(string? plan)
    {
        if (string.IsNullOrWhiteSpace(plan))
            return PlanFallback;

        string? heading = null;

        foreach (var rawLine in plan.Split('\n'))
        {
            var trimmed = rawLine.Trim();
            var isHeading = trimmed.StartsWith('#');

            var line = trimmed.Replace("**", "").Replace("`", "")
                              .TrimStart('#', '>', '-', '*', '_', ' ')
                              .Trim();

            if (line.Length == 0)
                continue;

            // Hold it and keep looking. NO `heading is null` guard: that is what made a second,
            // more specific heading masquerade as content (see the remarks above).
            if (isHeading)
            {
                heading = line;
                continue;
            }

            var preview = heading is null ? line : $"{heading}: {line}";

            return preview.Length > PlanPreviewMaxLength
                ? string.Concat(preview.AsSpan(0, PlanPreviewMaxLength).TrimEnd(), "…")
                : preview;
        }

        // A plan that is nothing BUT headings still deserves its most specific one as the tooltip.
        return heading ?? PlanFallback;
    }
}
