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

    /// <summary>
    /// Create a new task under a feature. Validates <paramref name="dto"/> (name
    /// non-empty/&lt;= 500 chars, feature exists, status/priority are defined enum
    /// values). Computes <see cref="TaskItem.SortOrder"/> from existing siblings when
    /// not supplied and sets <see cref="TaskItem.CompletedAt"/> when created in the
    /// Done state. Writes a <see cref="CommentType.Created"/> comment and a
    /// <c>TaskCreated</c> activity event.
    /// </summary>
    /// <param name="dto">Create payload.</param>
    /// <param name="actor">Actor label; null falls back to "System" for the comment author.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created task entity (with its database-assigned id).</returns>
    /// <exception cref="ValidationException">Name is empty/too long or an enum value is undefined.</exception>
    /// <exception cref="FeatureNotFoundException">The target feature does not exist.</exception>
    Task<TaskItem> CreateAsync(TaskCreate dto, string? actor, CancellationToken ct = default);

    /// <summary>
    /// Reassign task to a different feature with project-boundary check. Closes #83.
    /// Writes a <see cref="CommentType.FeatureChange"/> comment and a
    /// <c>TaskFeatureChanged</c> activity event.
    /// </summary>
    /// <exception cref="TaskNotFoundException">The task does not exist.</exception>
    /// <exception cref="FeatureNotFoundException">The target feature does not exist.</exception>
    /// <exception cref="CrossProjectReassignmentException">The target feature belongs to a different project.</exception>
    Task<TaskItem> ReassignToFeatureAsync(int taskId, int newFeatureId, string? actor, CancellationToken ct = default);

    /// <summary>
    /// Full task update with field-by-field audit diff. Idempotent when no field
    /// actually changes. Emits a <c>TaskStatusChanged</c> (+ StatusChange comment)
    /// and/or <c>TaskUpdated</c> (+ Note comment) activity event as appropriate.
    /// </summary>
    /// <exception cref="TaskNotFoundException">The task does not exist.</exception>
    /// <exception cref="ValidationException">Name is empty/too long or an enum value is undefined.</exception>
    Task<TaskItem> UpdateAsync(int taskId, TaskUpdate dto, string? actor, CancellationToken ct = default);

    /// <summary>
    /// Delete a task and cascade-clean its children. Subtasks are removed leaf-first
    /// to satisfy the self-referential <c>Subtask.ChildSubtasks</c> Restrict FK;
    /// acceptance criteria are removed explicitly; comments and both dependency
    /// directions are removed by EF cascade. Emits a <c>TaskDeleted</c> activity
    /// event <em>before</em> removal; the audit row survives the delete because
    /// <see cref="ActivityEvent"/> has no FK to the <see cref="TaskItem"/> entity
    /// (its <c>EntityId</c> is a plain int, not a foreign key). Note the event still
    /// carries the <c>ProjectId</c> for attribution. Closes the UI half of #88 for deletes.
    /// </summary>
    /// <param name="taskId">Target task id.</param>
    /// <param name="actor">
    /// Actor label. A blank/null actor is stored as <c>null</c> on the activity event
    /// (no "System" fallback there — only authored comments fall back to "System").
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="TaskNotFoundException">The task does not exist.</exception>
    Task DeleteAsync(int taskId, string? actor, CancellationToken ct = default);

    /// <summary>
    /// Delete a project and its entire object graph (features, tasks, subtasks, acceptance
    /// criteria, comments, dependencies; requirement links are nulled), removing children in
    /// an FK-safe order so the self-referential <c>Subtask.ChildSubtasks</c> Restrict FK is
    /// not violated. Emits a <c>ProjectDeleted</c> activity event (snapshot <em>before</em>
    /// removal; the audit row survives because <see cref="ActivityEvent"/> has no FK to the
    /// project). Both the REST API and the Blazor UI route through here so project deletion is
    /// FK-safe and audited identically regardless of caller (fixes #155; down-payment on #149).
    /// </summary>
    /// <param name="projectId">Target project id.</param>
    /// <param name="actor">Actor label; null is stored as null on the activity event.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ProjectNotFoundException">The project does not exist.</exception>
    Task DeleteProjectAsync(int projectId, string? actor, CancellationToken ct = default);

    /// <summary>
    /// Claim a task for <paramref name="actor"/>. Idempotent when the same actor
    /// re-claims (the original <see cref="TaskItem.ClaimedAt"/> is preserved). Emits
    /// a <c>TaskClaimed</c> activity event. No comment (matches the REST endpoint).
    /// </summary>
    /// <param name="taskId">Target task id.</param>
    /// <param name="actor">Claiming actor; required (non-blank).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated task entity.</returns>
    /// <exception cref="ValidationException">Actor is blank (API maps to 422).</exception>
    /// <exception cref="TaskNotFoundException">The task does not exist.</exception>
    /// <exception cref="TaskClaimConflictException">The task is already claimed by a different actor (API maps to 409).</exception>
    Task<TaskItem> ClaimAsync(int taskId, string actor, CancellationToken ct = default);

    /// <summary>
    /// Release a task previously claimed by <paramref name="actor"/>. Clears
    /// <see cref="TaskItem.ClaimedBy"/>/<see cref="TaskItem.ClaimedAt"/> and emits a
    /// <c>TaskReleased</c> activity event.
    /// </summary>
    /// <param name="taskId">Target task id.</param>
    /// <param name="actor">Releasing actor; required (non-blank).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated task entity.</returns>
    /// <exception cref="ValidationException">Actor is blank (API maps to 422).</exception>
    /// <exception cref="TaskNotFoundException">The task does not exist.</exception>
    /// <exception cref="TaskClaimConflictException">The task is claimed by a different actor (API maps to 409).</exception>
    Task<TaskItem> ReleaseAsync(int taskId, string actor, CancellationToken ct = default);

    /// <summary>
    /// Add a typed user comment to a task and emit a <c>TaskCommentAdded</c> activity
    /// event. Only user-facing <see cref="CommentType"/> values are accepted; system
    /// types (e.g. <see cref="CommentType.StatusChange"/>, <see cref="CommentType.Created"/>)
    /// are rejected. Use <see cref="IsUserCommentType"/> for the allowed set.
    /// </summary>
    /// <param name="taskId">Target task id.</param>
    /// <param name="text">Comment body; required (non-blank).</param>
    /// <param name="author">Comment author label; optional.</param>
    /// <param name="type">Comment type; must be a user-allowed value.</param>
    /// <param name="actor">Actor label for the activity event; optional.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created comment entity.</returns>
    /// <exception cref="TaskNotFoundException">The task does not exist.</exception>
    /// <exception cref="ValidationException">Text is blank or the comment type is not user-allowed.</exception>
    Task<TaskComment> AddCommentAsync(int taskId, string text, string? author, CommentType type, string? actor, CancellationToken ct = default);

    /// <summary>
    /// True when <paramref name="type"/> is a comment type a user/agent is allowed to
    /// author directly (Note, Decision, Question, Blocked, Handoff, Verification,
    /// Comment). System-generated types (StatusChange, Created, FeatureChange, …) are
    /// excluded. Edge layers (REST/MCP) can use this to preserve the existing
    /// "allowed types" 422 body shape.
    /// </summary>
    bool IsUserCommentType(CommentType type);
}

/// <summary>Create payload for <see cref="ITaskService.CreateAsync"/>. Status/Priority null → Backlog/Low.</summary>
public sealed record TaskCreate(
    int FeatureId,
    string Name,
    string? Description = null,
    WorkStatus? Status = null,
    TaskPriority? Priority = null,
    string? Assignee = null,
    int? SortOrder = null);

/// <summary>
/// Update payload for <see cref="ITaskService.UpdateAsync"/>. Optional fields are
/// <c>null</c> for unchanged. <see cref="SortOrder"/> is a non-audited reorder concern:
/// it persists in the SAME transaction as the audited fields (#190) yet never emits an
/// activity event or comment (the #88 audit boundary).
/// </summary>
public sealed record TaskUpdate(
    string? Name = null,
    string? Description = null,
    WorkStatus? Status = null,
    TaskPriority? Priority = null,
    string? Assignee = null,
    int? SortOrder = null);
