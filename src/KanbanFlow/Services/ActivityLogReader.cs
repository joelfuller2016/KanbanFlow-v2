using KanbanFlow.Data;
using KanbanFlow.Models;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Services;

/// <summary>The same retained history scope for humans, REST and MCP, including child records.</summary>
public static class ActivityLogReader
{
    public static IQueryable<ActivityEvent> Query(AppDbContext db, int? projectId = null, int? taskId = null,
        string? entityType = null, int? entityId = null)
    {
        var query = db.ActivityEvents.AsNoTracking();
        if (projectId is int project) query = query.Where(e => e.ProjectId == project);
        if (taskId is int task) query = query.Where(e => (e.EntityType == "Task" && e.EntityId == task) || e.TaskId == task || e.RelatedTaskId == task);
        if (entityType is not null) query = query.Where(e => e.EntityType == entityType && e.EntityId == entityId);
        return query;
    }

    public static async Task<ActivityPage> ReadAsync(AppDbContext db, int? projectId = null, int? taskId = null,
        int? limit = null, int? beforeId = null, CancellationToken ct = default)
    {
        var query = Query(db, projectId, taskId);
        var total = await query.CountAsync(ct);
        if (beforeId is int cursor)
        {
            var anchor = await query.SingleOrDefaultAsync(e => e.Id == cursor, ct)
                ?? throw new ValidationException("The activity cursor does not belong to this history.");
            query = query.Where(e => e.CreatedAt < anchor.CreatedAt || (e.CreatedAt == anchor.CreatedAt && e.Id < cursor));
        }
        var take = Math.Clamp(limit ?? 100, 1, 500);
        var rows = await query.OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id).Take(take + 1).ToListAsync(ct);
        var hasMore = rows.Count > take;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        return new(rows, total, hasMore ? rows[^1].Id : null);
    }
}

public sealed record ActivityPage(IReadOnlyList<ActivityEvent> Items, int Total, int? NextBeforeId);
