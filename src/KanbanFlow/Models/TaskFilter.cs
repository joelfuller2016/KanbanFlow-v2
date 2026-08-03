using Microsoft.AspNetCore.WebUtilities;

namespace KanbanFlow.Models;

/// <summary>
/// The board filter bar's state, as one immutable value (#112).
/// </summary>
/// <remarks>
/// Deliberately a plain value type with no Blazor, EF or HTTP dependency: it is the argument to the
/// pure <see cref="TaskFilter.Matches"/> predicate, which is what makes the combination logic
/// unit-testable without a browser or a database.
///
/// It also owns its own query-string encoding, because the URL is the ONLY persistence this filter
/// has (a reload must restore the exact view) and a round-trip that splits its two halves across two
/// files drifts the first time a field is added. <see cref="FromQuery"/> restores every field of a
/// state that <see cref="ToQuery"/> wrote, and <c>TaskFilterTests</c> asserts that — in that
/// direction only. The reverse is deliberately NOT an identity: <c>FromQuery</c> is lenient (it
/// accepts <c>?priority=high</c>, repeated keys, unknown keys and untrimmed values) precisely so a
/// hand-edited link still works, and <c>ToQuery</c> re-emits the one canonical spelling.
/// </remarks>
public sealed record TaskFilterState
{
    /// <summary>The empty filter: matches every task. <see cref="IsActive"/> is <c>false</c>.</summary>
    public static readonly TaskFilterState Empty = new();

    /// <summary>Priorities to keep. EMPTY means "no priority constraint", never "match nothing".</summary>
    public IReadOnlyCollection<TaskPriority> Priorities { get; init; } = [];

    /// <summary>Case-insensitive substring over <see cref="TaskItem.Assignee"/>.</summary>
    public string? Assignee { get; init; }

    /// <summary>Case-insensitive substring over <see cref="TaskItem.ClaimedBy"/>.</summary>
    public string? ClaimedBy { get; init; }

    /// <summary>Case-insensitive substring over <see cref="TaskItem.Name"/> OR <see cref="TaskItem.Description"/>.</summary>
    public string? Search { get; init; }

    /// <summary>Hide <see cref="WorkStatus.Done"/> tasks. Off by default — the Done column is real work.</summary>
    public bool HideDone { get; init; }

    /// <summary>
    /// Whether anything is actually narrowing the board. Drives the "no tasks match these filters"
    /// empty state, which must stay distinguishable from "this column is genuinely empty".
    /// </summary>
    public bool IsActive =>
        Priorities.Count > 0
        || !string.IsNullOrWhiteSpace(Assignee)
        || !string.IsNullOrWhiteSpace(ClaimedBy)
        || !string.IsNullOrWhiteSpace(Search)
        || HideDone;

    // ── URL round-trip ───────────────────────────────────────────────────────────────────────────

    /// <summary>Query-string key for <see cref="Search"/>.</summary>
    public const string SearchKey = "q";

    /// <summary>Query-string key for <see cref="Priorities"/> (comma-separated ENUM NAMES).</summary>
    public const string PriorityKey = "priority";

    /// <summary>Query-string key for <see cref="Assignee"/>.</summary>
    public const string AssigneeKey = "assignee";

    /// <summary>Query-string key for <see cref="ClaimedBy"/>.</summary>
    public const string ClaimedByKey = "claimedBy";

    /// <summary>Query-string key for <see cref="HideDone"/>.</summary>
    public const string HideDoneKey = "hideDone";

    /// <summary>
    /// Render this state as a query string (leading <c>?</c>, or <see cref="string.Empty"/> when the
    /// filter is empty) with every value URL-escaped.
    /// </summary>
    /// <remarks>
    /// Priorities are emitted in declaration order rather than click order so the URL of a given
    /// view is stable — otherwise picking High-then-Low and Low-then-High produce two different
    /// "shareable" links to the identical board.
    /// </remarks>
    public string ToQuery()
    {
        var parts = new List<KeyValuePair<string, string?>>();

        if (!string.IsNullOrWhiteSpace(Search))
            parts.Add(new(SearchKey, Search.Trim()));

        if (Priorities.Count > 0)
        {
            var ordered = Enum.GetValues<TaskPriority>().Where(Priorities.Contains).Select(p => p.ToString());
            parts.Add(new(PriorityKey, string.Join(',', ordered)));
        }

        if (!string.IsNullOrWhiteSpace(Assignee))
            parts.Add(new(AssigneeKey, Assignee.Trim()));

        if (!string.IsNullOrWhiteSpace(ClaimedBy))
            parts.Add(new(ClaimedByKey, ClaimedBy.Trim()));

        if (HideDone)
            parts.Add(new(HideDoneKey, "true"));

        return parts.Count == 0 ? string.Empty : QueryHelpers.AddQueryString(string.Empty, parts);
    }

    /// <summary>
    /// Parse a filter out of a URL (absolute or relative). Unknown keys, unparseable priority names
    /// and a malformed query are all IGNORED rather than thrown: a hand-edited or truncated link
    /// must degrade to a usable board, never to an error page.
    /// </summary>
    public static TaskFilterState FromUri(string uri)
    {
        var queryStart = uri.IndexOf('?');
        return FromQuery(queryStart < 0 ? string.Empty : uri[queryStart..]);
    }

    /// <summary>Parse a filter out of a query string (with or without the leading <c>?</c>).</summary>
    public static TaskFilterState FromQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Empty;

        var parsed = QueryHelpers.ParseQuery(query);

        var priorities = new List<TaskPriority>();
        if (parsed.TryGetValue(PriorityKey, out var priorityValues))
        {
            // Accept repeats (?priority=Low&priority=High) as well as one comma-separated value,
            // since both shapes are things a human hand-editing the URL will produce.
            foreach (var token in priorityValues
                         .SelectMany(value => (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
            {
                // TryParse alone accepts numeric strings like "999" (any in-range int), so pair it
                // with IsDefined — the same guard the task API uses on its enum edges.
                if (Enum.TryParse<TaskPriority>(token, ignoreCase: true, out var priority)
                    && Enum.IsDefined(priority)
                    && !priorities.Contains(priority))
                {
                    priorities.Add(priority);
                }
            }
        }

        return new TaskFilterState
        {
            Search = Single(parsed, SearchKey),
            Priorities = priorities,
            Assignee = Single(parsed, AssigneeKey),
            ClaimedBy = Single(parsed, ClaimedByKey),
            HideDone = string.Equals(Single(parsed, HideDoneKey), "true", StringComparison.OrdinalIgnoreCase),
        };

        static string? Single(Dictionary<string, Microsoft.Extensions.Primitives.StringValues> parsed, string key)
        {
            if (!parsed.TryGetValue(key, out var values))
                return null;

            var value = values.FirstOrDefault();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}

/// <summary>
/// The board's filter predicate (#112) — pure, static, and the single place the AND-combination
/// lives, so <c>TaskFilterTests</c> can prove each clause is load-bearing without a browser.
/// </summary>
/// <remarks>
/// This is a HOST-PAGE concern applied at the <c>Items</c> call site, never a parameter on
/// <c>KanbanBoard</c>: that shell is generic over <c>&lt;TItem, TStatus&gt;</c> and shared with
/// /ideas, and it already documents the contract this relies on — "Items may be a
/// filtered/partial subset; the host resolves the anchor against
/// persisted order". Filtering therefore cannot corrupt a drag: <c>OnCardMoved</c>'s third argument
/// is the next RENDERED card's stable identity, and <c>MoveOnBoardAsync</c> resolves it against the
/// complete persisted column.
/// </remarks>
public static class TaskFilter
{
    /// <summary>Whether a task survives every active clause of <paramref name="state"/> (AND).</summary>
    public static bool Matches(TaskItem task, TaskFilterState state)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(state);

        if (state.HideDone && task.Status == WorkStatus.Done)
            return false;

        if (state.Priorities.Count > 0 && !state.Priorities.Contains(task.Priority))
            return false;

        if (!Contains(task.Assignee, state.Assignee))
            return false;

        if (!Contains(task.ClaimedBy, state.ClaimedBy))
            return false;

        if (!string.IsNullOrWhiteSpace(state.Search)
            && !Contains(task.Name, state.Search)
            && !Contains(task.Description, state.Search))
        {
            return false;
        }

        return true;
    }

    /// <summary>Narrow a task list with <see cref="Matches"/>, preserving the caller's order.</summary>
    public static List<TaskItem> Apply(IEnumerable<TaskItem> tasks, TaskFilterState state)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        return [.. tasks.Where(task => Matches(task, state))];
    }

    /// <summary>
    /// Case-insensitive substring test where a blank NEEDLE passes (the clause is inactive) and a
    /// null/blank HAYSTACK fails an active clause (an unassigned task is not "assigned to bob").
    /// </summary>
    private static bool Contains(string? haystack, string? needle)
    {
        if (string.IsNullOrWhiteSpace(needle))
            return true;

        return !string.IsNullOrWhiteSpace(haystack)
            && haystack.Contains(needle.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
