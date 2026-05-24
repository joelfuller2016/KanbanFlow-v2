using KanbanFlow.Data;
using KanbanFlow.Models;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Services;

/// <summary>
/// Leaf-first deletion of project / feature / subtask graphs. EF Core + SQLite enforce FKs, and the
/// <c>Feature→ChildFeatures</c> and <c>Subtask→ChildSubtasks</c> self-relationships are
/// <see cref="DeleteBehavior.Restrict"/>, so a plain cascade aborts when a parent still has
/// children. These helpers stage removals deepest-first to dodge the RESTRICT FK; the caller owns
/// the <c>SaveChanges</c>/transaction. Shared by the REST DELETE endpoints and
/// <see cref="ProjectService"/> (and thus the Blazor UI) so every delete path behaves identically (#155).
/// </summary>
internal static class GraphDeletion
{
    public static async Task DeleteProjectChildrenAsync(AppDbContext db, int projectId)
    {
        var projectFeatureIds = await db.Features
            .Where(f => f.ProjectId == projectId)
            .Select(f => f.Id)
            .ToListAsync();

        await DeleteFeatureTreesAsync(db, projectFeatureIds);

        var requirements = await db.Requirements
            .Where(r => r.ProjectId == projectId)
            .ToListAsync();
        db.Requirements.RemoveRange(requirements);
    }

    public static async Task DeleteFeatureTreeAsync(AppDbContext db, int rootFeatureId)
    {
        await DeleteFeatureTreesAsync(db, [rootFeatureId]);
    }

    public static async Task DeleteSubtaskTreeAsync(AppDbContext db, int rootSubtaskId)
    {
        var root = await db.Subtasks.FindAsync(rootSubtaskId);
        if (root is null) return;

        var allSubtasksForTask = await db.Subtasks
            .Where(s => s.TaskItemId == root.TaskItemId)
            .ToListAsync();

        var subtaskIds = new HashSet<int>();
        CollectSubtaskAndChildren(rootSubtaskId, allSubtasksForTask, subtaskIds);

        var subtasksToRemove = allSubtasksForTask
            .Where(s => subtaskIds.Contains(s.Id))
            .OrderByDescending(s => GetSubtaskDepth(s, allSubtasksForTask))
            .ToList();

        db.Subtasks.RemoveRange(subtasksToRemove);
    }

    private static async Task DeleteFeatureTreesAsync(AppDbContext db, List<int> rootFeatureIds)
    {
        if (rootFeatureIds.Count == 0) return;

        var allFeatures = await db.Features.ToListAsync();
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
            .ToListAsync();
        var taskIds = tasks.Select(t => t.Id).ToList();

        var requirements = await db.Requirements
            .Where(r => (r.FeatureId.HasValue && featureIds.Contains(r.FeatureId.Value)) ||
                        (r.TaskItemId.HasValue && taskIds.Contains(r.TaskItemId.Value)))
            .ToListAsync();

        foreach (var requirement in requirements)
        {
            if (requirement.FeatureId.HasValue && featureIds.Contains(requirement.FeatureId.Value))
                requirement.FeatureId = null;
            if (requirement.TaskItemId.HasValue && taskIds.Contains(requirement.TaskItemId.Value))
                requirement.TaskItemId = null;
        }

        if (taskIds.Count > 0)
        {
            await DeleteSubtasksForTasksAsync(db, taskIds);
            await DeleteAcceptanceCriteriaForTasksAsync(db, taskIds);

            var comments = await db.TaskComments
                .Where(c => taskIds.Contains(c.TaskItemId))
                .ToListAsync();
            db.TaskComments.RemoveRange(comments);
            db.Tasks.RemoveRange(tasks);
        }

        var featuresToRemove = allFeatures
            .Where(f => featureIds.Contains(f.Id))
            .OrderByDescending(f => GetFeatureDepth(f, allFeatures))
            .ToList();
        db.Features.RemoveRange(featuresToRemove);
    }

    private static int GetFeatureDepth(Feature feature, List<Feature> allFeatures)
    {
        var depth = 0;
        var current = feature;
        while (current.ParentFeatureId.HasValue)
        {
            var parent = allFeatures.FirstOrDefault(f => f.Id == current.ParentFeatureId.Value);
            if (parent is null) break;
            depth++;
            current = parent;
        }
        return depth;
    }

    private static async Task DeleteSubtasksForTasksAsync(AppDbContext db, List<int> taskIds)
    {
        if (taskIds.Count == 0) return;

        var subtasks = await db.Subtasks
            .Where(s => taskIds.Contains(s.TaskItemId))
            .ToListAsync();

        var orderedSubtasks = subtasks
            .OrderByDescending(s => GetSubtaskDepth(s, subtasks))
            .ToList();

        db.Subtasks.RemoveRange(orderedSubtasks);
    }

    private static async Task DeleteAcceptanceCriteriaForTasksAsync(AppDbContext db, List<int> taskIds)
    {
        if (taskIds.Count == 0) return;

        var criteria = await db.AcceptanceCriteria
            .Where(c => taskIds.Contains(c.TaskItemId))
            .ToListAsync();

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

    private static int GetSubtaskDepth(Subtask subtask, List<Subtask> allSubtasks)
    {
        var depth = 0;
        var current = subtask;
        while (current.ParentSubtaskId.HasValue)
        {
            var parent = allSubtasks.FirstOrDefault(s => s.Id == current.ParentSubtaskId.Value);
            if (parent is null) break;
            depth++;
            current = parent;
        }
        return depth;
    }
}
