using KanbanFlow.Data;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Services;

/// <summary>
/// Cleanup for the in-app <see cref="TestRunnerService"/>: removes the orphaned
/// <see cref="Models.ActivityEvent"/> rows its throwaway test projects leave behind,
/// WITHOUT touching real deleted-project audit history (issue #85). An event is the
/// runner's own iff it was created during the current run (CreatedAt >= runStartUtc)
/// AND its project no longer exists. Pre-existing orphans predate runStartUtc, so they
/// are preserved. Fixes #150.
/// </summary>
internal static class TestAuditCleanup
{
    public static async Task<int> PurgeOrphanedSinceAsync(
        IDbContextFactory<AppDbContext> dbFactory, DateTime runStartUtc, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ActivityEvents
            .Where(e => e.CreatedAt >= runStartUtc && !db.Projects.Any(p => p.Id == e.ProjectId))
            .ExecuteDeleteAsync(ct);
    }
}
