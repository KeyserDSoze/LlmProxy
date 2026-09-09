namespace LlmProxy.Domain.Nodes;

public sealed class InferenceNode
{
    private InferenceNode()
    {
    }

    public InferenceNode(string name, string baseAddress, int weight = 1, int maxConcurrency = 4)
    {
        Rename(name);
        SetBaseAddress(baseAddress);
        SetCapacity(weight, maxConcurrency);
    }

    public Guid Id { get; private set; } = Guid.NewGuid();
    public string Name { get; private set; } = string.Empty;
    public string BaseAddress { get; private set; } = string.Empty;
    public bool Enabled { get; private set; } = true;
    public NodeStatus Status { get; private set; } = NodeStatus.Unknown;
    public int Weight { get; private set; } = 1;
    public int MaxConcurrency { get; private set; } = 4;
    public DateTimeOffset? LastHealthCheckUtc { get; private set; }

    public void Update(string name, string baseAddress, int weight, int maxConcurrency)
    {
        Rename(name);
        SetBaseAddress(baseAddress);
        SetCapacity(weight, maxConcurrency);
    }

    public void Enable()
    {
        Enabled = true;
        if (Status == NodeStatus.Disabled)
        {
            Status = NodeStatus.Unknown;
        }
    }

    public void Disable()
    {
        Enabled = false;
        Status = NodeStatus.Disabled;
    }

    public void StartDrain()
    {
        if (!Enabled)
        {
            throw new InvalidOperationException("A disabled node cannot be drained.");
        }

        Status = NodeStatus.Draining;
    }

    public void SetHealth(NodeStatus status, DateTimeOffset checkedAtUtc)
    {
        if (!Enabled || Status == NodeStatus.Draining)
        {
            return;
        }

        Status = status;
        LastHealthCheckUtc = checkedAtUtc;
    }

    private void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Node name is required.", nameof(name));
        }

        Name = name.Trim();
    }

    private void SetBaseAddress(string baseAddress)
    {
        if (!Uri.TryCreate(baseAddress, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Node base address must be an absolute HTTP(S) URI.", nameof(baseAddress));
        }

        BaseAddress = baseAddress.TrimEnd('/');
    }

    private void SetCapacity(int weight, int maxConcurrency)
    {
        if (weight < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(weight), "Weight must be at least 1.");
        }

        if (maxConcurrency < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrency), "Max concurrency must be at least 1.");
        }

        Weight = weight;
        MaxConcurrency = maxConcurrency;
    }
}
