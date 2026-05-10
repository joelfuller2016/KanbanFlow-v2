namespace KanbanFlow.Services;

/// <summary>
/// Thrown when <see cref="ITaskService.ReassignToFeatureAsync"/> would move a task
/// to a feature owned by a different project. Closes issue #83 — task hierarchies
/// must stay rooted in a single project.
/// </summary>
public sealed class CrossProjectReassignmentException : Exception
{
    public int TaskId { get; }
    public int SourceProjectId { get; }
    public int TargetProjectId { get; }

    public CrossProjectReassignmentException(int taskId, int sourceProjectId, int targetProjectId)
        : base($"Cannot reassign task {taskId} from project {sourceProjectId} to a feature in project {targetProjectId}.")
    {
        TaskId = taskId;
        SourceProjectId = sourceProjectId;
        TargetProjectId = targetProjectId;
    }
}
