using KanbanFlow.Models;

namespace KanbanFlow.Services;

/// <summary>
/// Owns the durable external-work-item identity and the resumable create journal (#336).
/// GitHub I/O remains outside this service: callers must persist <see cref="MarkDispatchedAsync"/>
/// before making the remote request, then finalize the observed result through this boundary.
/// </summary>
public interface IExternalWorkItemService
{
    /// <summary>Create or return one existing same-project task with its verified GitHub identity.</summary>
    Task<ExternalWorkItemProjectionResult> ProjectInboundGitHubIssueAsync(
        ProjectInboundGitHubIssueRequest request,
        string? actor,
        CancellationToken ct = default);

    Task<ExternalWriteLeaseResult> PrepareAsync(
        PrepareExternalWriteRequest request,
        CancellationToken ct = default);

    Task<ExternalWriteLeaseResult> ResumeAsync(
        Guid operationId,
        CancellationToken ct = default);

    Task<ExternalWriteOperation> MarkDispatchedAsync(
        Guid operationId,
        string leaseToken,
        ManualRetryDispatchEvidence? manualRetryEvidence = null,
        CancellationToken ct = default);

    Task<ExternalWriteOperation> RecordFailureAsync(
        Guid operationId,
        string leaseToken,
        ExternalWriteFailureKind kind,
        string error,
        CancellationToken ct = default);

    Task<ExternalWriteOperation> AuthorizeAmbiguousRetryAsync(
        AuthorizeAmbiguousExternalWriteRetryRequest request,
        CancellationToken ct = default);

    Task<ExternalWorkItemLink> FinalizeAsync(
        FinalizeExternalWriteRequest request,
        string? actor,
        CancellationToken ct = default);

    Task<ExternalWorkItemLink> RegisterVerifiedLinkAsync(
        RegisterExternalWorkItemLinkRequest request,
        string? actor,
        CancellationToken ct = default);

    Task<ExternalWorkItemLink> QuarantineAsync(
        QuarantineExternalWorkItemRequest request,
        string? actor,
        CancellationToken ct = default);

    Task<ExternalWriteOperation?> GetOperationAsync(
        Guid operationId,
        CancellationToken ct = default);

    Task<ExternalWorkItemLink?> GetActiveLinkAsync(
        int taskId,
        ExternalProvider provider,
        CancellationToken ct = default);

    Task<IReadOnlyList<ExternalWorkItemLink>> ListActiveLinksAsync(
        ExternalProvider provider,
        string repositoryExternalId,
        CancellationToken ct = default);
}

/// <summary>The exact immutable outbound request frozen before any remote contact.</summary>
public sealed record PrepareExternalWriteRequest(
    int TaskId,
    ExternalProvider Provider,
    string RepositoryExternalId,
    string RepositoryOwner,
    string RepositoryName,
    string Title,
    string Body,
    string? MilestoneTitle,
    string? Label,
    DateTime ExpectedTaskUpdatedAt);

/// <summary>
/// A claimed journal operation. <see cref="SafeToCreate"/> is true for an initial Prepared attempt,
/// or for ManualRetryAuthorized after the server has reserved the lease; the latter still requires
/// fresh zero-occurrence evidence when dispatch is recorded. Every other state must reconcile.
/// </summary>
public sealed record ExternalWriteLeaseResult(
    ExternalWriteOperation Operation,
    string LeaseToken,
    bool SafeToCreate);

/// <summary>
/// Fresh provider evidence required at the dispatch boundary of an operator-authorized retry.
/// Keeping this proof on dispatch makes a crash between resume and dispatch safe to repeat.
/// </summary>
public sealed record ManualRetryDispatchEvidence(
    Guid ConfirmationOperationId,
    int ExpectedAttemptCount,
    bool ScanComplete,
    int ObservedMatchCount,
    int ObservedRawTargetOccurrenceCount,
    DateTimeOffset ScanCompletedAt);

public sealed record FinalizeExternalWriteRequest(
    Guid OperationId,
    string? LeaseToken,
    string RepositoryExternalId,
    string RepositoryOwner,
    string RepositoryName,
    string ExternalNodeId,
    int ExternalNumber,
    string Url);

/// <summary>
/// Explicit operator resolution for the irreducibly ambiguous "dispatched, but no marker is
/// observable" case. This records an accepted risk boundary; it is not provider-side proof.
/// </summary>
public sealed record AuthorizeAmbiguousExternalWriteRetryRequest(
    Guid OperationId,
    Guid ConfirmationOperationId,
    int ExpectedAttemptCount,
    bool ScanComplete,
    int ObservedMatchCount,
    int ObservedRawTargetOccurrenceCount,
    DateTimeOffset ScanCompletedAt,
    string Actor,
    string Reason);

public sealed record RegisterExternalWorkItemLinkRequest(
    int TaskId,
    ExternalProvider Provider,
    string RepositoryExternalId,
    string RepositoryOwner,
    string RepositoryName,
    string ExternalNodeId,
    int ExternalNumber,
    string Url);

/// <summary>Task fields and immutable identity for one atomic inbound GitHub projection.</summary>
public sealed record ProjectInboundGitHubIssueRequest(
    TaskCreate Task,
    string RepositoryExternalId,
    string RepositoryOwner,
    string RepositoryName,
    string ExternalNodeId,
    int ExternalNumber,
    string Url);

/// <summary>Created is false when a same-project identity already owns an unchanged task.</summary>
public sealed record ExternalWorkItemProjectionResult(TaskItem Task, ExternalWorkItemLink Link, bool Created);

public sealed record QuarantineExternalWorkItemRequest(
    int TaskId,
    ExternalProvider Provider,
    string RepositoryExternalId,
    string RepositoryOwner,
    string RepositoryName,
    int? CandidateNumber,
    string? CandidateUrl,
    string Reason);

/// <summary>Whether a failed attempt is proven not to have reached the provider.</summary>
public enum ExternalWriteFailureKind
{
    DefinitivePreCreate = 0,
    AmbiguousOrIncomplete = 1
}
