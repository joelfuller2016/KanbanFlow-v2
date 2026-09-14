namespace KanbanFlow.Models;

/// <summary>
/// Durable outbox/journal entry for creating an external issue. The operation and its exact payload
/// are persisted before GitHub is contacted; retries reuse the same marker and payload (#336).
/// This row intentionally has no task foreign key so failure evidence survives task deletion, just
/// like <see cref="ActivityEvent"/>.
/// </summary>
public sealed class ExternalWriteOperation
{
    public int Id { get; set; }
    public Guid OperationId { get; set; }
    public int TaskId { get; set; }
    public int ProjectId { get; set; }
    public ExternalProvider Provider { get; set; }
    public required string RepositoryExternalId { get; set; }
    public required string RepositoryOwner { get; set; }
    public required string RepositoryName { get; set; }
    public required string Title { get; set; }
    public required string Body { get; set; }
    public string? MilestoneTitle { get; set; }
    public string? Label { get; set; }
    /// <summary>SHA-256 of the canonical immutable request fields.</summary>
    public required string RequestHash { get; set; }
    /// <summary>The task version the frozen request was prepared from.</summary>
    public DateTime ExpectedTaskUpdatedAt { get; set; }
    public ExternalWriteState State { get; set; } = ExternalWriteState.Prepared;
    public string? LeaseToken { get; set; }
    public DateTime? LeaseExpiresAt { get; set; }
    public int AttemptCount { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public DateTime? DispatchedAt { get; set; }
    public string? LastError { get; set; }
    public string? ResultingExternalNodeId { get; set; }
    public int? ResultingExternalNumber { get; set; }
    public string? ResultingUrl { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}

// ON-DISK CONTRACT: persisted as a raw ordinal int. Append only; never reorder/remove.
public enum ExternalWriteState
{
    /// <summary>No GitHub request has been dispatched; a lease holder may create.</summary>
    Prepared = 0,
    /// <summary>A request may have reached GitHub; recovery must reconcile and never auto-create.</summary>
    Dispatched = 1,
    Completed = 2,
    /// <summary>The remote outcome is ambiguous or reconciliation was incomplete.</summary>
    NeedsReview = 3,
    /// <summary>A known pre-create failure; explicit resume may safely retry the same operation.</summary>
    DefinitiveFailure = 4,
    /// <summary>
    /// An operator accepted the residual risk after a fresh complete zero-marker scan. This is a
    /// policy authorization, not proof from GitHub. Resume preserves this state, and dispatch is
    /// allowed only with another fresh zero-occurrence scan so a pre-dispatch crash stays gated.
    /// </summary>
    ManualRetryAuthorized = 5
}
