namespace KanbanFlow.Services;

public sealed class TaskNotFoundException : Exception
{
    public int TaskId { get; }

    public TaskNotFoundException(int taskId)
        : base($"Task {taskId} not found.")
    {
        TaskId = taskId;
    }
}
