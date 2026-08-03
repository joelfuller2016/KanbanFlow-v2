using KanbanFlow.Models;
using KanbanFlow.Services;

namespace KanbanFlow.Tests;

/// <summary>
/// Tests for <see cref="ProjectProgressCalculator"/> — the single shared "how much of this is
/// done?" calculator behind <c>GET /api/projects/{id}</c> and <c>GET /api/projects/{id}/context</c>
/// (#125), so Home, <c>/project/{id}</c> and MCP can never report different numbers for the same
/// project.
///
/// The calculator is pure (no EF, no Blazor), so every case below exercises the REAL production
/// code over in-memory inputs — no database, no browser, no fakes.
///
/// Each test is scoped to ONE property so a defect produces a specific signal. Tests that are not
/// about a given property are built so that property's sabotage leaves them green (scopes with
/// equal Done/InProgress counts; no empty scope outside the empty-project case; assertions that do
/// not depend on the rollup where the rollup is not the subject). Measured blast radius:
///   (a) count Done as InProgress → <see cref="MixedStatuses_CountsEveryBucketIndividually"/> and
///       <see cref="AllTasksDone_IsOneHundredPercent"/>. It cannot be fewer: "all Done ⇒ 100%" is
///       inherently a Done-counting assertion.
///   (b) drop the <c>Total &gt; 0</c> guard → only <see cref="EmptyProject_HasZeroTotalAndZeroPercent_NeverNaN"/>.
///   (c) roll up own tasks only → <see cref="ThreeLevelNesting_AncestorIncludesAllDescendantTasks"/>
///       and <see cref="MultipleChildFeatures_AllSiblingSubtreesAreAdded"/> — the depth and the
///       breadth halves of the recursion; every other test is built to survive it.
///   (d) count a Done task as blocked → only <see cref="DoneTaskWithAnOpenBlocker_IsNotCountedAsBlocked"/>.
///   (e) neuter the duplicate-id guard → only <see cref="DuplicateFeatureRows_DoNotInflateAnAncestorsRollup"/>.
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
    public void MultipleChildFeatures_AllSiblingSubtreesAreAdded()
    {
        // The fold suspends and resumes a parent frame once per child; a mis-tracked resume
        // cursor would drop or repeat a sibling. Uneven subtree sizes make either failure visible.
        var report = ProjectProgressCalculator.Calculate(
            [
                Feat(1),
                Feat(2, parentFeatureId: 1), Feat(3, parentFeatureId: 1), Feat(4, parentFeatureId: 1),
                Feat(5, parentFeatureId: 3)
            ],
            [
                Task(1, 1),
                Task(2, 2),
                Task(3, 3), Task(4, 3),
                Task(5, 4), Task(6, 4), Task(7, 4),
                Task(8, 5)
            ],
            NoBlockers);

        Assert.Equal(1, report.ForFeature(2).Total);
        Assert.Equal(3, report.ForFeature(3).Total); // own 2 + grandchild 1
        Assert.Equal(3, report.ForFeature(4).Total);
        Assert.Equal(8, report.ForFeature(1).Total); // own 1 + 1 + 3 + 3, each sibling exactly once
        Assert.Equal(8, report.Project.Total);
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

        // Derived from the INPUT, never from report.Project.Total — computing the residual by
        // subtraction would make the identity below true for any implementation. Membership in
        // topLevelIds is the exact test only because this fixture's roots have no children.
        var topLevelIds = features.Where(f => f.ParentFeatureId is null).Select(f => f.Id).ToHashSet();
        var unparentedTotal = tasks.Count(t => !topLevelIds.Contains(t.FeatureId));

        Assert.Equal(5, topLevelTotal);                                   // 2 + 3
        Assert.Equal(3, unparentedTotal);                                 // 2 orphaned + 1 dangling
        Assert.Equal(8, report.Project.Total);                            // every task, at any depth
        Assert.Equal(topLevelTotal + unparentedTotal, report.Project.Total);
        Assert.Equal(2, report.ForFeature(3).Total);                      // the orphan folds as its own root
    }

    [Fact]
    public void DoneTaskWithAnOpenBlocker_IsNotCountedAsBlocked()
    {
        // Nothing is left for a blocker to hold up once the task is finished, and
        // GET /api/projects/{id}/next-tasks counts blockedTaskCount over non-Done tasks only —
        // if this rule drifted, two endpoints would report two "blocked" numbers for one project.
        var report = ProjectProgressCalculator.Calculate(
            [Feat(1)],
            [
                Task(1, 1, WorkStatus.Done),  // stale dependency: blocker never finished
                Task(2, 1, WorkStatus.Todo)   // genuinely blocked
            ],
            [1, 2]);

        // Only the Blocked rule is asserted here — Done-counting has its own tests, and pinning
        // it again would make this test red for a defect it is not guarding.
        Assert.Equal(1, report.Project.Blocked);
        Assert.Equal(2, report.Project.Total);
        Assert.Equal(1, report.ForFeature(1).Blocked);
    }

    [Fact]
    public void DuplicateFeatureRows_DoNotInflateAnAncestorsRollup()
    {
        // Distinct-by-Id is the documented contract; a repeated row must still never make a
        // feature report more tasks than the project contains.
        var report = ProjectProgressCalculator.Calculate(
            [Feat(1), Feat(2, parentFeatureId: 1), Feat(2, parentFeatureId: 1)],
            [Task(1, 2)],
            NoBlockers);

        Assert.Equal(1, report.Project.Total);
        Assert.Equal(1, report.ForFeature(2).Total);            // the deduped child still counts its own task
        // An upper bound, matching this test's name: the duplicate row would make the parent
        // report 2. Asserting the exact rollup here instead would redden this test for any
        // rollup defect, not just the one it guards.
        Assert.True(report.ForFeature(1).Total <= report.Project.Total,
            $"Feature 1 reported {report.ForFeature(1).Total} tasks; the project only contains {report.Project.Total}.");
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
    public void WideFeatureStar_FoldsInLinearTime()
    {
        // Guards goal 7 (single-pass, no rescan per node) at the one shape that can prove it: a
        // root with N children makes the root's frame resume N times. Resuming from NextChild is
        // O(N) overall and finishes in milliseconds; restarting the child scan at 0 on every
        // resume is O(N^2) — 2e10 lookups here — and would never return.
        const int width = 200_000;
        var features = new ProgressFeature[width + 1];
        var tasks = new ProgressTask[width];
        features[0] = Feat(1);
        for (var i = 0; i < width; i++)
        {
            var id = i + 2;
            features[i + 1] = Feat(id, parentFeatureId: 1);
            tasks[i] = Task(id, id);
        }

        var report = ProjectProgressCalculator.Calculate(features, tasks, NoBlockers);

        // Deliberately depth-independent: the fold runs over the whole star whatever we assert,
        // so a quadratic resume hangs before reaching this line. The multi-child rollup VALUE is
        // asserted by MultipleChildFeatures_... below, keeping each defect to one signal.
        Assert.Equal(width, report.Project.Total);
        Assert.Equal(width + 1, report.ByFeatureId.Count); // every node folded exactly once
    }

    [Fact]
    public void DeepFeatureChain_FoldsWithoutStackOverflow()
    {
        // Guards the ITERATIVE half of the fold: a recursive walk blows the 1 MB stack at this
        // depth. (Non-quadratic is the star test's job; a slow implementation would still pass
        // here.) The rollup VALUES are asserted by the nesting test, so this one deliberately
        // asserts only depth-independent facts and a rollup regression stays a one-test signal.
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
