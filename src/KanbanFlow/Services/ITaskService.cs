using KanbanFlow.Models;

namespace KanbanFlow.Services;

/// <summary>
/// Audit-aware task mutations. Both the REST API endpoints and the Blazor UI
/// components MUST go through this service so that every change produces an
/// <see cref="ActivityEvent"/> row and a corresponding <see cref="TaskComment"/>
/// where appropriate. Issue #88 ("UI mutations bypass shared validation/audit")
/// is closed by routing every mutation through here.
/// </summary>
public interface ITaskService
{
    /// <summary>
    /// Change a task's status. Idempotent for same-status calls. Sets
    /// <see cref="TaskItem.CompletedAt"/> when transitioning to/from Done.
    /// Writes a <see cref="CommentType.StatusChange"/> comment and a
    /// <c>TaskStatusChanged</c> activity event.
    /// </summary>
    /// <param name="taskId">Target task id.</param>
    /// <param name="newStatus">Desired status.</param>
    /// <param name="actor">
    /// Free-text actor label (e.g. "User", agent name). Used for the comment
    /// author and the activity event's Actor field. <c>null</c> is permitted
    /// and falls back to "System" for the comment author.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated task entity.</returns>
    /// <exception cref="TaskNotFoundException">The task does not exist.</exception>
    Task<TaskItem> ChangeStatusAsync(int taskId, WorkStatus newStatus, string? actor, CancellationToken ct = default);

    /// <summary>
    /// Toggle a task between Done and Todo. When transitioning to Done,
    /// cascade-marks all subtasks completed; when transitioning away from Done,
    /// cascade-unmarks them. Writes a status-change comment and activity event
    /// equivalent to <see cref="ChangeStatusAsync"/>.
    /// </summary>
    /// <param name="taskId">Target task id.</param>
    /// <param name="actor">Actor label; null falls back to "System".</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated task entity.</returns>
    /// <exception cref="TaskNotFoundException">The task does not exist.</exception>
    Task<TaskItem> ToggleDoneAsync(int taskId, string? actor, CancellationToken ct = default);

    // ── Stubs: planned for follow-up PRs ──
    // Each stub closes one or more open issues:
    //   ReassignToFeatureAsync   — closes #83 (cross-project task reassignment)
    //   UpdateAsync              — full task update with field-by-field audit diff
    //   DeleteAsync              — closes #88 UI deletion bypass; cascades subtasks/criteria
    //   ClaimAsync / ReleaseAsync — lifts existing API claim/release into the service
    //   CreateAsync              — task authoring; partial fix for #82-class linkage validation
    // The stubs throw NotImplementedException with TODO markers so the call sites
    // can be wired progressively without a flag-day refactor.

    /// <summary>(Stub) Reassign task to a different feature with project-boundary check.</summary>
    /// <remarks>Closes issue #83. TODO: implement in follow-up PR.</remarks>
    Task<TaskItem> ReassignToFeatureAsync(int taskId, int newFeatureId, string? actor, CancellationToken ct = default);

    /// <summary>(Stub) Full task update with field-by-field audit diff.</summary>
    /// <remarks>TODO: implement in follow-up PR. Today PUT /api/tasks/{id} is still inline in ApiEndpoints.</remarks>
    Task<TaskItem> UpdateAsync(int taskId, TaskUpdate dto, string? actor, CancellationToken ct = default);

    /// <summary>(Stub) Delete task with cascade cleanup of subtasks, criteria, comments, dependencies, and audit emission.</summary>
    /// <remarks>Closes the UI half of #88 for deletes. TODO: implement in follow-up PR.</remarks>
    Task DeleteAsync(int taskId, string? actor, CancellationToken ct = default);
}

/// <summary>Update payload for <see cref="ITaskService.UpdateAsync"/>. Optional fields are <c>null</c> for unchanged.</summary>
public sealed record TaskUpdate(
    string? Name = null,
    string? Description = null,
    WorkStatus? Status = null,
    TaskPriority? Priority = null,
    string? Assignee = null);
