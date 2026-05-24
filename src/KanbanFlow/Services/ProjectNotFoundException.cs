namespace KanbanFlow.Services;

public sealed class ProjectNotFoundException : Exception
{
    public int ProjectId { get; }

    public ProjectNotFoundException(int projectId)
        : base($"Project {projectId} not found.")
    {
        ProjectId = projectId;
    }
}
