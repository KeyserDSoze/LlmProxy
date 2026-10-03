using System.Text.Json.Serialization;

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
    [JsonIgnore]
    public string? UpstreamBearerTokenCiphertext { get; private set; }
    public string? HardwareMetricsBaseAddress { get; private set; }
    public string? ManagementBaseAddress { get; private set; }
    [JsonIgnore]
    public string? ManagementBearerTokenCiphertext { get; private set; }
    public bool Enabled { get; private set; } = true;
    public NodeStatus Status { get; private set; } = NodeStatus.Unknown;
    public int Weight { get; private set; } = 1;
    public int MaxConcurrency { get; private set; } = 4;
    public DateTimeOffset? LastHealthCheckUtc { get; private set; }
    public DateTimeOffset? LastHealthyAtUtc { get; private set; }
    public long? LastHealthLatencyMilliseconds { get; private set; }
    public string? LastHealthError { get; private set; }
    public int ConsecutiveHealthSuccesses { get; private set; }
    public int ConsecutiveHealthFailures { get; private set; }

    public void Update(string name, string baseAddress, int weight, int maxConcurrency)
    {
        Rename(name);
        SetBaseAddress(baseAddress);
        SetCapacity(weight, maxConcurrency);
    }

    public void SetUpstreamBearerTokenCiphertext(string? ciphertext)
    {
        UpstreamBearerTokenCiphertext = string.IsNullOrWhiteSpace(ciphertext) ? null : ciphertext;
    }

    public void SetHardwareMetricsBaseAddress(string? baseAddress)
    {
        HardwareMetricsBaseAddress = string.IsNullOrWhiteSpace(baseAddress)
            ? null
            : InferenceEndpoint.NormalizeBaseAddress(baseAddress);
    }

    public void SetManagementBaseAddress(string? baseAddress)
    {
        ManagementBaseAddress = string.IsNullOrWhiteSpace(baseAddress)
            ? null
            : InferenceEndpoint.NormalizeBaseAddress(baseAddress);
    }

    public void SetManagementBearerTokenCiphertext(string? ciphertext)
    {
        ManagementBearerTokenCiphertext = string.IsNullOrWhiteSpace(ciphertext) ? null : ciphertext;
    }

    public void Enable()
    {
        Enabled = true;
        if (Status == NodeStatus.Disabled)
        {
            Status = NodeStatus.Unknown;
            ConsecutiveHealthSuccesses = 0;
            ConsecutiveHealthFailures = 0;
            LastHealthError = null;
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

    public void RecordHealthSuccess(
        DateTimeOffset checkedAtUtc,
        long latencyMilliseconds,
        int healthyAfterSuccesses = 2)
    {
        if (!CanUpdateHealth())
        {
            return;
        }

        if (healthyAfterSuccesses < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(healthyAfterSuccesses));
        }

        LastHealthCheckUtc = checkedAtUtc;
        LastHealthyAtUtc = checkedAtUtc;
        LastHealthLatencyMilliseconds = Math.Max(0, latencyMilliseconds);
        LastHealthError = null;
        ConsecutiveHealthSuccesses++;
        ConsecutiveHealthFailures = 0;
        Status = ConsecutiveHealthSuccesses >= healthyAfterSuccesses
            ? NodeStatus.Healthy
            : NodeStatus.Degraded;
    }

    public void RecordHealthFailure(
        DateTimeOffset checkedAtUtc,
        long latencyMilliseconds,
        string? error,
        int unhealthyAfterFailures = 3)
    {
        if (!CanUpdateHealth())
        {
            return;
        }

        if (unhealthyAfterFailures < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(unhealthyAfterFailures));
        }

        LastHealthCheckUtc = checkedAtUtc;
        LastHealthLatencyMilliseconds = Math.Max(0, latencyMilliseconds);
        LastHealthError = NormalizeError(error);
        ConsecutiveHealthFailures++;
        ConsecutiveHealthSuccesses = 0;
        Status = ConsecutiveHealthFailures >= unhealthyAfterFailures
            ? NodeStatus.Unhealthy
            : NodeStatus.Degraded;
    }

    private bool CanUpdateHealth() => Enabled && Status != NodeStatus.Draining;

    private static string? NormalizeError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return null;
        }

        var value = error.Trim();
        return value.Length <= 1000 ? value : value[..1000];
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
        BaseAddress = InferenceEndpoint.NormalizeBaseAddress(baseAddress);
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
