namespace KanbanFlow.Services;

/// <summary>The authorized release did not apply, or the task has incomplete claim data.</summary>
public sealed class TaskClaimStateChangedException(int taskId, string? claimedBy, DateTime? claimedAt)
    : Exception(string.IsNullOrWhiteSpace(claimedBy)
        ? $"Task {taskId} has incomplete claim data. Claim the task before retrying the release."
        : $"Task {taskId}'s claim could not be released. Refresh the task and retry.")
{
    public string? ClaimedBy { get; } = claimedBy;
    public DateTime? ClaimedAt { get; } = claimedAt;
}
