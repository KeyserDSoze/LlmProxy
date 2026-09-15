namespace LlmProxy.Infrastructure.Routing;

internal sealed class CapacityLeaseValidityTracker
{
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _maxUnrenewedDuration;
    private long _lastSuccessfulRenewalTimestamp;

    public CapacityLeaseValidityTracker(TimeProvider timeProvider, TimeSpan maxUnrenewedDuration)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (maxUnrenewedDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxUnrenewedDuration));
        }

        _timeProvider = timeProvider;
        _maxUnrenewedDuration = maxUnrenewedDuration;
        _lastSuccessfulRenewalTimestamp = _timeProvider.GetTimestamp();
    }

    public TimeSpan MaxUnrenewedDuration => _maxUnrenewedDuration;

    public void MarkRenewed()
        => Volatile.Write(ref _lastSuccessfulRenewalTimestamp, _timeProvider.GetTimestamp());

    public TimeSpan GetElapsedSinceLastRenewal()
        => _timeProvider.GetElapsedTime(Volatile.Read(ref _lastSuccessfulRenewalTimestamp));

    public bool HasCrossedSafetyDeadline()
        => GetElapsedSinceLastRenewal() >= _maxUnrenewedDuration;
}
