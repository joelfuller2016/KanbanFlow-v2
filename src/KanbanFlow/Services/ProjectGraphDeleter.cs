using KanbanFlow.Data;
using KanbanFlow.Models;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Services;

/// <summary>
/// FK-safe, leaf-first deletion of a project's object graph (features → tasks →
/// subtasks/acceptance-criteria/comments, plus requirement-link cleanup). This lives in the
/// SERVICE layer and is shared by BOTH the REST endpoints (<c>ApiEndpoints</c>) and
/// <see cref="TaskService"/>, so the two surfaces delete identically and the service layer
/// never has to depend on the API layer.
///
/// Ordering rules that matter for SQLite's real FK enforcement:
/// <list type="bullet">
///   <item><description><c>Feature.ChildFeatures</c> and <c>Subtask.ChildSubtasks</c> are
///   <c>OnDelete(Restrict)</c> (self-referential), so a parent cannot be removed before its
///   children — both trees are deleted deepest-leaf-first via a memoized depth ordering.</description></item>
///   <item><description><c>TaskComment</c>, <c>TaskDependency</c>/<c>BlockingDependencies</c> are
///   <c>OnDelete(Cascade)</c> and <c>Requirement.FeatureId</c>/<c>TaskItemId</c> are
///   <c>OnDelete(SetNull)</c>; those are handled explicitly here (comments removed, requirement
///   links nulled) so behavior is deterministic and the caller controls the single SaveChanges.</description></item>
/// </list>
/// </summary>
internal static class ProjectGraphDeleter
{
    /// <summary>
    /// Removes every child of <paramref name="projectId"/>: the full feature/task/subtask graph
    /// (via <see cref="DeleteFeatureTreesAsync"/>) and all requirements scoped to the project.
    /// Project-scoped requirements are <c>RemoveRange</c>'d; requirement links to features/tasks
    /// inside the tree are nulled by <see cref="DeleteFeatureTreesAsync"/>. Does NOT remove the
    /// <see cref="Project"/> row itself or call SaveChanges — the caller does both.
    /// </summary>
    public static async Task DeleteProjectChildrenAsync(AppDbContext db, int projectId, CancellationToken ct = default)
    {
        var projectFeatureIds = await db.Features
            .Where(f => f.ProjectId == projectId)
            .Select(f => f.Id)
            .ToListAsync(ct);

        await DeleteFeatureTreesAsync(db, projectFeatureIds, ct);

        var requirements = await db.Requirements
            .Where(r => r.ProjectId == projectId)
            .ToListAsync(ct);
        db.Requirements.RemoveRange(requirements);
    }

    /// <summary>Deletes a single feature subtree (root + all descendants and their tasks/children).</summary>
    public static async Task DeleteFeatureTreeAsync(AppDbContext db, int rootFeatureId, CancellationToken ct = default)
    {
        await DeleteFeatureTreesAsync(db, [rootFeatureId], ct);
    }

    /// <summary>
    /// Deletes the union of the feature subtrees rooted at <paramref name="rootFeatureIds"/>,
    /// leaf-first. Tasks under any collected feature have their subtasks (leaf-first), acceptance
    /// criteria, and comments removed; requirement links into the tree are nulled.
    /// </summary>
    public static async Task DeleteFeatureTreesAsync(AppDbContext db, List<int> rootFeatureIds, CancellationToken ct = default)
    {
        if (rootFeatureIds.Count == 0) return;

        var allFeatures = await db.Features.ToListAsync(ct);
        var featureIds = new HashSet<int>();

        void CollectFeatureAndChildren(int featureId)
        {
            if (!featureIds.Add(featureId)) return;

            foreach (var child in allFeatures.Where(f => f.ParentFeatureId == featureId))
            {
                CollectFeatureAndChildren(child.Id);
            }
        }

        foreach (var rootFeatureId in rootFeatureIds)
        {
            CollectFeatureAndChildren(rootFeatureId);
        }

        var tasks = await db.Tasks
            .Where(t => featureIds.Contains(t.FeatureId))
            .ToListAsync(ct);
        var taskIds = tasks.Select(t => t.Id).ToList();

        await EnsureNoNonTerminalExternalWritesAsync(db, taskIds, ct);

        var requirements = await db.Requirements
            .Where(r => (r.FeatureId.HasValue && featureIds.Contains(r.FeatureId.Value)) ||
                        (r.TaskItemId.HasValue && taskIds.Contains(r.TaskItemId.Value)))
            .ToListAsync(ct);

        foreach (var requirement in requirements)
        {
            if (requirement.FeatureId.HasValue && featureIds.Contains(requirement.FeatureId.Value))
                requirement.FeatureId = null;
            if (requirement.TaskItemId.HasValue && taskIds.Contains(requirement.TaskItemId.Value))
                requirement.TaskItemId = null;
        }

        if (taskIds.Count > 0)
        {
            await DeleteSubtasksForTasksAsync(db, taskIds, ct);
            await DeleteAcceptanceCriteriaForTasksAsync(db, taskIds, ct);

            var comments = await db.TaskComments
                .Where(c => taskIds.Contains(c.TaskItemId))
                .ToListAsync(ct);
            db.TaskComments.RemoveRange(comments);
            db.Tasks.RemoveRange(tasks);
        }

        // Features are self-referential Restrict, so remove the deepest first. Depth is memoized
        // (O(N) total) instead of an O(N²) per-node parent scan.
        var parentByFeatureId = allFeatures.ToDictionary(f => f.Id, f => f.ParentFeatureId);
        var featureDepthCache = new Dictionary<int, int>(allFeatures.Count);

        var featuresToRemove = allFeatures
            .Where(f => featureIds.Contains(f.Id))
            .OrderByDescending(f => GetFeatureDepth(f.Id, parentByFeatureId, featureDepthCache, new HashSet<int>()))
            .ToList();
        db.Features.RemoveRange(featuresToRemove);
    }

    /// <summary>Deletes a single subtask subtree (root + descendants), leaf-first, within its owning task.</summary>
    public static async Task DeleteSubtaskTreeAsync(AppDbContext db, int rootSubtaskId, CancellationToken ct = default)
    {
        var root = await db.Subtasks.FindAsync([rootSubtaskId], ct);
        if (root is null) return;

        var allSubtasksForTask = await db.Subtasks
            .Where(s => s.TaskItemId == root.TaskItemId)
            .ToListAsync(ct);

        var subtaskIds = new HashSet<int>();
        CollectSubtaskAndChildren(rootSubtaskId, allSubtasksForTask, subtaskIds);

        var parentBySubtaskId = allSubtasksForTask.ToDictionary(s => s.Id, s => s.ParentSubtaskId);
        var depthCache = new Dictionary<int, int>(allSubtasksForTask.Count);

        var subtasksToRemove = allSubtasksForTask
            .Where(s => subtaskIds.Contains(s.Id))
            .OrderByDescending(s => GetSubtaskDepth(s.Id, parentBySubtaskId, depthCache, new HashSet<int>()))
            .ToList();

        db.Subtasks.RemoveRange(subtasksToRemove);
    }

    /// <summary>
    /// Removes ALL subtasks belonging to <paramref name="taskIds"/>, leaf-first. Subtask.ChildSubtasks
    /// is <c>OnDelete(Restrict)</c>, so deepest-depth nodes are removed before their parents. This is
    /// the single home for leaf-first subtask removal, used by both the project/feature cascade and the
    /// per-task delete path.
    /// </summary>
    public static async Task DeleteSubtasksForTasksAsync(AppDbContext db, List<int> taskIds, CancellationToken ct = default)
    {
        if (taskIds.Count == 0) return;

        var subtasks = await db.Subtasks
            .Where(s => taskIds.Contains(s.TaskItemId))
            .ToListAsync(ct);
        if (subtasks.Count == 0) return;

        // Build id -> parentSubtaskId once (O(n)), then memoize each node's depth so the ordering is
        // O(n) total instead of O(n²) (the old per-node FirstOrDefault scan).
        var parentById = subtasks.ToDictionary(s => s.Id, s => s.ParentSubtaskId);
        var depthCache = new Dictionary<int, int>(subtasks.Count);

        var orderedSubtasks = subtasks
            .OrderByDescending(s => GetSubtaskDepth(s.Id, parentById, depthCache, new HashSet<int>()))
            .ToList();

        db.Subtasks.RemoveRange(orderedSubtasks);
    }

    /// <summary>Convenience overload for removing the subtasks of a single task.</summary>
    public static Task DeleteSubtasksForTaskAsync(AppDbContext db, int taskId, CancellationToken ct = default)
        => DeleteSubtasksForTasksAsync(db, [taskId], ct);

    /// <summary>
    /// A task cannot disappear while an issue create may still be in flight. The journal has no
    /// task FK so its audit evidence survives deletion, but finalization needs the originating task
    /// to exist. Call this before every task/feature/project graph removal (#336).
    /// </summary>
    public static async Task EnsureNoNonTerminalExternalWritesAsync(
        AppDbContext db,
        IReadOnlyCollection<int> taskIds,
        CancellationToken ct = default)
    {
        if (taskIds.Count == 0) return;

        var operation = await db.ExternalWriteOperations
            .AsNoTracking()
            .Where(operation => taskIds.Contains(operation.TaskId) &&
                operation.State != ExternalWriteState.Completed &&
                operation.State != ExternalWriteState.DefinitiveFailure)
            .OrderBy(operation => operation.Id)
            .FirstOrDefaultAsync(ct);

        if (operation is null) return;

        throw new ExternalWorkItemConflictException(
            "external-operation-blocks-delete",
            $"Task {operation.TaskId} cannot be deleted while external write operation " +
            $"{operation.OperationId:D} is {operation.State}.",
            operation.TaskId,
            operation.Provider,
            operation.OperationId,
            operation.State,
            operation.LeaseExpiresAt);
    }

    /// <summary>Removes all acceptance criteria belonging to <paramref name="taskIds"/>.</summary>
    public static async Task DeleteAcceptanceCriteriaForTasksAsync(AppDbContext db, List<int> taskIds, CancellationToken ct = default)
    {
        if (taskIds.Count == 0) return;

        var criteria = await db.AcceptanceCriteria
            .Where(c => taskIds.Contains(c.TaskItemId))
            .ToListAsync(ct);

        db.AcceptanceCriteria.RemoveRange(criteria);
    }

    private static void CollectSubtaskAndChildren(int subtaskId, List<Subtask> allSubtasks, HashSet<int> subtaskIds)
    {
        if (!subtaskIds.Add(subtaskId)) return;

        foreach (var child in allSubtasks.Where(s => s.ParentSubtaskId == subtaskId))
        {
            CollectSubtaskAndChildren(child.Id, allSubtasks, subtaskIds);
        }
    }

    // Memoized depth walk for subtasks: follows parent links via the prebuilt map, caching every
    // node visited along the way. Stops at a root (null parent) or a parent outside this set
    // (defensive). The `visiting` set guards a corrupt self-referential chain from unbounded recursion.
    private static int GetSubtaskDepth(int id, Dictionary<int, int?> parentById, Dictionary<int, int> depthCache, HashSet<int> visiting)
    {
        if (depthCache.TryGetValue(id, out var cached))
            return cached;
        if (!visiting.Add(id))
            return 0; // cycle detected — break defensively

        var parentId = parentById[id];
        int depth;
        if (parentId is null || !parentById.ContainsKey(parentId.Value))
            depth = 0;
        else
            depth = GetSubtaskDepth(parentId.Value, parentById, depthCache, visiting) + 1;

        depthCache[id] = depth;
        return depth;
    }

    // Memoized depth walk for features — same memoized O(N) pattern as the subtask version.
    private static int GetFeatureDepth(int id, Dictionary<int, int?> parentById, Dictionary<int, int> depthCache, HashSet<int> visiting)
    {
        if (depthCache.TryGetValue(id, out var cached))
            return cached;
        if (!visiting.Add(id))
            return 0; // cycle detected — break defensively

        var parentId = parentById[id];
        int depth;
        if (parentId is null || !parentById.ContainsKey(parentId.Value))
            depth = 0;
        else
            depth = GetFeatureDepth(parentId.Value, parentById, depthCache, visiting) + 1;

        depthCache[id] = depth;
        return depth;
    }
}
