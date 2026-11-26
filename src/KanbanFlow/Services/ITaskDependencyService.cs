using KanbanFlow.Models;

namespace KanbanFlow.Services;

/// <summary>
/// Owns task-dependency writes. Creation validates the graph and inserts the edge
/// in a single transaction.
/// </summary>
public interface ITaskDependencyService
{
    /// <summary>
    /// Creates one canonical blocked-to-blocking task edge after validating that
    /// both tasks belong to the same project and the resulting graph stays acyclic.
    /// </summary>
    Task<TaskDependencyCreateResult> CreateAsync(
        TaskDependencyCreate request,
        CancellationToken ct = default);

    /// <summary>Deletes an edge and returns whether it existed.</summary>
    Task<bool> DeleteAsync(int id, CancellationToken ct = default);
}

/// <summary>
/// Canonical dependency input. <paramref name="BlockedTaskId"/> is the task that
/// cannot proceed; <paramref name="BlockingTaskId"/> is the task it depends on.
/// </summary>
public sealed record TaskDependencyCreate(
    int BlockedTaskId,
    int BlockingTaskId,
    TaskDependencyType Type);

/// <summary>
/// Created dependency plus the task names required by the existing REST payload.
/// The names are explicit because the service's DbContext is disposed on return.
/// </summary>
public sealed record TaskDependencyCreateResult(
    TaskDependency Dependency,
    string BlockedTaskName,
    string BlockingTaskName);
