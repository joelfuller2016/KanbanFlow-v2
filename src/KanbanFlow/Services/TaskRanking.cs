using KanbanFlow.Models;

namespace KanbanFlow.Services;

/// <summary>
/// A task chosen by <see cref="TaskRanking.RankNextTasks"/>, carrying the derived flags the
/// REST and MCP projections need. For every task in the returned set <see cref="IsBlocked"/> is
/// <c>false</c> — blocked tasks are filtered out of the recommendation list — but the field is
/// kept explicit so callers can project it without re-deriving the value.
/// </summary>
public sealed record RankedTask(TaskItem Task, bool ClaimedByOther, bool IsBlocked, string Reason);

/// <summary>
/// The single, shared "what to work on next" ranker used by BOTH the REST endpoint
/// (<c>GET /api/projects/{id}/next-tasks</c>) and the MCP <c>get_next_tasks</c> tool, so the two
/// can never drift again (#151, #188). Pure and DB-free: the caller loads the data and this ranks
/// it, which keeps the ordering deterministic and unit-testable without a database.
/// </summary>
public static class TaskRanking
{
    /// <summary>
    /// Rank a project's open tasks into a recommended work order. The canonical order is:
    /// tasks claimed by another actor sink to the bottom, then Critical → Low priority, then
    /// in-progress ahead of everything else at the same priority, then ascending Id as a stable
    /// tie-break. Tasks with any unfinished dependency are excluded.
    /// </summary>
    /// <param name="openTasks">
    /// Project tasks already filtered by the caller to <c>Status != Done</c>. Blocked tasks are
    /// still expected here; this method drops them.
    /// </param>
    /// <param name="statusById">
    /// Status of EVERY task in the project (not just the open ones), used to decide whether a
    /// dependency is complete. A dependency whose id is absent from this map counts as unfinished.
    /// </param>
    /// <param name="dependsOnByTask">
    /// Lookup from a task id to the ids of the tasks it depends on (its blockers). Tasks with no
    /// dependencies simply yield an empty sequence.
    /// </param>
    /// <param name="actor">
    /// Optional actor label (trimmed before comparison). A task claimed by a DIFFERENT actor is
    /// de-prioritised. When <c>null</c>/blank, EVERY claimed task is treated as claimed-by-other and
    /// sinks (a claim can never equal a null actor) — matching historic MCP behaviour; see remarks.
    /// </param>
    /// <param name="limit">Maximum number of results; clamped to the range 1–50.</param>
    /// <remarks>
    /// A task is "claimed by another actor" when it has a non-blank <see cref="TaskItem.ClaimedBy"/>
    /// that does not equal <paramref name="actor"/> (case-insensitive). With <paramref name="actor"/>
    /// null, any claimed task therefore counts as claimed-by-other and sinks — matching the historic
    /// MCP behaviour. Unclaimed tasks are never de-prioritised.
    /// </remarks>
    /// <param name="now">
    /// Current time, used only to decide whether another actor's claim has gone stale (#429).
    /// Injected rather than read from the clock so the boundary is testable without sleeping.
    /// Defaults to <see cref="DateTime.UtcNow"/>.
    /// </param>
    /// <param name="claimTtl">
    /// How long a claim stays fresh. A claim older than this stops de-prioritising its task, so a
    /// crashed agent can no longer park work permanently. Defaults to
    /// <see cref="ClaimPolicy.DefaultTtl"/>.
    /// </param>
    public static IReadOnlyList<RankedTask> RankNextTasks(
        IReadOnlyCollection<TaskItem> openTasks,
        IReadOnlyDictionary<int, WorkStatus> statusById,
        ILookup<int, int> dependsOnByTask,
        string? actor,
        int limit,
        DateTime? now = null,
        TimeSpan? claimTtl = null)
    {
        ArgumentNullException.ThrowIfNull(openTasks);
        ArgumentNullException.ThrowIfNull(statusById);
        ArgumentNullException.ThrowIfNull(dependsOnByTask);

        var effectiveNow = now ?? DateTime.UtcNow;
        var effectiveTtl = claimTtl ?? ClaimPolicy.DefaultTtl;
        if (effectiveTtl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(claimTtl), "Claim TTL must be positive.");

        var clampedLimit = Math.Clamp(limit, 1, 50);
        // Normalize the actor exactly as ClaimedBy is normalized on write (trim, blank→null), so an
        // actor like "alice " matches a task claimed by "alice" and is NOT wrongly de-prioritised (#151 review).
        var normalizedActor = string.IsNullOrWhiteSpace(actor) ? null : actor.Trim();

        return openTasks
            .Select(task =>
            {
                // A dependency is unfinished if its target is not yet Done, or is missing entirely
                // (e.g. a cross-project or dangling edge) — either way the task is not ready.
                var hasOpenBlocker = dependsOnByTask[task.Id].Any(dependsOnId =>
                    !statusById.TryGetValue(dependsOnId, out var status) || status != WorkStatus.Done);
                // A STALE claim no longer de-prioritises the task (#429). Without this, one
                // crashed agent parks a task at the bottom of every other actor's list forever —
                // ClaimedByOther is the primary sort key, so the penalty is maximal and permanent.
                var claimIsStale = ClaimPolicy.IsStale(task.ClaimedBy, task.ClaimedAt, effectiveNow, effectiveTtl);
                var claimedByOther = !string.IsNullOrWhiteSpace(task.ClaimedBy)
                    && !string.Equals(task.ClaimedBy, normalizedActor, StringComparison.OrdinalIgnoreCase)
                    && !claimIsStale;
                return (Task: task, HasOpenBlocker: hasOpenBlocker, ClaimedByOther: claimedByOther, ClaimIsStale: claimIsStale);
            })
            .Where(x => !x.HasOpenBlocker)
            .OrderBy(x => x.ClaimedByOther)                              // unclaimed / own first
            .ThenByDescending(x => x.Task.Priority)                     // Critical → Low
            .ThenBy(x => x.Task.Status == WorkStatus.InProgress ? 0 : 1) // in-progress first
            .ThenBy(x => x.Task.Id)                                     // stable, oldest first
            .Take(clampedLimit)
            .Select(x => new RankedTask(x.Task, x.ClaimedByOther, IsBlocked: false,
                BuildReason(x.Task, x.ClaimedByOther, x.ClaimIsStale)))
            .ToList();
    }

    /// <summary>
    /// Human-readable explanation of why a task is recommended. Merged from the former REST
    /// <c>BuildNextTaskReason</c> and MCP <c>BuildReason</c> so both surfaces read identically:
    /// it names the priority, flags in-progress work, confirms there are no open blockers, and
    /// notes when a task is claimed by another actor (or unclaimed).
    /// </summary>
    private static string BuildReason(TaskItem task, bool claimedByOther, bool claimIsStale)
    {
        var parts = new List<string> { $"{task.Priority} priority" };
        if (task.Status == WorkStatus.InProgress) parts.Add("already in progress");
        parts.Add("no open blockers");
        if (claimedByOther) parts.Add($"claimed by {task.ClaimedBy} (deprioritised)");
        else if (claimIsStale && !string.IsNullOrWhiteSpace(task.ClaimedBy))
            parts.Add($"claim by {task.ClaimedBy} is stale (available)");
        else if (string.IsNullOrWhiteSpace(task.ClaimedBy)) parts.Add("unclaimed");
        return "Top because: " + string.Join(", ", parts) + ".";
    }
}
