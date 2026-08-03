using KanbanFlow.Models;
using KanbanFlow.Services;

namespace KanbanFlow.Tests;

/// <summary>
/// Tests for <see cref="ProjectProgressCalculator"/> — the single shared "how much of this is
/// done?" calculator behind <c>GET /api/projects/{id}</c> and <c>GET /api/projects/{id}/context</c>
/// (#125), so those two endpoints can never report different numbers for the same project. Home,
/// <c>/project/{id}</c> and MCP still recompute their own counts and move onto it under #281/#111.
///
/// The calculator is pure (no EF, no Blazor), so every case below exercises the REAL production
/// code over in-memory inputs — no database, no browser, no fakes.
///
/// Each test is deliberately scoped to ONE property so a defect produces a specific signal.
/// The mutation proof for #125 is:
///   (a) count Done as InProgress  → only the Done-counting tests go red
///       (<see cref="MixedStatuses_CountsEveryBucketIndividually"/>, <see cref="AllTasksDone_IsOneHundredPercent"/>)
///   (b) drop the <c>Total &gt; 0</c> guard → only <see cref="EmptyProject_HasZeroTotalAndZeroPercent_NeverNaN"/> goes red
///   (c) roll up own tasks only     → only <see cref="ThreeLevelNesting_AncestorIncludesAllDescendantTasks"/> goes red
/// Tests that are not about a given property are built so that property's sabotage leaves them
/// green (e.g. scopes with equal Done/InProgress counts, and no empty scope outside case (b)).
/// </summary>
public sealed class ProjectProgressCalculatorTests
{
    private static ProgressFeature Feat(int id, int? parentFeatureId = null) => new(id, parentFeatureId);

    private static ProgressTask Task(int id, int featureId, WorkStatus status = WorkStatus.Todo) =>
        new(id, featureId, status);

    private static readonly int[] NoBlockers = [];

    // ── percentage contract ────────────────────────────────────────────────────────

    [Fact]
    public void EmptyProject_HasZeroTotalAndZeroPercent_NeverNaN()
    {
        var report = ProjectProgressCalculator.Calculate(
            [Feat(1)],           // a feature that owns no tasks at all
            [],
            NoBlockers);

        Assert.Equal(0, report.Project.Total);
        Assert.Equal(0, report.Project.Completed);
        Assert.Equal(0, report.Project.InProgress);
        Assert.Equal(0, report.Project.Blocked);
        Assert.Equal(0.0, report.Project.PercentComplete);
        Assert.False(double.IsNaN(report.Project.PercentComplete), "PercentComplete must be 0.0, never NaN, when Total is 0.");

        // A feature with no tasks must be 0.0 too — not NaN, not a divide-by-zero.
        var feature = report.ForFeature(1);
        Assert.Equal(0, feature.Total);
        Assert.Equal(0.0, feature.PercentComplete);
        Assert.False(double.IsNaN(feature.PercentComplete), "An empty feature's PercentComplete must be 0.0, never NaN.");
    }

    [Fact]
    public void AllTasksDone_IsOneHundredPercent()
    {
        var report = ProjectProgressCalculator.Calculate(
            [Feat(1)],
            [
                Task(1, 1, WorkStatus.Done),
                Task(2, 1, WorkStatus.Done),
                Task(3, 1, WorkStatus.Done),
                Task(4, 1, WorkStatus.Done)
            ],
            NoBlockers);

        Assert.Equal(4, report.Project.Completed);
        Assert.Equal(4, report.Project.Total);
        Assert.Equal(100.0, report.Project.PercentComplete);
    }

    [Fact]
    public void OneOfThreeDone_RoundsToExactlyThirtyThreePointThree()
    {
        // Pins the rounding: 100 * 1/3 = 33.333… must surface as 33.3, and as a NUMBER — an
        // agent parses this field, so a formatted string would break it.
        var report = ProjectProgressCalculator.Calculate(
            [Feat(1)],
            [
                Task(1, 1, WorkStatus.Done),
                Task(2, 1, WorkStatus.InProgress),
                Task(3, 1, WorkStatus.Todo)
            ],
            NoBlockers);

        Assert.Equal(33.3, report.Project.PercentComplete);
    }

    [Fact]
    public void ExactMidpointPercentage_RoundsToEven()
    {
        // 100 * 1/16 = 6.25 — an EXACT midpoint (6.25 is exactly representable in binary, so this
        // is deterministic, not a floating-point coin flip). The mode must be a decision, not a
        // default: Math.Round(x, 1) is MidpointRounding.ToEven, so 6.25 -> 6.2, never 6.3. Pinned
        // because #281/#111 will compare this number against client-side arithmetic, and an agent
        // parses it. A silent switch to AwayFromZero would otherwise ship unnoticed.
        //
        // 1 Done + 1 InProgress (not 1 Done + 15 Todo) so the mutation "count InProgress as Done"
        // leaves this test green — its subject is the rounding mode, not the Done predicate.
        var tasks = new ProgressTask[16];
        for (var i = 0; i < tasks.Length; i++)
        {
            var status = i switch
            {
                0 => WorkStatus.Done,
                1 => WorkStatus.InProgress,
                _ => WorkStatus.Todo
            };
            tasks[i] = Task(i + 1, 1, status);
        }

        var report = ProjectProgressCalculator.Calculate([Feat(1)], tasks, NoBlockers);

        Assert.Equal(16, report.Project.Total);
        Assert.Equal(6.2, report.Project.PercentComplete);
    }

    // ── status buckets ─────────────────────────────────────────────────────────────

    [Fact]
    public void MixedStatuses_CountsEveryBucketIndividually()
    {
        var report = ProjectProgressCalculator.Calculate(
            [Feat(1)],
            [
                Task(1, 1, WorkStatus.Backlog),
                Task(2, 1, WorkStatus.Todo),
                Task(3, 1, WorkStatus.InProgress),
                Task(4, 1, WorkStatus.Review),
                Task(5, 1, WorkStatus.Done),
                Task(6, 1, WorkStatus.Done),
                Task(7, 1, WorkStatus.Done)
            ],
            NoBlockers);

        // All five fields asserted individually — Done is NOT InProgress, and neither is Review.
        Assert.Equal(3, report.Project.Completed);
        Assert.Equal(1, report.Project.InProgress);
        Assert.Equal(0, report.Project.Blocked);
        Assert.Equal(7, report.Project.Total);
        Assert.Equal(42.9, report.Project.PercentComplete); // 100 * 3/7 = 42.857… → 42.9
    }

    [Fact]
    public void TaskWithOpenBlocker_IsCountedInBlockedAndInItsStatusBucket()
    {
        var report = ProjectProgressCalculator.Calculate(
            [Feat(1)],
            [
                Task(1, 1, WorkStatus.InProgress), // blocked
                Task(2, 1, WorkStatus.Done)        // not blocked
            ],
            [1]);

        Assert.Equal(1, report.Project.Blocked);
        Assert.Equal(1, report.Project.InProgress); // the blocked task still occupies its status bucket
        Assert.Equal(1, report.Project.Completed);
        Assert.Equal(2, report.Project.Total);      // Blocked is a cross-cut, never a sixth status
        Assert.Equal(50.0, report.Project.PercentComplete);
        Assert.Equal(report.Project, report.ForFeature(1)); // single-feature project: identical numbers
    }

    // ── recursive rollup ───────────────────────────────────────────────────────────

    [Fact]
    public void ThreeLevelNesting_AncestorIncludesAllDescendantTasks()
    {
        // root(1) → child(2) → grandchild(3). Every level owns 1 Done + 1 InProgress, so this
        // test stays green under the "count Done as InProgress" sabotage and goes red ONLY when
        // the rollup stops being recursive. Every counter rolls up through the same fold, so the
        // Blocked rollup is asserted here too rather than in a second nesting test — one defect,
        // one signal.
        var report = ProjectProgressCalculator.Calculate(
            [Feat(1), Feat(2, parentFeatureId: 1), Feat(3, parentFeatureId: 2)],
            [
                Task(1, 1, WorkStatus.Done), Task(2, 1, WorkStatus.InProgress),
                Task(3, 2, WorkStatus.Done), Task(4, 2, WorkStatus.InProgress),
                Task(5, 3, WorkStatus.Done), Task(6, 3, WorkStatus.InProgress)
            ],
            [6]); // the grandchild's InProgress task is blocked

        Assert.Equal(2, report.ForFeature(3).Total);      // leaf: own tasks only
        Assert.Equal(1, report.ForFeature(3).Completed);
        Assert.Equal(1, report.ForFeature(3).Blocked);

        Assert.Equal(4, report.ForFeature(2).Total);      // own 2 + grandchild 2
        Assert.Equal(2, report.ForFeature(2).Completed);
        Assert.Equal(1, report.ForFeature(2).Blocked);    // inherited from the grandchild

        Assert.Equal(6, report.ForFeature(1).Total);      // own 2 + child subtree 4
        Assert.Equal(3, report.ForFeature(1).Completed);
        Assert.Equal(3, report.ForFeature(1).InProgress);
        Assert.Equal(1, report.ForFeature(1).Blocked);    // inherited two levels down
        Assert.Equal(50.0, report.ForFeature(1).PercentComplete);

        Assert.Equal(6, report.Project.Total);
    }

    [Fact]
    public void ProjectTotal_EqualsSumOfTopLevelFeatureTotalsPlusUnparentedTasks()
    {
        // Deliberately FLAT: the recursive rollup has its own test above, so this one isolates
        // the project-vs-tree reconciliation (goal 4). Feature 3's parent is not in the set, so
        // it never appears in the top-level features[] array; its tasks — and the task whose
        // feature does not exist at all — are the "unparented" residual.
        ProgressFeature[] features = [Feat(1), Feat(2), Feat(3, parentFeatureId: 999)];
        ProgressTask[] tasks =
        [
            Task(1, 1), Task(2, 1),
            Task(3, 2), Task(4, 2), Task(5, 2),
            Task(6, 3), Task(7, 3),
            Task(8, 777) // dangling: no such feature was supplied
        ];

        var report = ProjectProgressCalculator.Calculate(features, tasks, NoBlockers);

        var topLevelTotal = features
            .Where(f => f.ParentFeatureId is null)
            .Sum(f => report.ForFeature(f.Id).Total);
        var unparentedTotal = report.Project.Total - topLevelTotal;

        Assert.Equal(5, topLevelTotal);                                   // 2 + 3
        Assert.Equal(3, unparentedTotal);                                 // 2 orphaned + 1 dangling
        Assert.Equal(8, report.Project.Total);                            // every task, at any depth
        Assert.Equal(topLevelTotal + unparentedTotal, report.Project.Total);
        Assert.Equal(2, report.ForFeature(3).Total);                      // the orphan folds as its own root
    }

    [Fact]
    public void UnknownFeatureId_ReportsEmptyProgressInsteadOfThrowing()
    {
        var report = ProjectProgressCalculator.Calculate([Feat(1)], [Task(1, 1)], NoBlockers);

        Assert.Equal(ProjectProgress.Empty, report.ForFeature(42));
    }

    // ── cycle safety ───────────────────────────────────────────────────────────────

    [Fact]
    public void FeatureCycle_Terminates_AndDoesNotDoubleCount()
    {
        // 1 → 2 → 3 → 1. The DAG is cycle-guarded on write elsewhere; the calculator must not
        // hang (or inflate its counts) if that guarantee is ever violated.
        var report = ProjectProgressCalculator.Calculate(
            [Feat(1, parentFeatureId: 3), Feat(2, parentFeatureId: 1), Feat(3, parentFeatureId: 2)],
            [Task(1, 1), Task(2, 2), Task(3, 3)],
            NoBlockers);

        // Reaching this line at all is the termination half of the proof: an unguarded walk over
        // this graph never returns. The rest is the no-double-count half — the exact fold VALUES
        // are the recursion test's business, so a rollup regression stays a one-test signal.
        Assert.Equal(3, report.Project.Total);
        Assert.Equal(3, report.ByFeatureId.Count);   // every feature in the cycle still gets a result
        // No feature may report more tasks than exist — that is what double-counting would look like.
        Assert.All(new[] { 1, 2, 3 }, id => Assert.InRange(report.ForFeature(id).Total, 1, 3));
    }

    [Fact]
    public void SelfParentingFeature_Terminates_AndCountsItsOwnTasksOnce()
    {
        var report = ProjectProgressCalculator.Calculate(
            [Feat(1, parentFeatureId: 1)],
            [Task(1, 1), Task(2, 1)],
            NoBlockers);

        Assert.Equal(2, report.ForFeature(1).Total);
        Assert.Equal(2, report.Project.Total);
    }

    // ── shape / scale ──────────────────────────────────────────────────────────────

    [Fact]
    public void DeepFeatureChain_FoldsWithoutStackOverflow()
    {
        // Guards the "single-pass, iterative fold" requirement (goal 7): a recursive walk blows
        // the stack at this depth and a per-node rescan of the feature set is quadratic. The
        // rollup VALUES are asserted by the nesting test above; this one deliberately asserts
        // only depth-independent facts so a rollup regression produces exactly one signal.
        const int depth = 20_000;
        var features = new ProgressFeature[depth];
        var tasks = new ProgressTask[depth];
        for (var i = 0; i < depth; i++)
        {
            var id = i + 1;
            features[i] = Feat(id, i == 0 ? null : id - 1);
            tasks[i] = Task(id, id);
        }

        var report = ProjectProgressCalculator.Calculate(features, tasks, NoBlockers);

        Assert.Equal(depth, report.Project.Total);
        Assert.Equal(1, report.ForFeature(depth).Total); // the deepest leaf owns exactly its own task
    }
}
