namespace KanbanFlow.Services;

public sealed class StoreBusyException(Exception innerException)
    : Exception("The data store is busy. Retry the request.", innerException)
{
}
