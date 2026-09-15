namespace LlmProxy.Application.Abstractions;

public enum CapacityAdmissionFailure
{
    None = 0,
    CapacityExhausted = 1,
    CoordinationUnavailable = 2
}

public sealed record CapacityAdmissionResult(
    bool Acquired,
    CapacityAdmissionFailure Failure,
    IAsyncDisposable? Lease,
    string Provider,
    string? RejectionScope = null)
{
    public static CapacityAdmissionResult Success(IAsyncDisposable lease, string provider) =>
        new(true, CapacityAdmissionFailure.None, lease, provider);

    public static CapacityAdmissionResult Rejected(string provider, string? rejectionScope = null) =>
        new(false, CapacityAdmissionFailure.CapacityExhausted, null, provider, rejectionScope);

    public static CapacityAdmissionResult Unavailable(string provider) =>
        new(false, CapacityAdmissionFailure.CoordinationUnavailable, null, provider);
}

public interface IRequestCapacityGate
{
    ValueTask<CapacityAdmissionResult> TryAcquireAsync(
        Guid deploymentId,
        Guid nodeId,
        int deploymentMaxConcurrency,
        int nodeMaxConcurrency,
        CancellationToken cancellationToken = default);
}
