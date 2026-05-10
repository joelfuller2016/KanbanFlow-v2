namespace KanbanFlow.Services;

/// <summary>
/// Service-layer validation failure (bad input from caller). Distinct from
/// <see cref="ArgumentException"/> so callers can `catch` it without masking
/// genuine programming errors, and so the API surfaces a clean message instead
/// of "(Parameter 'dto')" suffixes from .NET's default ArgumentException format.
/// </summary>
public sealed class ValidationException : Exception
{
    public ValidationException(string message) : base(message) { }
}
