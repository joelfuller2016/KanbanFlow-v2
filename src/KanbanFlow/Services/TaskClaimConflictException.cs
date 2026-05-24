namespace KanbanFlow.Services;

/// <summary>
/// Thrown when <see cref="ITaskService.ClaimAsync"/> or
/// <see cref="ITaskService.ReleaseAsync"/> is called for a task that is already
/// claimed by a <em>different</em> actor. The API layer maps this to HTTP 409 with
/// a <c>{ message, claimedBy, claimedAt }</c> body so the existing claim/release
/// conflict responses stay byte-for-byte compatible.
/// </summary>
public sealed class TaskClaimConflictException : Exception
{
    public int TaskId { get; }
    public string? ClaimedBy { get; }
    public DateTime? ClaimedAt { get; }

    public TaskClaimConflictException(int taskId, string? claimedBy, DateTime? claimedAt)
        : base($"Task {taskId} is already claimed by {claimedBy}.")
    {
        TaskId = taskId;
        ClaimedBy = claimedBy;
        ClaimedAt = claimedAt;
    }
}
