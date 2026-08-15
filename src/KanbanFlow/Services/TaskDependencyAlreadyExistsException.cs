namespace KanbanFlow.Services;

/// <summary>
/// Thrown when the exact blocked-to-blocking edge already exists. The API maps
/// this domain conflict to its existing HTTP 409 response.
/// </summary>
public sealed class TaskDependencyAlreadyExistsException : Exception
{
    public int BlockedTaskId { get; }
    public int BlockingTaskId { get; }

    public TaskDependencyAlreadyExistsException(int blockedTaskId, int blockingTaskId)
        : base("This task dependency already exists.")
    {
        BlockedTaskId = blockedTaskId;
        BlockingTaskId = blockingTaskId;
    }
}
