namespace KanbanFlow.Services;

/// <summary>
/// The single project-deletion path shared by the REST API and the Blazor UI (#155). Deletes the
/// whole project graph leaf-first (features + subtasks have RESTRICT self-FKs, so a plain cascade
/// aborts) and emits a <c>ProjectDeleted</c> audit event.
/// </summary>
public interface IProjectService
{
    /// <summary>Deletes the project and its entire graph, emitting a ProjectDeleted activity event.
    /// Returns <c>false</c> if the project does not exist.</summary>
    Task<bool> DeleteAsync(int projectId, string? actor, CancellationToken ct = default);
}
