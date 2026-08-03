using KanbanFlow.Data;
using KanbanFlow.Models;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Services;

/// <summary>
/// The top-ranked open task for a project, as chosen by <see cref="TaskRanking"/> — the SAME
/// ranker behind <c>GET /api/projects/{id}/next-tasks</c> and the MCP <c>get_next_tasks</c> tool.
/// It is deliberately a projection of <see cref="RankedTask"/> rather than a new ranking: a second
/// "what's next" rule is exactly the divergence #151/#188 were filed for.
/// </summary>
/// <param name="Reason">
/// The ranker's own explanation, verbatim. Carried so a surface can show WHY this task is next
/// instead of inventing its own justification.
/// </param>
public sealed record NextTaskSummary(
    int Id, int FeatureId, string Name, string Status, string Priority, string? ClaimedBy, string Reason);

/// <summary>
/// One actor currently holding work in a project, with how many tasks they hold.
/// </summary>
/// <param name="Actor">
/// The claim label exactly as stored (trimmed). An open-access display string, never an identity.
/// </param>
/// <param name="TaskCount">
/// How many UNFINISHED tasks this actor holds. A claim is never cleared by completing a task —
/// only <c>ReleaseTask</c> clears it (TaskService.cs:1000) — so a Done task can carry a stale
/// <see cref="TaskItem.ClaimedBy"/> forever. Counting those would report an agent as "working on"
/// something it finished days ago, so finished tasks are excluded. Same shape of rule, and the
/// same reason, as <see cref="ProjectProgress.Blocked"/> excluding Done.
/// </param>
public sealed record ActiveClaim(string Actor, int TaskCount);

/// <summary>
/// Everything the project LIST needs to answer "how far along is this? what's next? is anything
/// happening?" — computed once, server-side (#281).
///
/// The first nine members are the pre-existing <c>GET /api/projects</c> response, unchanged in
/// name, type and order; the rest are additive. Nothing was removed or renamed, so an existing
/// consumer of that endpoint sees exactly what it saw before.
/// </summary>
public sealed record ProjectListSummary
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? Goals { get; init; }
    public string? Readme { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required DateTime UpdatedAt { get; init; }

    /// <summary>Features owned by the project, at any nesting depth.</summary>
    public required int FeatureCount { get; init; }

    /// <summary>
    /// Every task under the project's features. Always equal to <c>Progress.Total</c>; kept
    /// because it predates <see cref="Progress"/> and removing it would break consumers.
    /// </summary>
    public required int TaskCount { get; init; }

    /// <summary>
    /// The #125 progress object, from the shared <see cref="ProjectProgressCalculator"/> — the
    /// same instance of the same calculation <c>GET /api/projects/{id}</c> serves, so the list and
    /// the detail page can never disagree about how done a project is.
    /// </summary>
    public required ProjectProgress Progress { get; init; }

    /// <summary>The ranker's #1 pick, or null when the project has no rankable open task.</summary>
    public NextTaskSummary? NextTask { get; init; }

    /// <summary>
    /// When the project's audit trail last recorded anything, or null when nothing has ever
    /// happened. UTC as stored — the rendering surface owns the timezone conversion (#306).
    /// </summary>
    public DateTime? LastActivityAt { get; init; }

    /// <summary>Actors holding unfinished work, busiest first. Empty when nobody holds anything.</summary>
    public IReadOnlyList<ActiveClaim> ActiveClaims { get; init; } = [];
}

/// <summary>
/// Builds the project-list summary ONCE, for every surface that shows a list of projects (#281).
///
/// ── WHY THIS EXISTS ────────────────────────────────────────────────────────────────────────
/// Home used to derive its own counts in the page
/// (<c>p.Features.SelectMany(f =&gt; f.Tasks).Count()</c>) while <c>GET /api/projects</c> derived
/// its own in SQL, so "how many tasks does this project have?" had two implementations and no
/// test that they agreed. Progress, next-task and claim state had none at all. Both surfaces now
/// call this, which in turn calls the two shared engines — <see cref="ProjectProgressCalculator"/>
/// (#125) and <see cref="TaskRanking"/> (#151/#188) — rather than reimplementing either.
///
/// It takes an <see cref="AppDbContext"/> instead of resolving one, because its two callers get
/// theirs differently: the minimal API is handed a scoped context, the Blazor page creates one
/// from <see cref="IDbContextFactory{TContext}"/>. Passing the context in is what lets ONE method
/// serve both.
/// ───────────────────────────────────────────────────────────────────────────────────────────
/// </summary>
/// <remarks>
/// Cost is a FIXED five queries regardless of how many projects exist — never one per project.
/// Two of them are narrow projections (three scalar columns per task; three per dependency) in the
/// spirit of #157; only the open tasks are materialised as entities, and only because
/// <see cref="TaskRanking.RankNextTasks"/> ranks entities. Done tasks — the bulk of a mature
/// project — are never loaded as entities at all.
/// </remarks>
public static class ProjectSummaryService
{
    /// <summary>
    /// Loads a summary for every project, in the database's natural order. Callers that want a
    /// different order sort the result — ordering is presentation, and changing this method's
    /// order would silently reorder <c>GET /api/projects</c> for its existing consumers.
    /// </summary>
    /// <param name="db">An open context. Not disposed here; the caller owns its lifetime.</param>
    /// <param name="actor">
    /// Optional actor label passed straight through to <see cref="TaskRanking.RankNextTasks"/>:
    /// tasks claimed by a DIFFERENT actor sink. Null (the default, and what both current callers
    /// pass) reproduces <c>GET /api/projects/{id}/next-tasks</c> with no <c>actor</c> query
    /// parameter, so the three surfaces are comparable by construction.
    /// </param>
    public static async Task<List<ProjectListSummary>> LoadAsync(
        AppDbContext db, string? actor = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        // ── 1. project scalars + the two counts, projected in SQL (#157: never load the
        //       Feature→Task graph just to count it) ──
        var projects = await db.Projects
            .AsNoTracking()
            .Select(p => new
            {
                p.Id,
                p.Name,
                p.Description,
                p.Goals,
                p.Readme,
                p.CreatedAt,
                p.UpdatedAt,
                FeatureCount = p.Features.Count,
                TaskCount = p.Features.SelectMany(f => f.Tasks).Count()
            })
            .ToListAsync(ct);

        if (projects.Count == 0)
            return [];

        // ── 2. the feature tree: the parent edge for the progress rollup, the owner so a task can
        //       be attributed to its project (TaskItem carries FeatureId, not ProjectId) ──
        var features = await db.Features
            .AsNoTracking()
            .Select(f => new { f.Id, f.ProjectId, f.ParentFeatureId })
            .ToListAsync(ct);
        var featuresByProject = features.ToLookup(f => f.ProjectId);
        var projectIdByFeature = features.ToDictionary(f => f.Id, f => f.ProjectId);

        // ── 3. every task as three scalars — enough for the progress counters and for resolving
        //       whether a dependency's target is finished ──
        var taskRows = await db.Tasks
            .AsNoTracking()
            .Select(t => new { t.Id, t.FeatureId, t.Status })
            .ToListAsync(ct);

        // Status of every task ANYWHERE, deliberately not scoped to one project. This resolves the
        // blocked counter, whose SQL form on GET /api/projects/{id} is an INNER JOIN through
        // TaskDependency.DependsOnTaskItem — a join that does not care which project the target
        // lives in, and that drops a dangling target entirely. Scoping this map per project would
        // silently reclassify a cross-project blocker as "target missing".
        var statusByTaskId = taskRows.ToDictionary(t => t.Id, t => t.Status);

        // ── 4. open tasks as ENTITIES — the only rows loaded in full, because TaskRanking ranks
        //       entities. Done tasks are excluded exactly as both existing next-task surfaces
        //       exclude them, so this loads strictly less than they do. ──
        var openTasks = await db.Tasks
            .AsNoTracking()
            .Where(t => t.Status != WorkStatus.Done)
            .ToListAsync(ct);

        // ── 5. dependencies, three scalars each ──
        var dependencies = await db.TaskDependencies
            .AsNoTracking()
            .Select(d => new { d.ProjectId, d.TaskItemId, d.DependsOnTaskItemId })
            .ToListAsync(ct);
        var dependenciesByProject = dependencies.ToLookup(d => d.ProjectId);

        // ── 6. last audited activity per project, aggregated in SQL ──
        var lastActivityByProject = (await db.ActivityEvents
                .AsNoTracking()
                .GroupBy(e => e.ProjectId)
                .Select(g => new { ProjectId = g.Key, LastAt = g.Max(e => e.CreatedAt) })
                .ToListAsync(ct))
            .ToDictionary(x => x.ProjectId, x => x.LastAt);

        // A task whose feature is missing belongs to no project and is unreachable from every
        // project's rollup — the same way ProjectProgressCalculator treats a task whose feature is
        // absent from its input. It is dropped here rather than silently attributed somewhere.
        var taskRowsByProject = taskRows
            .Where(t => projectIdByFeature.ContainsKey(t.FeatureId))
            .ToLookup(t => projectIdByFeature[t.FeatureId]);
        var openTasksByProject = openTasks
            .Where(t => projectIdByFeature.ContainsKey(t.FeatureId))
            .ToLookup(t => projectIdByFeature[t.FeatureId]);

        var summaries = new List<ProjectListSummary>(projects.Count);
        foreach (var p in projects)
        {
            var projectTaskRows = taskRowsByProject[p.Id].ToList();
            var projectOpenTasks = openTasksByProject[p.Id].ToList();
            var projectDependencies = dependenciesByProject[p.Id].ToList();

            // Blocked = "depends on something not yet Done". Resolved through the GLOBAL status
            // map so it matches the endpoint's INNER JOIN: a dependency whose target row does not
            // exist yields no join row there, and no entry here.
            var blockedTaskIds = projectDependencies
                .Where(d => statusByTaskId.TryGetValue(d.DependsOnTaskItemId, out var status)
                            && status != WorkStatus.Done)
                .Select(d => d.TaskItemId)
                .Distinct()
                .ToList();

            var progress = ProjectProgressCalculator.Calculate(
                featuresByProject[p.Id].Select(f => new ProgressFeature(f.Id, f.ParentFeatureId)),
                projectTaskRows.Select(t => new ProgressTask(t.Id, t.FeatureId, t.Status)),
                blockedTaskIds);

            // The ranker gets exactly what GET /api/projects/{id}/next-tasks gives it: the open
            // tasks, the status of EVERY task in the project (not just the open ones — a blocker
            // is usually Done), and a depends-on lookup over the project's dependencies. Limit 1:
            // the card shows one pick, and the ranker clamps to 1–50.
            var statusByProjectTaskId = projectTaskRows.ToDictionary(t => t.Id, t => t.Status);
            var dependsOnByTask = projectDependencies
                .ToLookup(d => d.TaskItemId, d => d.DependsOnTaskItemId);

            var nextTask = TaskRanking
                .RankNextTasks(projectOpenTasks, statusByProjectTaskId, dependsOnByTask, actor, limit: 1)
                .Select(r => new NextTaskSummary(
                    r.Task.Id,
                    r.Task.FeatureId,
                    r.Task.Name,
                    r.Task.Status.ToString(),
                    r.Task.Priority.ToString(),
                    r.Task.ClaimedBy,
                    r.Reason))
                .FirstOrDefault();

            // Claims are read off the OPEN tasks, which is what makes a stale claim on a finished
            // task invisible — see ActiveClaim.TaskCount. Grouping is case-insensitive because
            // claiming is (TaskService.cs:868-875); the label rendered is the stored casing of the
            // group's first task, so the display never invents a spelling the data does not have.
            var activeClaims = projectOpenTasks
                .Where(t => !string.IsNullOrWhiteSpace(t.ClaimedBy))
                .GroupBy(t => t.ClaimedBy!.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => new ActiveClaim(g.First().ClaimedBy!.Trim(), g.Count()))
                .OrderByDescending(c => c.TaskCount)
                .ThenBy(c => c.Actor, StringComparer.OrdinalIgnoreCase)
                .ToList();

            summaries.Add(new ProjectListSummary
            {
                Id = p.Id,
                Name = p.Name,
                Description = p.Description,
                Goals = p.Goals,
                Readme = p.Readme,
                CreatedAt = p.CreatedAt,
                UpdatedAt = p.UpdatedAt,
                FeatureCount = p.FeatureCount,
                TaskCount = p.TaskCount,
                Progress = progress.Project,
                NextTask = nextTask,
                LastActivityAt = lastActivityByProject.TryGetValue(p.Id, out var lastAt)
                    ? lastAt
                    : null,
                ActiveClaims = activeClaims
            });
        }

        return summaries;
    }
}
