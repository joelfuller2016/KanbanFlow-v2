using KanbanFlow.Models;

namespace KanbanFlow.Services;

/// <summary>
/// Derived completion counters for a project or for one feature subtree (#125).
/// </summary>
/// <param name="Completed">Tasks whose status is <see cref="WorkStatus.Done"/>.</param>
/// <param name="InProgress">Tasks whose status is <see cref="WorkStatus.InProgress"/>.</param>
/// <param name="Blocked">
/// UNFINISHED tasks with at least one open blocker in the supplied lookup. A Done task is never
/// blocked — nothing is left for its blocker to hold up — which is also how
/// <c>GET /api/projects/{id}/next-tasks</c> counts <c>blockedTaskCount</c>, so the two surfaces
/// agree. Otherwise independent of status: a blocked task is ALSO counted in its status bucket,
/// so this is a cross-cut and never a sixth status.
/// </param>
/// <param name="Total">Every task in scope, at any status.</param>
/// <param name="PercentComplete">
/// <c>Completed / Total</c> as a percentage rounded to one decimal — a NUMBER, never a formatted
/// string, because agents parse it. Exactly <c>0.0</c> (never <c>NaN</c>) when <paramref name="Total"/> is 0.
/// </param>
public sealed record ProjectProgress(
    int Completed, int InProgress, int Blocked, int Total, double PercentComplete)
{
    /// <summary>The all-zero progress of an empty scope.</summary>
    public static readonly ProjectProgress Empty = new(0, 0, 0, 0, 0.0);

    /// <summary>
    /// The only sanctioned way to build a progress record: it derives
    /// <see cref="PercentComplete"/> from the counters, so a value produced here can never
    /// disagree with its own counts. (The positional constructor stays public because
    /// System.Text.Json needs it to round-trip the record; prefer this factory in app code.)
    /// Rounding is <see cref="Math.Round(double, int)"/>'s default to-even, so an exact midpoint
    /// such as 1-of-16 (6.25) surfaces as 6.2, not 6.3.
    /// </summary>
    public static ProjectProgress From(int completed, int inProgress, int blocked, int total) =>
        new(completed, inProgress, blocked, total,
            total > 0 ? Math.Round(100.0 * completed / total, 1) : 0.0);
}

/// <summary>
/// A feature reduced to the only two fields the rollup needs: its id and its parent. Keeps the
/// calculator free of EF entities so callers can feed it a cheap SQL projection.
/// </summary>
public readonly record struct ProgressFeature(int Id, int? ParentFeatureId);

/// <summary>
/// A task reduced to the only three fields the counters need: its id (to test the blocker
/// lookup), its owning feature, and its status.
/// </summary>
public readonly record struct ProgressTask(int Id, int FeatureId, WorkStatus Status);

/// <summary>
/// The computed progress for a whole project plus every feature in it.
/// </summary>
/// <param name="Project">Progress over EVERY task in the project, at any feature depth.</param>
/// <param name="ByFeatureId">
/// Per-feature progress, each covering the feature's own tasks plus all of its descendants'.
/// </param>
public sealed record ProjectProgressReport(
    ProjectProgress Project,
    IReadOnlyDictionary<int, ProjectProgress> ByFeatureId)
{
    /// <summary>
    /// Progress for one feature, or <see cref="ProjectProgress.Empty"/> when the id was not part
    /// of the calculated set — so a projection can never NRE on an unexpected feature.
    /// </summary>
    public ProjectProgress ForFeature(int featureId) =>
        ByFeatureId.TryGetValue(featureId, out var progress) ? progress : ProjectProgress.Empty;
}

/// <summary>
/// The single, shared "how much of this is done?" calculator (#125). Every surface — the Home
/// list, <c>/project/{id}</c>, <c>GET /api/projects/{id}</c>, <c>GET /api/projects/{id}/context</c>
/// and MCP — reads its numbers from here, so they can never report different values for the same
/// project. Pure and DB-free: the caller loads the rows, this counts them, which keeps it
/// unit-testable without a database or a browser.
/// </summary>
/// <remarks>
/// The feature rollup is recursive (a feature's progress includes all descendant features'
/// tasks) but NOT quadratic. <c>Components/Shared/ProjectOutline.razor</c> rescans the whole
/// feature set once per node; this builds a parent → children adjacency map ONCE and then folds
/// bottom-up, memoising each feature, for O(features + tasks) total work. The fold is iterative
/// (explicit stack, so deep nesting cannot overflow) and cycle-guarded: <see cref="Feature"/>
/// self-nests via <see cref="Feature.ParentFeatureId"/> and although cycles are rejected on write
/// elsewhere, this calculator must terminate — and must not double-count — even if that guarantee
/// is ever violated.
/// </remarks>
public static class ProjectProgressCalculator
{
    /// <summary>
    /// Count a project's tasks once and roll them up through the feature tree.
    /// </summary>
    /// <param name="features">
    /// Every feature in the project, distinct by <see cref="ProgressFeature.Id"/>. A feature whose
    /// parent is absent from this set is treated as a root of its own subtree.
    /// </param>
    /// <param name="tasks">
    /// Every task in the project. A task whose <see cref="ProgressTask.FeatureId"/> matches no
    /// supplied feature still counts toward the PROJECT totals — it is simply unreachable from
    /// the feature tree.
    /// </param>
    /// <param name="blockedTaskIds">
    /// Ids of the tasks that have at least one open (not-yet-Done) blocker. Supplied by the caller
    /// so the blocked count reuses the lookup the caller already built, rather than re-querying.
    /// The caller need not filter out finished tasks — <see cref="ProjectProgress.Blocked"/> owns
    /// the "a Done task is not blocked" rule so it is applied identically for every caller.
    /// </param>
    public static ProjectProgressReport Calculate(
        IEnumerable<ProgressFeature> features,
        IEnumerable<ProgressTask> tasks,
        IEnumerable<int> blockedTaskIds)
    {
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(blockedTaskIds);

        var blocked = blockedTaskIds as IReadOnlySet<int> ?? blockedTaskIds.ToHashSet();
        var featureList = features as IReadOnlyList<ProgressFeature> ?? features.ToList();

        // ── one pass over the tasks: project totals + each feature's OWN counters ──
        // `tasks` is enumerated exactly ONCE, on purpose — callers pass a lazy LINQ projection.
        // Do not add a second pass over it.
        var ownCounters = new Dictionary<int, Counters>(featureList.Count);
        var projectCounters = default(Counters);
        foreach (var task in tasks)
        {
            var counters = Counters.For(task.Status, blocked.Contains(task.Id));
            projectCounters += counters;
            ownCounters[task.FeatureId] = ownCounters.TryGetValue(task.FeatureId, out var existing)
                ? existing + counters
                : counters;
        }

        // ── the adjacency map, built ONCE (never rescan the feature set per node) ──
        var childrenByParent = new Dictionary<int, List<int>>();
        var seenFeatureIds = new HashSet<int>(featureList.Count);
        foreach (var feature in featureList)
        {
            // Distinct-by-Id is the documented contract, but a repeated id would put the same
            // child in a parent's list twice and inflate the parent's rollup. Same reasoning as
            // the cycle guard below: never trust a caller-side guarantee to keep this total.
            if (!seenFeatureIds.Add(feature.Id)) continue;
            if (feature.ParentFeatureId is not int parentId) continue;
            if (!childrenByParent.TryGetValue(parentId, out var siblings))
                childrenByParent[parentId] = siblings = [];
            siblings.Add(feature.Id);
        }

        // ── bottom-up fold, memoised, iterative, cycle-guarded ──
        var memo = new Dictionary<int, Counters>(featureList.Count);
        var onPath = new HashSet<int>();
        var stack = new Stack<Frame>();

        foreach (var feature in featureList)
        {
            if (memo.ContainsKey(feature.Id)) continue;

            onPath.Add(feature.Id);
            stack.Push(new Frame(feature.Id, 0));

            while (stack.Count > 0)
            {
                var frame = stack.Pop();
                childrenByParent.TryGetValue(frame.Id, out var children);

                // Descend into the first child that is neither already folded nor an ancestor on
                // the current path. Resuming from NextChild keeps the whole fold linear.
                var descended = false;
                while (children is not null && frame.NextChild < children.Count)
                {
                    var childId = children[frame.NextChild];
                    if (memo.ContainsKey(childId) || onPath.Contains(childId))
                    {
                        frame.NextChild++;
                        continue;
                    }

                    stack.Push(frame);
                    onPath.Add(childId);
                    stack.Push(new Frame(childId, 0));
                    descended = true;
                    break;
                }

                if (descended) continue;

                // Every child is folded (or is a cycle back onto the current path, which
                // contributes nothing — that is what stops a cycle double-counting).
                var accumulated = ownCounters.GetValueOrDefault(frame.Id);
                if (children is not null)
                {
                    foreach (var childId in children)
                    {
                        if (memo.TryGetValue(childId, out var childCounters))
                            accumulated += childCounters;
                    }
                }

                memo[frame.Id] = accumulated;
                onPath.Remove(frame.Id);
            }
        }

        var byFeatureId = new Dictionary<int, ProjectProgress>(memo.Count);
        foreach (var (featureId, counters) in memo)
            byFeatureId[featureId] = counters.ToProgress();

        return new ProjectProgressReport(projectCounters.ToProgress(), byFeatureId);
    }

    /// <summary>A node being folded, plus how far through its children the fold has walked.</summary>
    private record struct Frame(int Id, int NextChild);

    /// <summary>Additive counters — the summable form of <see cref="ProjectProgress"/>.</summary>
    private readonly record struct Counters(int Completed, int InProgress, int Blocked, int Total)
    {
        public static Counters For(WorkStatus status, bool hasOpenBlocker) => new(
            Completed: status == WorkStatus.Done ? 1 : 0,
            InProgress: status == WorkStatus.InProgress ? 1 : 0,
            // A finished task is not blocked, however stale its dependency rows are. Matching
            // next-tasks' blockedTaskCount here is what stops two surfaces reporting two numbers.
            Blocked: hasOpenBlocker && status != WorkStatus.Done ? 1 : 0,
            Total: 1);

        public static Counters operator +(Counters left, Counters right) => new(
            left.Completed + right.Completed,
            left.InProgress + right.InProgress,
            left.Blocked + right.Blocked,
            left.Total + right.Total);

        public ProjectProgress ToProgress() => ProjectProgress.From(Completed, InProgress, Blocked, Total);
    }
}
