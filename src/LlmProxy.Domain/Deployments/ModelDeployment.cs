namespace LlmProxy.Domain.Deployments;

public sealed class ModelDeployment
{
    private ModelDeployment()
    {
    }

    public ModelDeployment(Guid nodeId, Guid modelId, int weight = 1, int? maxConcurrency = null)
    {
        if (nodeId == Guid.Empty)
        {
            throw new ArgumentException("Node id is required.", nameof(nodeId));
        }

        if (modelId == Guid.Empty)
        {
            throw new ArgumentException("Model id is required.", nameof(modelId));
        }

        NodeId = nodeId;
        ModelId = modelId;
        SetCapacity(weight, maxConcurrency);
    }

    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid NodeId { get; private set; }
    public Guid ModelId { get; private set; }
    public bool Enabled { get; private set; } = true;
    public int Weight { get; private set; } = 1;
    public int? MaxConcurrency { get; private set; }

    public void SetCapacity(int weight, int? maxConcurrency)
    {
        if (weight < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(weight), "Weight must be at least 1.");
        }

        if (maxConcurrency is < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrency), "Max concurrency must be null or at least 1.");
        }

        Weight = weight;
        MaxConcurrency = maxConcurrency;
    }

    public void Enable() => Enabled = true;
    public void Disable() => Enabled = false;
}
