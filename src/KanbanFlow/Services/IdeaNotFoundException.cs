namespace KanbanFlow.Services;

public sealed class IdeaNotFoundException : Exception
{
    public int IdeaId { get; }

    public IdeaNotFoundException(int ideaId)
        : base($"Idea {ideaId} not found.")
    {
        IdeaId = ideaId;
    }
}
