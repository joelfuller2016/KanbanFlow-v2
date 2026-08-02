namespace KanbanFlow.Models;

/// <summary>
/// Canonical ordering rules for <see cref="TaskItem"/>.
/// </summary>
/// <remarks>
/// Historical task ranks are integral and were allocated per feature, so equal ranks across
/// features are normal. Within an integral rank, spread those legacy rows across the following
/// unit interval by Id. This preserves the former <c>SortOrder, Id</c> order exactly while giving
/// an identity-anchored move numeric space between ordinary legacy rows. The raw persisted rank
/// remains the secondary key because IEEE-754 rounding can collapse an expanded legacy position
/// onto the next integral rank at large values. Fractional ranks are already effective positions.
/// </remarks>
public static class TaskOrdering
{
    private const double IdDivisor = (double)int.MaxValue + 1d;

    /// <summary>Get the task's effective rank used by every board, outline, API, and MCP reader.</summary>
    public static double EffectiveRank(TaskItem task)
        => EffectiveRank(task.SortOrder, task.Id);

    /// <summary>Get an effective rank from its persisted value and stable task identity.</summary>
    public static double EffectiveRank(double sortOrder, int taskId)
    {
        if (taskId <= 0 || !double.IsFinite(sortOrder) || sortOrder != Math.Truncate(sortOrder))
            return sortOrder;

        return sortOrder + (taskId / IdDivisor);
    }

    /// <summary>Order tasks by effective rank, persisted rank, then stable identity.</summary>
    public static IOrderedEnumerable<TaskItem> Order(IEnumerable<TaskItem> tasks)
        => tasks
            .OrderBy(EffectiveRank)
            .ThenBy(task => task.SortOrder)
            .ThenBy(task => task.Id);

    /// <summary>Compare a candidate persisted rank/id pair with an existing task.</summary>
    public static int Compare(double sortOrder, int taskId, TaskItem other)
    {
        var rankComparison = EffectiveRank(sortOrder, taskId).CompareTo(EffectiveRank(other));
        if (rankComparison != 0)
            return rankComparison;

        var rawRankComparison = sortOrder.CompareTo(other.SortOrder);
        return rawRankComparison != 0 ? rawRankComparison : taskId.CompareTo(other.Id);
    }
}
