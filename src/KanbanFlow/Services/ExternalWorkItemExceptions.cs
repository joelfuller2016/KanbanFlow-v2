using KanbanFlow.Models;

namespace KanbanFlow.Services;

/// <summary>
/// A deterministic external-link, operation, lease, identity, or state conflict. REST callers map
/// this to HTTP 409 and may use the structured fields to explain the winning durable state.
/// </summary>
public sealed class ExternalWorkItemConflictException : Exception
{
    public string ConflictCode { get; }
    public int? TaskId { get; }
    public ExternalProvider? Provider { get; }
    public Guid? OperationId { get; }
    public ExternalWriteState? OperationState { get; }
    public DateTime? LeaseExpiresAt { get; }

    public ExternalWorkItemConflictException(
        string conflictCode,
        string message,
        int? taskId = null,
        ExternalProvider? provider = null,
        Guid? operationId = null,
        ExternalWriteState? operationState = null,
        DateTime? leaseExpiresAt = null)
        : base(message)
    {
        ConflictCode = conflictCode;
        TaskId = taskId;
        Provider = provider;
        OperationId = operationId;
        OperationState = operationState;
        LeaseExpiresAt = leaseExpiresAt;
    }
}

public sealed class ExternalWriteOperationNotFoundException : Exception
{
    public Guid OperationId { get; }

    public ExternalWriteOperationNotFoundException(Guid operationId)
        : base($"External write operation {operationId:D} not found.")
    {
        OperationId = operationId;
    }
}
