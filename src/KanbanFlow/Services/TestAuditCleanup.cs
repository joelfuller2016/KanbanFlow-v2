using System.Text.Json;
using KanbanFlow.Data;
using KanbanFlow.Models;
using KanbanFlow.Models.Workflows;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Services;

/// <summary>
/// Removes audit from deleted fixtures whose identities the test runner recorded.
/// Real work can produce orphaned events during the same time window.
/// </summary>
internal static class TestAuditCleanup
{
    // ProjectId can also attribute independent memory and workflow history.
    private static readonly string[] ProjectGraphAuditTypes =
    [
        nameof(Project), nameof(Feature), "Task", nameof(Subtask), nameof(AcceptanceCriterion),
        nameof(TaskComment), nameof(TaskDependency), nameof(Requirement), nameof(ProjectRepository),
        nameof(ExternalWorkItemLink)
    ];

    /// <summary>
    /// Deletes this run's fixture audit after its project and idea rows have been removed.
    /// </summary>
    /// <param name="runStartUtc">Inclusive lower bound for event creation time; only events with CreatedAt >= this value are considered.</param>
    /// <param name="preExistingProjectIds">Project IDs that existed at the start of the run; events for these projects are preserved.</param>
    /// <param name="fixtureProjectIds">Project IDs obtained from this run's creates, promotions or marker sweep.</param>
    /// <param name="fixtureIdeaIds">Idea IDs obtained from this run's creates or marker sweep.</param>
    /// <param name="ct">Cancellation token to cancel the purge operation.</param>
    /// <returns>The number of ActivityEvent rows deleted.</returns>
    public static async Task<int> PurgeOrphanedSinceAsync(
        IDbContextFactory<AppDbContext> dbFactory, DateTime runStartUtc,
        IReadOnlyCollection<int> preExistingProjectIds,
        IReadOnlyCollection<int> fixtureProjectIds,
        IReadOnlyCollection<int> fixtureIdeaIds, CancellationToken ct = default)
    {
        if (fixtureProjectIds.Count == 0 && fixtureIdeaIds.Count == 0)
            return 0;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var orphaned = db.ActivityEvents
            .Where(e => e.CreatedAt >= runStartUtc
                        && !preExistingProjectIds.Contains(e.ProjectId)
                        && !db.Projects.Any(p => p.Id == e.ProjectId));

        var deletedIdeaIds = fixtureIdeaIds.Except(await db.Ideas
            .Where(i => fixtureIdeaIds.Contains(i.Id)).Select(i => i.Id).ToListAsync(ct)).ToArray();
        var requestAuditIds = await FindFixtureWorkflowAuditAsync(orphaned, deletedIdeaIds, ct);

        return await orphaned
            .Where(e => (e.EntityType == "Idea" && deletedIdeaIds.Contains(e.EntityId))
                || (e.ProjectId > 0 && ProjectGraphAuditTypes.Contains(e.EntityType)
                    && fixtureProjectIds.Contains(e.ProjectId))
                || requestAuditIds.Contains(e.Id))
            .ExecuteDeleteAsync(ct);
    }

    private static async Task<int[]> FindFixtureWorkflowAuditAsync(
        IQueryable<ActivityEvent> orphaned, int[] deletedIdeaIds, CancellationToken ct)
    {
        if (deletedIdeaIds.Length == 0)
            return [];

        // Fixture ideas own their captured requests and messages even when no project is
        // assigned. Follow the exact keys in the audited unlink and deletion snapshots.
        var rows = await orphaned.Where(e =>
                (e.EntityType == "WorkRequest" && (e.EventType == "WorkRequestUpdated" || e.EventType == "WorkRequestDeleted"))
                || (e.EntityType == "RequestMessage" && e.EventType == "RequestMessageDeleted"))
            .Select(e => new { e.Id, e.EntityKey, e.EntityType, e.BeforeJson })
            .ToListAsync(ct);
        var requestIds = new HashSet<Guid>();
        var auditIds = new List<int>();
        foreach (var row in rows.Where(e => e.EntityType == "WorkRequest"))
        {
            if (Guid.TryParse(row.EntityKey, out var requestId)
                && ReadSnapshotProperty<int>(row.BeforeJson, "ideaId") is int ideaId
                && deletedIdeaIds.Contains(ideaId))
            {
                requestIds.Add(requestId);
                auditIds.Add(row.Id);
            }
        }

        foreach (var row in rows.Where(e => e.EntityType == "RequestMessage"))
            if (ReadSnapshotProperty<Guid>(row.BeforeJson, "requestId") is Guid requestId
                && requestIds.Contains(requestId))
                auditIds.Add(row.Id);

        return auditIds.ToArray();
    }

    private static T? ReadSnapshotProperty<T>(string? snapshot, string property) where T : struct
    {
        if (snapshot is null)
            return null;
        try
        {
            using var json = JsonDocument.Parse(snapshot);
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty(property, out var value)
                ? value.Deserialize<T?>()
                : null;
        }
        catch (JsonException)
        {
            // Invalid historical snapshots cannot prove fixture ownership.
            return null;
        }
    }
}
