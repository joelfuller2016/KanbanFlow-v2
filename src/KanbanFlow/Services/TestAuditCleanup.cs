using KanbanFlow.Data;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Services;

/// <summary>
/// Cleanup for the in-app <see cref="TestRunnerService"/>: removes the orphaned
/// <see cref="Models.ActivityEvent"/> rows its throwaway test projects leave behind,
/// WITHOUT touching real deleted-project audit history (issue #85). An event is the
/// runner's own iff ALL of: it was created during the current run (CreatedAt >= runStartUtc),
/// its project did NOT exist at run start (so it was created by the run), AND its project no
/// longer exists (so it was deleted by the run). The pre-existing exclusion matters because
/// the runner uses the live DB: a user could delete a real, pre-existing project mid-run, and
/// without the exclusion that real project's audit would be wrongly purged. So we preserve both
/// (a) real deleted-project audit that predates the run (#85) and (b) real projects deleted
/// mid-run. Only projects created AND deleted within the run (the runner's own throwaways) are
/// removed. Fixes #150.
/// </summary>
internal static class TestAuditCleanup
{
    /// <summary>
    /// Deletes ActivityEvent rows created at or after the specified run start that belong to projects created during the run and whose projects no longer exist.
    /// </summary>
    /// <param name="runStartUtc">Inclusive lower bound for event creation time; only events with CreatedAt >= this value are considered.</param>
    /// <param name="preExistingProjectIds">Project IDs that existed at the start of the run; events for these projects are preserved.</param>
    /// <param name="ct">Cancellation token to cancel the purge operation.</param>
    /// <returns>The number of ActivityEvent rows deleted.</returns>
    public static async Task<int> PurgeOrphanedSinceAsync(
        IDbContextFactory<AppDbContext> dbFactory, DateTime runStartUtc,
        IReadOnlyCollection<int> preExistingProjectIds, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ActivityEvents
            .Where(e => e.CreatedAt >= runStartUtc
                        && !preExistingProjectIds.Contains(e.ProjectId)   // project did NOT exist at run start
                        && !db.Projects.Any(p => p.Id == e.ProjectId))    // project does NOT exist now
            .ExecuteDeleteAsync(ct);
    }
}
