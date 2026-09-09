using System.Text.Json;
using KanbanFlow.Data;
using KanbanFlow.Models;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Services.Workflows;

public sealed partial class WorkspaceService
{
    // This snapshot belongs to one operation and its existing transaction. Keeping it
    // local prevents a later human edit from reusing a cached evidence or edit token.
    private sealed class TaskSnapshot(IReadOnlyDictionary<int, TaskItem> tasks)
    {
        private readonly Dictionary<int, string> stateHashes = [];

        public static async Task<TaskSnapshot> LoadAsync(AppDbContext db, IEnumerable<int> taskIds, CancellationToken ct)
        {
            var roots = taskIds.Distinct().ToArray();
            if (roots.Length == 0) return new(new Dictionary<int, TaskItem>());

            // UNION projects only the identity, so shared dependencies and even a legacy
            // cycle are visited once. One parameter carries every root; no SQL is built
            // from a caller's text. Include absent roots/targets so StateHash can reject
            // an incomplete graph instead of silently hashing a partial one.
            var rootJson = JsonSerializer.Serialize(roots);
            var reachableIds = await db.Database.SqlQuery<int>($"""
                WITH RECURSIVE reachable(Id) AS (
                    SELECT CAST(value AS INTEGER) FROM json_each({rootJson})
                    UNION
                    SELECT edge.DependsOnTaskItemId
                    FROM TaskDependencies AS edge
                    INNER JOIN reachable ON edge.TaskItemId = reachable.Id
                )
                SELECT Id AS Value FROM reachable
                """).ToArrayAsync(ct);
            var graph = await db.Tasks.AsNoTracking().AsSplitQuery()
                .Where(t => reachableIds.Contains(t.Id)).OrderBy(t => t.Id)
                .Include(t => t.AcceptanceCriteria).Include(t => t.Subtasks).Include(t => t.Dependencies)
                .ToDictionaryAsync(t => t.Id, ct);
            return new(graph);
        }

        public string PublicationHash(int taskId)
        {
            var task = tasks[taskId];
            return Hash(new { Scope = StateHash(taskId), task.Priority, task.Assignee, task.Status, task.UpdatedAt,
                Checks = task.AcceptanceCriteria.OrderBy(c => c.Id).Select(c => c.Completed) });
        }

        public string StateHash(int taskId)
        {
            if (stateHashes.TryGetValue(taskId, out var cached)) return cached;
            var reachable = new Dictionary<int, TaskItem>();
            var pending = new Queue<int>();
            pending.Enqueue(taskId);
            while (pending.TryDequeue(out var id))
            {
                if (reachable.ContainsKey(id)) continue;
                if (!tasks.TryGetValue(id, out var task)) return stateHashes[taskId] = "missing-task";
                reachable.Add(id, task);
                foreach (var edge in task.Dependencies) pending.Enqueue(edge.DependsOnTaskItemId);
            }

            // The property names, order and per-root status exclusions are the persisted
            // evidence format. Changing them would invalidate already recorded results.
            return stateHashes[taskId] = Hash(reachable.OrderBy(p => p.Key).Select(p => new
            {
                p.Key, p.Value.FeatureId, p.Value.Name, p.Value.Description,
                Criteria = p.Value.AcceptanceCriteria.OrderBy(c => c.Id).Select(c => new { c.Id, c.Text }),
                Subtasks = p.Value.Subtasks.OrderBy(c => c.Id).Select(c => new { c.Id, c.Text }),
                Dependencies = p.Value.Dependencies.OrderBy(d => d.DependsOnTaskItemId).Select(d => new { d.DependsOnTaskItemId, d.Type }),
                PrerequisiteStatus = p.Key == taskId ? (WorkStatus?)null : p.Value.Status,
                PrerequisiteCompletedAt = p.Key == taskId ? null : p.Value.CompletedAt,
                PrerequisiteChecks = p.Key == taskId ? null : p.Value.AcceptanceCriteria.OrderBy(c => c.Id).Select(c => c.Completed).ToArray()
            }));
        }
    }
}
