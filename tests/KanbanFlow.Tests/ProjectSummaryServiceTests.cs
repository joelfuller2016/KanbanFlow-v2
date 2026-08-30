using KanbanFlow.Data;
using KanbanFlow.Mcp;
using KanbanFlow.Models;
using KanbanFlow.Services;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Tests;

/// <summary>
/// Tests for <see cref="ProjectSummaryService"/> — the single server-side builder behind BOTH
/// <c>GET /api/projects</c> and the Home project list (#281). Before it existed, Home derived its
/// own task count in the page (<c>p.Features.SelectMany(f =&gt; f.Tasks).Count()</c>) and showed no
/// progress, no next task and no claim state at all.
///
/// Everything below runs against REAL SQLite through <see cref="SqliteTestFactory"/> with the real
/// EF model, the real <see cref="ProjectProgressCalculator"/> and the real <see cref="TaskRanking"/>.
/// Nothing is mocked and no assertion re-implements the thing it is checking.
///
/// Each test is scoped to ONE property so a defect gives a specific signal. Measured blast radius
/// of the sabotages that matter:
///   (a) build progress from the OPEN tasks instead of every task (the "count what I happen to be
///       holding" bug this issue is about) → <see cref="Progress_CountsFinishedTasksToo_NotOnlyTheOpenOnes"/>
///       and <see cref="Progress_IsTheSharedCalculatorsOutput_ForTheSameRows"/>. It cannot be
///       fewer: "how many are done out of how many" is inherently a total-counting assertion.
///   (b) pick the first open task instead of ranking → <see cref="NextTask_IsTheRankersTopPick_NotTheFirstOpenTask"/>
///       and <see cref="NextTask_IsIdenticalToWhatMcpGetNextTasksReturns"/>.
///   (c) count claims over every task rather than the unfinished ones →
///       only <see cref="ActiveClaims_IgnoreAStaleClaimLeftOnAFinishedTask"/>.
///   (d) drop the per-project grouping → only <see cref="TwoProjects_DoNotBleedIntoEachOther"/>.
/// </summary>
public sealed class ProjectSummaryServiceTests
{
    // ── seeding helpers ────────────────────────────────────────────────────────────────────
    // Ids are assigned by SQLite, so every helper returns what it created; nothing below assumes
    // a task is id 1.

    private static Project SeedProject(AppDbContext db, string name = "Proj")
    {
        var project = new Project { Name = name };
        db.Projects.Add(project);
        db.SaveChanges();
        return project;
    }

    private static Feature SeedFeature(AppDbContext db, Project project, string name = "Feat", int? parentId = null)
    {
        var feature = new Feature { Name = name, ProjectId = project.Id, ParentFeatureId = parentId };
        db.Features.Add(feature);
        db.SaveChanges();
        return feature;
    }

    private static TaskItem SeedTask(
        AppDbContext db,
        Feature feature,
        string name,
        WorkStatus status = WorkStatus.Todo,
        TaskPriority priority = TaskPriority.Low,
        string? claimedBy = null)
    {
        var task = new TaskItem
        {
            Name = name,
            FeatureId = feature.Id,
            Status = status,
            Priority = priority,
            ClaimedBy = claimedBy,
            ClaimedAt = claimedBy is null ? null : DateTime.UtcNow
        };
        db.Tasks.Add(task);
        db.SaveChanges();
        return task;
    }

    /// <param name="actor">
    /// Passed straight to <see cref="ProjectSummaryService.LoadAsync"/>. Defaulted to null so every
    /// existing call reads as "the parameterless call" — the shape both production callers use.
    /// </param>
    private static async Task<ProjectListSummary> LoadOneAsync(
        SqliteTestFactory factory, int projectId, string? actor = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        var summaries = await ProjectSummaryService.LoadAsync(db, actor);
        return Assert.Single(summaries, s => s.Id == projectId);
    }

    // ══ progress (AC 1) ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The counter that the whole issue turns on. A surface that counts only the tasks it happens
    /// to have loaded — and the ranker only ever loads OPEN tasks — reports a total that shrinks
    /// every time somebody finishes something, so a project reads as less complete the more work
    /// gets done. The total must cover every task at every status.
    /// </summary>
    [Fact]
    public async Task Progress_CountsFinishedTasksToo_NotOnlyTheOpenOnes()
    {
        using var factory = new SqliteTestFactory();
        Project project;
        await using (var db = await factory.CreateDbContextAsync())
        {
            project = SeedProject(db);
            var feature = SeedFeature(db, project);
            SeedTask(db, feature, "done-1", WorkStatus.Done);
            SeedTask(db, feature, "done-2", WorkStatus.Done);
            SeedTask(db, feature, "wip", WorkStatus.InProgress);
            SeedTask(db, feature, "todo", WorkStatus.Todo);
        }

        var summary = await LoadOneAsync(factory, project.Id);

        Assert.Equal(4, summary.Progress.Total);
        Assert.Equal(2, summary.Progress.Completed);
        Assert.Equal(1, summary.Progress.InProgress);
        Assert.Equal(50.0, summary.Progress.PercentComplete);

        // TaskCount predates the progress object and some consumer may still read it; the two must
        // never be able to disagree about the same project.
        Assert.Equal(summary.Progress.Total, summary.TaskCount);
    }

    /// <summary>
    /// Delegation, not duplication: whatever the shared calculator says over the project's rows is
    /// exactly what the summary carries. This is what stops the list growing a private third
    /// definition of "done" beside <c>GET /api/projects/{id}</c> and <c>/context</c> (#125). The
    /// project is deliberately NESTED, because a rollup bug is invisible on a flat one.
    /// </summary>
    [Fact]
    public async Task Progress_IsTheSharedCalculatorsOutput_ForTheSameRows()
    {
        using var factory = new SqliteTestFactory();
        Project project;
        await using (var db = await factory.CreateDbContextAsync())
        {
            project = SeedProject(db);
            var root = SeedFeature(db, project, "root");
            var child = SeedFeature(db, project, "child", root.Id);
            var grandchild = SeedFeature(db, project, "grandchild", child.Id);
            SeedTask(db, root, "r1", WorkStatus.Done);
            SeedTask(db, child, "c1", WorkStatus.Todo);
            SeedTask(db, child, "c2", WorkStatus.Done);
            SeedTask(db, grandchild, "g1", WorkStatus.InProgress);
            SeedTask(db, grandchild, "g2", WorkStatus.Review);
        }

        var summary = await LoadOneAsync(factory, project.Id);

        await using var read = await factory.CreateDbContextAsync();
        var expected = ProjectProgressCalculator.Calculate(
            await read.Features.AsNoTracking()
                .Where(f => f.ProjectId == project.Id)
                .Select(f => new ProgressFeature(f.Id, f.ParentFeatureId))
                .ToListAsync(),
            await read.Tasks.AsNoTracking()
                .Where(t => t.Feature.ProjectId == project.Id)
                .Select(t => new ProgressTask(t.Id, t.FeatureId, t.Status))
                .ToListAsync(),
            []);

        Assert.Equal(expected.Project, summary.Progress);
    }

    /// <summary>
    /// The blocked counter is wired to real dependency rows, and a finished blocker stops blocking.
    /// The Done-exclusion rule itself belongs to <see cref="ProjectProgress"/>; what is under test
    /// here is that this service feeds it the right blocked ids at all.
    /// </summary>
    [Fact]
    public async Task Progress_Blocked_ReflectsOpenDependencies_AndClearsWhenTheBlockerFinishes()
    {
        using var factory = new SqliteTestFactory();
        Project project;
        int blockerId;
        await using (var db = await factory.CreateDbContextAsync())
        {
            project = SeedProject(db);
            var feature = SeedFeature(db, project);
            var blocker = SeedTask(db, feature, "blocker", WorkStatus.Todo);
            var blocked = SeedTask(db, feature, "blocked", WorkStatus.Todo);
            db.TaskDependencies.Add(new TaskDependency
            {
                ProjectId = project.Id,
                TaskItemId = blocked.Id,
                DependsOnTaskItemId = blocker.Id
            });
            db.SaveChanges();
            blockerId = blocker.Id;
        }

        Assert.Equal(1, (await LoadOneAsync(factory, project.Id)).Progress.Blocked);

        await using (var db = await factory.CreateDbContextAsync())
        {
            var blocker = await db.Tasks.FindAsync(blockerId);
            blocker!.Status = WorkStatus.Done;
            await db.SaveChangesAsync();
        }

        Assert.Equal(0, (await LoadOneAsync(factory, project.Id)).Progress.Blocked);
    }

    /// <summary>A project with no tasks reports zeroes — not null, not NaN — so the card can ask.</summary>
    [Fact]
    public async Task Progress_OnAProjectWithNoTasks_IsAllZeroes()
    {
        using var factory = new SqliteTestFactory();
        Project project;
        await using (var db = await factory.CreateDbContextAsync())
        {
            project = SeedProject(db);
            SeedFeature(db, project);
        }

        var summary = await LoadOneAsync(factory, project.Id);

        Assert.Equal(0, summary.Progress.Total);
        Assert.Equal(0.0, summary.Progress.PercentComplete);
        Assert.False(double.IsNaN(summary.Progress.PercentComplete));
    }

    // ══ next task (AC 2) ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// "Next" must be the RANKER's answer, not "the first open task". The seed makes the two
    /// disagree on purpose: the lowest-id open task is Low priority, the Critical one was created
    /// later, and the ranker sorts by priority before id.
    /// </summary>
    [Fact]
    public async Task NextTask_IsTheRankersTopPick_NotTheFirstOpenTask()
    {
        using var factory = new SqliteTestFactory();
        Project project;
        int firstId, criticalId;
        await using (var db = await factory.CreateDbContextAsync())
        {
            project = SeedProject(db);
            var feature = SeedFeature(db, project);
            firstId = SeedTask(db, feature, "created first, low priority", WorkStatus.Todo, TaskPriority.Low).Id;
            criticalId = SeedTask(db, feature, "created later, critical", WorkStatus.Todo, TaskPriority.Critical).Id;
        }

        Assert.True(firstId < criticalId, "seed precondition: the low-priority task must have the lower id");

        var summary = await LoadOneAsync(factory, project.Id);

        Assert.NotNull(summary.NextTask);
        Assert.Equal(criticalId, summary.NextTask!.Id);
        Assert.Equal("Critical", summary.NextTask.Priority);
        // The reason comes from the ranker verbatim — the card must not invent its own.
        Assert.Contains("Critical priority", summary.NextTask.Reason);
    }

    /// <summary>
    /// THE regression guard this issue asks for by name. REST and MCP have already drifted twice
    /// over exactly this question (#151, #188), and the Home card is now a third surface answering
    /// it. Assert the equality rather than assuming it: every field the card can display must match
    /// what <c>get_next_tasks</c> hands an agent for the same project.
    ///
    /// The seed exercises all three ranking rules at once — a claim, a priority spread and an
    /// in-progress task — so an ordering difference cannot hide behind a single-task project.
    /// </summary>
    [Fact]
    public async Task NextTask_IsIdenticalToWhatMcpGetNextTasksReturns()
    {
        using var factory = new SqliteTestFactory();
        Project project;
        await using (var db = await factory.CreateDbContextAsync())
        {
            project = SeedProject(db);
            var feature = SeedFeature(db, project);
            SeedTask(db, feature, "claimed critical", WorkStatus.Todo, TaskPriority.Critical, claimedBy: "agent-a");
            SeedTask(db, feature, "unclaimed high", WorkStatus.Todo, TaskPriority.High);
            SeedTask(db, feature, "unclaimed high, in progress", WorkStatus.InProgress, TaskPriority.High);
            SeedTask(db, feature, "unclaimed low", WorkStatus.Todo, TaskPriority.Low);
            SeedTask(db, feature, "finished", WorkStatus.Done, TaskPriority.Critical);
        }

        var summary = await LoadOneAsync(factory, project.Id);

        // The MCP tool with no actor — the same `actor: null` the summary uses, so the two are
        // being asked the identical question.
        var viaMcp = await KanbanMcpTools.GetNextTasks(factory, new ClaimTtlSetting(ClaimPolicy.DefaultTtl), project.Id, actor: null, limit: 5);

        Assert.NotEmpty(viaMcp);
        Assert.NotNull(summary.NextTask);

        var top = viaMcp[0];
        Assert.Equal(top.Id, summary.NextTask!.Id);
        Assert.Equal(top.Name, summary.NextTask.Name);
        Assert.Equal(top.Status, summary.NextTask.Status);
        Assert.Equal(top.Priority, summary.NextTask.Priority);
        Assert.Equal(top.ClaimedBy, summary.NextTask.ClaimedBy);
        Assert.Equal(top.Reason, summary.NextTask.Reason);
    }

    /// <summary>
    /// <c>LoadAsync</c>'s <c>actor</c> parameter, which until now NOTHING exercised: every other
    /// test here — and both production callers — pass null, so deleting the argument at the
    /// <see cref="TaskRanking.RankNextTasks"/> call site would have left the whole suite green
    /// while quietly breaking the one surface that will eventually pass an actor.
    ///
    /// The seed is chosen so the parameter is the ONLY thing that can move the answer. A Critical
    /// task claimed by "me" and an unclaimed High task rank in opposite orders depending on it:
    /// with no actor every claim counts as someone else's and the Critical one sinks below the
    /// High; with actor "me" the claim is the caller's own, so nothing sinks and Critical wins on
    /// priority. A task claimed by "other-agent" would NOT work here — it is de-prioritised under
    /// both calls, so both would return the same task and the assertion would prove nothing.
    ///
    /// Case and whitespace are folded in too ("  ME  "), because <c>RankNextTasks</c> normalises
    /// the actor exactly as <c>ClaimedBy</c> is normalised on write, and an actor that has to be
    /// spelled byte-identically is not usable by an agent that stores its own name.
    /// </summary>
    [Fact]
    public async Task NextTask_HonoursTheActor_SoAnAgentsOwnClaimDoesNotSink()
    {
        using var factory = new SqliteTestFactory();
        Project project;
        int mineId, unclaimedId;
        await using (var db = await factory.CreateDbContextAsync())
        {
            project = SeedProject(db, "Actor");
            var feature = SeedFeature(db, project);
            mineId = SeedTask(db, feature, "critical, claimed by me",
                WorkStatus.Todo, TaskPriority.Critical, claimedBy: "me").Id;
            unclaimedId = SeedTask(db, feature, "high, unclaimed",
                WorkStatus.Todo, TaskPriority.High).Id;
        }

        // No actor: every claimed task is someone else's, so the unclaimed High outranks the
        // Critical one despite being lower priority.
        var anonymous = await LoadOneAsync(factory, project.Id);
        Assert.NotNull(anonymous.NextTask);
        Assert.Equal(unclaimedId, anonymous.NextTask!.Id);

        // Same data, same project, one argument different — and the answer must change.
        var mine = await LoadOneAsync(factory, project.Id, actor: "me");
        Assert.NotNull(mine.NextTask);
        Assert.Equal(mineId, mine.NextTask!.Id);
        Assert.Equal("Critical", mine.NextTask.Priority);
        // The reason is the ranker's verbatim, and for one's OWN claim it must not say
        // "deprioritised" — that string is what an agent reads to understand the pick.
        Assert.DoesNotContain("deprioritised", mine.NextTask.Reason, StringComparison.OrdinalIgnoreCase);

        // Trimmed and case-folded, per RankNextTasks' documented normalisation.
        Assert.Equal(mineId, (await LoadOneAsync(factory, project.Id, actor: "  ME  ")).NextTask!.Id);

        // Blank is not an actor: it must behave exactly like null, not like an actor named "".
        Assert.Equal(unclaimedId, (await LoadOneAsync(factory, project.Id, actor: "   ")).NextTask!.Id);
    }

    /// <summary>
    /// A task whose dependency is unfinished is not workable, so it is not what's next — and with
    /// nothing else open the card must say nothing rather than recommend blocked work.
    ///
    /// The blocker lives in ANOTHER project on purpose: an in-project blocker would itself be an
    /// open task and simply become the recommendation, which would test nothing. It is also the
    /// case both existing next-task surfaces call out explicitly (ApiEndpoints.cs:392-395) — the
    /// ranker treats a target it cannot resolve as unfinished.
    /// </summary>
    [Fact]
    public async Task NextTask_IsNull_WhenEveryOpenTaskIsBlocked()
    {
        using var factory = new SqliteTestFactory();
        Project project;
        await using (var db = await factory.CreateDbContextAsync())
        {
            project = SeedProject(db, "Blocked");
            var feature = SeedFeature(db, project);
            SeedTask(db, feature, "already finished", WorkStatus.Done);
            var blocked = SeedTask(db, feature, "blocked", WorkStatus.Todo, TaskPriority.Critical);

            var elsewhere = SeedProject(db, "Elsewhere");
            var elsewhereFeature = SeedFeature(db, elsewhere, "elsewhere-feat");
            var externalBlocker = SeedTask(db, elsewhereFeature, "external blocker", WorkStatus.Todo);

            db.TaskDependencies.Add(new TaskDependency
            {
                ProjectId = project.Id,
                TaskItemId = blocked.Id,
                DependsOnTaskItemId = externalBlocker.Id
            });
            db.SaveChanges();
        }

        var summary = await LoadOneAsync(factory, project.Id);

        Assert.Null(summary.NextTask);
        // The blocked task still counts toward the project's totals — it exists, it is just not
        // workable. Only the recommendation disappears.
        Assert.Equal(2, summary.Progress.Total);
    }

    /// <summary>Nothing open ⇒ nothing next. The finished project must render calm, not "unknown".</summary>
    [Fact]
    public async Task NextTask_IsNull_WhenEveryTaskIsDone()
    {
        using var factory = new SqliteTestFactory();
        Project project;
        await using (var db = await factory.CreateDbContextAsync())
        {
            project = SeedProject(db);
            var feature = SeedFeature(db, project);
            SeedTask(db, feature, "a", WorkStatus.Done);
            SeedTask(db, feature, "b", WorkStatus.Done);
        }

        var summary = await LoadOneAsync(factory, project.Id);

        Assert.Null(summary.NextTask);
        Assert.Equal(100.0, summary.Progress.PercentComplete);
    }

    // ══ claims (AC 4) ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Completing a task does NOT clear its claim — only an explicit release does — so a finished
    /// task can carry a claim indefinitely. Counting those would show an agent as busy on work it
    /// closed weeks ago, which is a fake signal, which is what AC 5 forbids.
    /// </summary>
    [Fact]
    public async Task ActiveClaims_IgnoreAStaleClaimLeftOnAFinishedTask()
    {
        using var factory = new SqliteTestFactory();
        Project project;
        await using (var db = await factory.CreateDbContextAsync())
        {
            project = SeedProject(db);
            var feature = SeedFeature(db, project);
            SeedTask(db, feature, "finished but still claimed", WorkStatus.Done, claimedBy: "ghost");
            SeedTask(db, feature, "actually being worked", WorkStatus.InProgress, claimedBy: "live");
        }

        var summary = await LoadOneAsync(factory, project.Id);

        var claim = Assert.Single(summary.ActiveClaims);
        Assert.Equal("live", claim.Actor);
        Assert.Equal(1, claim.TaskCount);
    }

    /// <summary>
    /// Claiming compares case-insensitively (TaskService.cs:868-875), so the display must group the
    /// same way or one agent shows up as two. The rendered label is a spelling that exists in the
    /// data — never a normalised invention.
    /// </summary>
    [Fact]
    public async Task ActiveClaims_GroupOneActorCaseInsensitively_BusiestFirst()
    {
        using var factory = new SqliteTestFactory();
        Project project;
        await using (var db = await factory.CreateDbContextAsync())
        {
            project = SeedProject(db);
            var feature = SeedFeature(db, project);
            SeedTask(db, feature, "1", WorkStatus.Todo, claimedBy: "Alice");
            SeedTask(db, feature, "2", WorkStatus.Todo, claimedBy: "alice");
            SeedTask(db, feature, "3", WorkStatus.Todo, claimedBy: "bob");
            SeedTask(db, feature, "4", WorkStatus.Todo);
        }

        var summary = await LoadOneAsync(factory, project.Id);

        Assert.Equal(2, summary.ActiveClaims.Count);
        Assert.Equal(2, summary.ActiveClaims[0].TaskCount);
        Assert.Equal("bob", summary.ActiveClaims[1].Actor);
        Assert.Equal(1, summary.ActiveClaims[1].TaskCount);
        Assert.Contains(summary.ActiveClaims[0].Actor, new[] { "Alice", "alice" });
    }

    /// <summary>Nobody holding anything ⇒ an EMPTY list, never a null the card has to defend against.</summary>
    [Fact]
    public async Task ActiveClaims_AreEmpty_WhenNothingIsClaimed()
    {
        using var factory = new SqliteTestFactory();
        Project project;
        await using (var db = await factory.CreateDbContextAsync())
        {
            project = SeedProject(db);
            var feature = SeedFeature(db, project);
            SeedTask(db, feature, "unclaimed", WorkStatus.Todo);
        }

        Assert.Empty((await LoadOneAsync(factory, project.Id)).ActiveClaims);
    }

    // ══ recency (AC 3) ══════════════════════════════════════════════════════════════════════

    /// <summary>The newest audit row wins, and it is returned as stored UTC for the view to convert.</summary>
    [Fact]
    public async Task LastActivityAt_IsTheMostRecentAuditEvent()
    {
        using var factory = new SqliteTestFactory();
        Project project;
        var newest = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        await using (var db = await factory.CreateDbContextAsync())
        {
            project = SeedProject(db);
            db.ActivityEvents.AddRange(
                new ActivityEvent
                {
                    ProjectId = project.Id, EntityType = "Task", EntityId = 1,
                    EventType = "TaskCreated", CreatedAt = newest.AddDays(-3)
                },
                new ActivityEvent
                {
                    ProjectId = project.Id, EntityType = "Task", EntityId = 1,
                    EventType = "TaskStatusChanged", CreatedAt = newest
                },
                new ActivityEvent
                {
                    ProjectId = project.Id, EntityType = "Task", EntityId = 1,
                    EventType = "TaskUpdated", CreatedAt = newest.AddHours(-2)
                });
            db.SaveChanges();
        }

        var summary = await LoadOneAsync(factory, project.Id);

        Assert.NotNull(summary.LastActivityAt);
        // SQLite hands DateTimes back as Kind=Unspecified; compare the instant, not the Kind — the
        // rendering layer is what stamps it (ActivityTimelineBuilder.AsUtc, #306).
        Assert.Equal(newest, DateTime.SpecifyKind(summary.LastActivityAt!.Value, DateTimeKind.Utc));
    }

    /// <summary>
    /// A project nothing has ever happened to reports NULL, so the card can omit the row entirely
    /// instead of rendering an "unknown" chip (AC 5).
    /// </summary>
    [Fact]
    public async Task LastActivityAt_IsNull_WhenNothingHasEverHappened()
    {
        using var factory = new SqliteTestFactory();
        Project project;
        await using (var db = await factory.CreateDbContextAsync())
        {
            project = SeedProject(db);
        }

        var summary = await LoadOneAsync(factory, project.Id);

        Assert.Null(summary.LastActivityAt);
        Assert.Empty(summary.ActiveClaims);
        Assert.Null(summary.NextTask);
        Assert.Equal(0, summary.Progress.Total);
    }

    // ══ shape + isolation ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// The loader reads every project's rows in ONE pass and groups them in memory, so a grouping
    /// slip would attribute one project's work to another — the kind of bug that looks plausible
    /// on a single-project database and is wrong everywhere else.
    /// </summary>
    [Fact]
    public async Task TwoProjects_DoNotBleedIntoEachOther()
    {
        using var factory = new SqliteTestFactory();
        Project alpha, beta;
        await using (var db = await factory.CreateDbContextAsync())
        {
            alpha = SeedProject(db, "Alpha");
            var alphaFeature = SeedFeature(db, alpha, "alpha-feat");
            SeedTask(db, alphaFeature, "alpha done", WorkStatus.Done);
            SeedTask(db, alphaFeature, "alpha open", WorkStatus.Todo, TaskPriority.High, claimedBy: "alpha-agent");

            beta = SeedProject(db, "Beta");
            var betaFeature = SeedFeature(db, beta, "beta-feat");
            SeedTask(db, betaFeature, "beta open 1", WorkStatus.Todo, TaskPriority.Critical);
            SeedTask(db, betaFeature, "beta open 2", WorkStatus.Todo);
            SeedTask(db, betaFeature, "beta open 3", WorkStatus.Todo);

            db.ActivityEvents.Add(new ActivityEvent
            {
                ProjectId = beta.Id, EntityType = "Task", EntityId = 1,
                EventType = "TaskCreated", CreatedAt = DateTime.UtcNow
            });
            db.SaveChanges();
        }

        await using var read = await factory.CreateDbContextAsync();
        var summaries = await ProjectSummaryService.LoadAsync(read);

        var a = summaries.Single(s => s.Id == alpha.Id);
        var b = summaries.Single(s => s.Id == beta.Id);

        Assert.Equal(2, a.Progress.Total);
        Assert.Equal(1, a.Progress.Completed);
        Assert.Equal("alpha open", a.NextTask!.Name);
        Assert.Equal("alpha-agent", Assert.Single(a.ActiveClaims).Actor);
        Assert.Null(a.LastActivityAt);

        Assert.Equal(3, b.Progress.Total);
        Assert.Equal(0, b.Progress.Completed);
        Assert.Equal("beta open 1", b.NextTask!.Name);
        Assert.Empty(b.ActiveClaims);
        Assert.NotNull(b.LastActivityAt);
    }

    /// <summary>
    /// The keys <c>GET /api/projects</c> already served are still populated. This is the
    /// back-compat half: the endpoint's shape grew, it did not change.
    /// </summary>
    [Fact]
    public async Task Summary_StillCarriesTheOriginalListFields()
    {
        using var factory = new SqliteTestFactory();
        Project project;
        await using (var db = await factory.CreateDbContextAsync())
        {
            project = SeedProject(db);
            project.Description = "desc";
            project.Goals = "goals";
            project.Readme = "readme";
            db.SaveChanges();

            var root = SeedFeature(db, project, "root");
            var child = SeedFeature(db, project, "child", root.Id);
            SeedTask(db, root, "t1");
            SeedTask(db, child, "t2");
        }

        var summary = await LoadOneAsync(factory, project.Id);

        Assert.Equal("Proj", summary.Name);
        Assert.Equal("desc", summary.Description);
        Assert.Equal("goals", summary.Goals);
        Assert.Equal("readme", summary.Readme);
        Assert.Equal(2, summary.FeatureCount);
        Assert.Equal(2, summary.TaskCount);
        Assert.NotEqual(default, summary.CreatedAt);
        Assert.NotEqual(default, summary.UpdatedAt);
    }

    /// <summary>An empty database returns an empty list, not a null and not a throw.</summary>
    [Fact]
    public async Task NoProjects_ReturnsAnEmptyList()
    {
        using var factory = new SqliteTestFactory();
        await using var db = await factory.CreateDbContextAsync();

        Assert.Empty(await ProjectSummaryService.LoadAsync(db));
    }
}
