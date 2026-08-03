using KanbanFlow.Models;

namespace KanbanFlow.Tests;

/// <summary>
/// Unit tests for the board filter (#112): the pure <see cref="TaskFilter.Matches"/> predicate and
/// the <see cref="TaskFilterState"/> URL round-trip that makes a filtered board shareable.
///
/// These exercise the REAL production types with no fakes. The predicate was extracted out of
/// <c>ProjectView.razor</c> precisely so the AND-combination is provable here without a browser —
/// the e2e scenario in <c>scripts/ui-e2e.mjs</c> then proves the WIRING (that the page passes
/// <c>filteredTasks</c> to the board and that a drag under an active filter still persists the right
/// order), which is the half a unit test cannot see.
///
/// Every clause has at least one test that fails if ONLY that clause is deleted — see the
/// "one clause at a time" block, which is what makes the mutation proof in the PR meaningful.
/// </summary>
public sealed class TaskFilterTests
{
    private static TaskItem Task(
        int id = 1,
        string name = "Task",
        string? description = null,
        TaskPriority priority = TaskPriority.Medium,
        WorkStatus status = WorkStatus.Todo,
        string? assignee = null,
        string? claimedBy = null) =>
        new()
        {
            Id = id,
            Name = name,
            Description = description,
            Priority = priority,
            Status = status,
            Assignee = assignee,
            ClaimedBy = claimedBy,
        };

    // ── The empty filter is a pass-through, never a "match nothing" ────────────────────────────

    [Fact]
    public void EmptyFilter_MatchesEverything_AndIsNotActive()
    {
        Assert.False(TaskFilterState.Empty.IsActive);

        foreach (var status in Enum.GetValues<WorkStatus>())
        {
            foreach (var priority in Enum.GetValues<TaskPriority>())
            {
                Assert.True(TaskFilter.Matches(Task(status: status, priority: priority), TaskFilterState.Empty));
            }
        }
    }

    [Fact]
    public void EmptyPriorityList_DoesNotExcludeAnything()
    {
        // A set-valued filter has two plausible readings of "empty" and only one is usable: an empty
        // priority selection must mean "no priority constraint", not "match nothing" — otherwise the
        // board blanks itself the moment the user unticks their last chip.
        var state = new TaskFilterState { Priorities = [] };
        Assert.False(state.IsActive);
        Assert.True(TaskFilter.Matches(Task(priority: TaskPriority.Low), state));
        Assert.True(TaskFilter.Matches(Task(priority: TaskPriority.Critical), state));
    }

    [Fact]
    public void BlankText_IsTreatedAsNoFilter()
    {
        // "   " reaches this from a user who typed a space and deleted it. It must not become a
        // literal substring search for a space, which would match almost every multi-word title.
        var state = new TaskFilterState { Search = "   ", Assignee = "", ClaimedBy = null };
        Assert.False(state.IsActive);
        Assert.True(TaskFilter.Matches(Task(name: "NoSpacesHere", assignee: null, claimedBy: null), state));
    }

    // ── One clause at a time — each of these fails if ONLY its own clause is removed ───────────

    [Theory]
    [InlineData(TaskPriority.High, true)]
    [InlineData(TaskPriority.Critical, true)]
    [InlineData(TaskPriority.Medium, false)]
    [InlineData(TaskPriority.Low, false)]
    public void PriorityFilter_KeepsOnlySelectedPriorities(TaskPriority priority, bool expected)
    {
        var state = new TaskFilterState { Priorities = [TaskPriority.High, TaskPriority.Critical] };
        Assert.True(state.IsActive);
        Assert.Equal(expected, TaskFilter.Matches(Task(priority: priority), state));
    }

    [Fact]
    public void AssigneeFilter_IsACaseInsensitiveSubstring_AndExcludesUnassigned()
    {
        var state = new TaskFilterState { Assignee = "ali" };
        Assert.True(state.IsActive);
        Assert.True(TaskFilter.Matches(Task(assignee: "Alice"), state));
        Assert.True(TaskFilter.Matches(Task(assignee: "ALICE"), state));
        Assert.False(TaskFilter.Matches(Task(assignee: "Bob"), state));
        // An unassigned task is not "assigned to ali" — a null haystack must FAIL an active clause,
        // not sail through it the way a null needle does.
        Assert.False(TaskFilter.Matches(Task(assignee: null), state));
        Assert.False(TaskFilter.Matches(Task(assignee: "  "), state));
    }

    [Fact]
    public void ClaimedByFilter_IsACaseInsensitiveSubstring_AndExcludesUnclaimed()
    {
        var state = new TaskFilterState { ClaimedBy = "AGENT" };
        Assert.True(state.IsActive);
        Assert.True(TaskFilter.Matches(Task(claimedBy: "agent-7"), state));
        Assert.False(TaskFilter.Matches(Task(claimedBy: "human"), state));
        Assert.False(TaskFilter.Matches(Task(claimedBy: null), state));
    }

    [Fact]
    public void ClaimedByFilter_DoesNotReadTheAssigneeField()
    {
        // Two near-identical string clauses over two different columns: a copy-paste that points
        // both at Assignee compiles, and every other test in this file still passes.
        var state = new TaskFilterState { ClaimedBy = "agent" };
        Assert.False(TaskFilter.Matches(Task(assignee: "agent", claimedBy: null), state));
        Assert.True(TaskFilter.Matches(Task(assignee: null, claimedBy: "agent"), state));
    }

    [Fact]
    public void SearchMatchesName_CaseInsensitively()
    {
        var state = new TaskFilterState { Search = "wIDget" };
        Assert.True(TaskFilter.Matches(Task(name: "Build the Widget"), state));
        Assert.False(TaskFilter.Matches(Task(name: "Build the gadget"), state));
    }

    [Fact]
    public void SearchMatchesDescription_WhenTheNameDoesNot()
    {
        // The one search case that a name-only implementation passes every other test while failing.
        var state = new TaskFilterState { Search = "sqlite" };
        Assert.True(TaskFilter.Matches(Task(name: "Persistence spike", description: "Try SQLite WAL mode"), state));
        Assert.False(TaskFilter.Matches(Task(name: "Persistence spike", description: "Try Postgres"), state));
        Assert.False(TaskFilter.Matches(Task(name: "Persistence spike", description: null), state));
    }

    [Theory]
    [InlineData(WorkStatus.Done, false)]
    [InlineData(WorkStatus.Backlog, true)]
    [InlineData(WorkStatus.Todo, true)]
    [InlineData(WorkStatus.InProgress, true)]
    [InlineData(WorkStatus.Review, true)]
    public void HideDone_ExcludesOnlyDone(WorkStatus status, bool expected)
    {
        var state = new TaskFilterState { HideDone = true };
        Assert.True(state.IsActive);
        Assert.Equal(expected, TaskFilter.Matches(Task(status: status), state));
    }

    [Fact]
    public void HideDoneOff_KeepsDoneTasks()
    {
        // Default OFF. The Done column is real work with a real history; hiding it by default would
        // silently rewrite what the board means on first load.
        Assert.False(TaskFilterState.Empty.HideDone);
        Assert.True(TaskFilter.Matches(Task(status: WorkStatus.Done), TaskFilterState.Empty));
    }

    // ── Combination: the clauses AND together ─────────────────────────────────────────────────

    [Fact]
    public void AllFiltersCombine_WithAnd()
    {
        var state = new TaskFilterState
        {
            Priorities = [TaskPriority.High],
            Assignee = "alice",
            ClaimedBy = "agent",
            Search = "widget",
            HideDone = true,
        };

        // The one task that satisfies every clause.
        TaskItem Match(
            string name = "Ship the widget",
            TaskPriority priority = TaskPriority.High,
            WorkStatus status = WorkStatus.InProgress,
            string? assignee = "Alice",
            string? claimedBy = "agent-7") =>
            Task(name: name, priority: priority, status: status, assignee: assignee, claimedBy: claimedBy);

        Assert.True(TaskFilter.Matches(Match(), state));

        // Each of these differs from Match() in exactly ONE field, so each one proves that field's
        // clause participates in the AND rather than being shadowed by a neighbour.
        Assert.False(TaskFilter.Matches(Match(priority: TaskPriority.Low), state));
        Assert.False(TaskFilter.Matches(Match(assignee: "Bob"), state));
        Assert.False(TaskFilter.Matches(Match(claimedBy: "human"), state));
        Assert.False(TaskFilter.Matches(Match(name: "Ship the gadget"), state));
        Assert.False(TaskFilter.Matches(Match(status: WorkStatus.Done), state));
    }

    [Fact]
    public void Apply_NarrowsTheList_AndPreservesCallerOrder()
    {
        // Order is load-bearing: the page hands the board a list the board then re-orders per
        // column, but `Apply` itself must never reshuffle — a Where that became an OrderBy would
        // quietly override TaskOrdering for the filtered view only.
        var tasks = new List<TaskItem>
        {
            Task(id: 3, name: "gamma widget"),
            Task(id: 1, name: "alpha widget"),
            Task(id: 2, name: "beta gadget"),
            Task(id: 4, name: "delta widget"),
        };

        var filtered = TaskFilter.Apply(tasks, new TaskFilterState { Search = "widget" });

        Assert.Equal(new[] { 3, 1, 4 }, filtered.Select(t => t.Id).ToArray());
    }

    [Fact]
    public void Apply_WithEmptyFilter_ReturnsEveryTask()
    {
        var tasks = new List<TaskItem> { Task(id: 1), Task(id: 2, status: WorkStatus.Done) };
        Assert.Equal(new[] { 1, 2 }, TaskFilter.Apply(tasks, TaskFilterState.Empty).Select(t => t.Id).ToArray());
    }

    // ── URL round-trip (AC 3: a reload restores the exact view) ────────────────────────────────

    [Fact]
    public void EmptyFilter_ProducesAnEmptyQuery()
    {
        // Not "?" and not "?hideDone=false". Clearing the filters must leave a clean URL, which is
        // the assertion the e2e Clear step makes.
        Assert.Equal(string.Empty, TaskFilterState.Empty.ToQuery());
    }

    [Fact]
    public void ToQuery_ThenFromQuery_RoundTripsEveryField()
    {
        var state = new TaskFilterState
        {
            Search = "widget",
            Priorities = [TaskPriority.Critical, TaskPriority.Low],
            Assignee = "alice",
            ClaimedBy = "agent-7",
            HideDone = true,
        };

        var round = TaskFilterState.FromQuery(state.ToQuery());

        Assert.Equal(state.Search, round.Search);
        Assert.Equal(state.Assignee, round.Assignee);
        Assert.Equal(state.ClaimedBy, round.ClaimedBy);
        Assert.Equal(state.HideDone, round.HideDone);
        Assert.Equal(new[] { TaskPriority.Low, TaskPriority.Critical }, round.Priorities.ToArray());
        Assert.True(round.IsActive);
    }

    [Fact]
    public void ToQuery_EscapesValues_AndOrdersPrioritiesByDeclaration()
    {
        var state = new TaskFilterState
        {
            // `&`, `=` and a space are all query-string metacharacters. Unescaped, "a&b" would
            // parse back as two keys and the shared link would silently mean something else.
            Search = "a&b = c",
            Priorities = [TaskPriority.Critical, TaskPriority.Low],
        };

        var query = state.ToQuery();
        Assert.DoesNotContain("a&b", query);
        // Click order was Critical-then-Low; the URL is emitted in declaration order so the same
        // view always has the same link. The comma stays literal — it is a sub-delimiter, legal
        // unescaped in a query value, and a readable link is the point of putting it in the URL.
        Assert.Contains("priority=Low,Critical", query);
        Assert.Equal("a&b = c", TaskFilterState.FromQuery(query).Search);
    }

    [Fact]
    public void FromUri_ReadsTheQueryOffAnAbsoluteUrl()
    {
        var state = TaskFilterState.FromUri("http://localhost:5148/project/12?q=widget&hideDone=true");
        Assert.Equal("widget", state.Search);
        Assert.True(state.HideDone);
    }

    [Fact]
    public void FromUri_WithNoQuery_IsTheEmptyFilter()
    {
        Assert.False(TaskFilterState.FromUri("http://localhost:5148/project/12").IsActive);
    }

    [Theory]
    [InlineData("?priority=High,Critical")]
    [InlineData("priority=High,Critical")]
    [InlineData("?priority=high,CRITICAL")]
    [InlineData("?priority=High&priority=Critical")]
    public void FromQuery_AcceptsEveryShapeAHumanWillType(string query)
    {
        Assert.Equal(new[] { TaskPriority.High, TaskPriority.Critical }, TaskFilterState.FromQuery(query).Priorities.ToArray());
    }

    [Theory]
    [InlineData("?priority=999")]
    [InlineData("?priority=-1")]
    [InlineData("?priority=Urgent")]
    [InlineData("?priority=")]
    public void FromQuery_IgnoresUnparseablePriorities_RatherThanThrowing(string query)
    {
        // Enum.TryParse alone accepts any in-range numeric string ("999"), which is how #89's ghost
        // values got persisted elsewhere. A hand-edited link must degrade to a usable board.
        var state = TaskFilterState.FromQuery(query);
        Assert.Empty(state.Priorities);
        Assert.False(state.IsActive);
    }

    [Fact]
    public void FromQuery_IgnoresUnknownKeys()
    {
        var state = TaskFilterState.FromQuery("?q=widget&somethingElse=1&utm_source=slack");
        Assert.Equal("widget", state.Search);
        Assert.False(state.HideDone);
        Assert.Empty(state.Priorities);
    }

    [Fact]
    public void FromQuery_HideDoneIsOnlyTrueForTrue()
    {
        Assert.True(TaskFilterState.FromQuery("?hideDone=true").HideDone);
        Assert.True(TaskFilterState.FromQuery("?hideDone=TRUE").HideDone);
        Assert.False(TaskFilterState.FromQuery("?hideDone=false").HideDone);
        Assert.False(TaskFilterState.FromQuery("?hideDone=1").HideDone);
        Assert.False(TaskFilterState.FromQuery("?hideDone=").HideDone);
    }

    [Fact]
    public void FromQuery_DeduplicatesRepeatedPriorities()
    {
        Assert.Equal(new[] { TaskPriority.High }, TaskFilterState.FromQuery("?priority=High,High&priority=high").Priorities.ToArray());
    }

    // ── IsActive drives the "no tasks match these filters" empty state ─────────────────────────

    [Fact]
    public void IsActive_IsTrueForEachClauseIndependently()
    {
        Assert.True(new TaskFilterState { Search = "x" }.IsActive);
        Assert.True(new TaskFilterState { Assignee = "x" }.IsActive);
        Assert.True(new TaskFilterState { ClaimedBy = "x" }.IsActive);
        Assert.True(new TaskFilterState { HideDone = true }.IsActive);
        Assert.True(new TaskFilterState { Priorities = [TaskPriority.Low] }.IsActive);
    }
}
