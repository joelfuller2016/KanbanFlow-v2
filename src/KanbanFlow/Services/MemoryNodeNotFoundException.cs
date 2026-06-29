namespace KanbanFlow.Services;

public sealed class MemoryNodeNotFoundException : Exception
{
    public int MemoryNodeId { get; }

    public MemoryNodeNotFoundException(int memoryNodeId)
        : base($"Memory node {memoryNodeId} not found.")
    {
        MemoryNodeId = memoryNodeId;
    }
}
