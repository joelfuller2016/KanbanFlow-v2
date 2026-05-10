namespace KanbanFlow.Services;

public sealed class FeatureNotFoundException : Exception
{
    public int FeatureId { get; }

    public FeatureNotFoundException(int featureId)
        : base($"Feature {featureId} not found.")
    {
        FeatureId = featureId;
    }
}
