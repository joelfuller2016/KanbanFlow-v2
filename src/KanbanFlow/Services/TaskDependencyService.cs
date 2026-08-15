using KanbanFlow.Data;
using KanbanFlow.Models;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Services;

/// <summary>
/// SQLite-backed implementation of <see cref="ITaskDependencyService"/>.
/// </summary>
public sealed class TaskDependencyService(IDbContextFactory<AppDbContext> dbFactory)
    : ITaskDependencyService
{
    public async Task<TaskDependencyCreateResult> CreateAsync(
        TaskDependencyCreate request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Enum.IsDefined(request.Type))
            throw new ValidationException($"Invalid dependency type '{request.Type}'.");
        if (request.BlockedTaskId == request.BlockingTaskId)
            throw new ValidationException("A task cannot depend on itself.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Dependency creation is a graph read-modify-write operation. The cycle check only protects
        // the DAG if no other writer can add an edge between the read and this insert. For the pinned
        // Microsoft.Data.Sqlite provider, this overload issues BEGIN IMMEDIATE, reserving the single
        // SQLite writer slot HERE, before any task, duplicate, or graph read. A deferred transaction
        // would recreate the race because both requests could validate the same stale graph.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var tasks = await db.Tasks
            .AsNoTracking()
            .Where(t => t.Id == request.BlockedTaskId || t.Id == request.BlockingTaskId)
            .Select(t => new { t.Id, t.Name, ProjectId = t.Feature.ProjectId })
            .ToListAsync(ct);

        var blockedTask = tasks.SingleOrDefault(t => t.Id == request.BlockedTaskId);
        var blockingTask = tasks.SingleOrDefault(t => t.Id == request.BlockingTaskId);

        if (blockedTask is null)
            throw new TaskNotFoundException(request.BlockedTaskId);
        if (blockingTask is null)
            throw new TaskNotFoundException(request.BlockingTaskId);
        if (blockingTask.ProjectId != blockedTask.ProjectId)
            throw new ValidationException("Task dependencies must stay within the same project.");

        var duplicateExists = await db.TaskDependencies.AnyAsync(d =>
            d.TaskItemId == request.BlockedTaskId &&
            d.DependsOnTaskItemId == request.BlockingTaskId, ct);
        if (duplicateExists)
            throw new TaskDependencyAlreadyExistsException(
                request.BlockedTaskId,
                request.BlockingTaskId);

        var projectDependencies = await db.TaskDependencies
            .Where(d => d.ProjectId == blockedTask.ProjectId)
            .AsNoTracking()
            .ToListAsync(ct);

        if (WouldCreateCycle(
                request.BlockedTaskId,
                request.BlockingTaskId,
                projectDependencies))
        {
            throw new ValidationException("This dependency would create a cycle.");
        }

        var dependency = new TaskDependency
        {
            ProjectId = blockedTask.ProjectId,
            TaskItemId = request.BlockedTaskId,
            DependsOnTaskItemId = request.BlockingTaskId,
            Type = request.Type
        };

        db.TaskDependencies.Add(dependency);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return new TaskDependencyCreateResult(
            dependency,
            blockedTask.Name,
            blockingTask.Name);
    }

    private static bool WouldCreateCycle(
        int blockedTaskId,
        int blockingTaskId,
        IReadOnlyCollection<TaskDependency> projectDependencies)
    {
        var dependenciesByBlockedTask = projectDependencies
            .ToLookup(d => d.TaskItemId, d => d.DependsOnTaskItemId);
        var visited = new HashSet<int>();

        bool HasPath(int currentTaskId)
        {
            if (currentTaskId == blockedTaskId)
                return true;
            if (!visited.Add(currentTaskId))
                return false;

            return dependenciesByBlockedTask[currentTaskId].Any(HasPath);
        }

        return HasPath(blockingTaskId);
    }
}
