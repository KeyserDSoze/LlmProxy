using LlmProxy.Infrastructure.Routing;

namespace LlmProxy.UnitTests.Infrastructure;

public sealed class CapacityLeaseValidityTrackerTests
{
    [Fact]
    public void Safety_deadline_is_not_crossed_before_max_unrenewed_duration()
    {
        var timeProvider = new ManualTimeProvider();
        var tracker = new CapacityLeaseValidityTracker(timeProvider, TimeSpan.FromSeconds(16));

        timeProvider.Advance(TimeSpan.FromSeconds(15.999));

        Assert.False(tracker.HasCrossedSafetyDeadline());
    }

    [Fact]
    public void Safety_deadline_is_crossed_at_max_unrenewed_duration()
    {
        var timeProvider = new ManualTimeProvider();
        var tracker = new CapacityLeaseValidityTracker(timeProvider, TimeSpan.FromSeconds(16));

        timeProvider.Advance(TimeSpan.FromSeconds(16));

        Assert.True(tracker.HasCrossedSafetyDeadline());
    }

    [Fact]
    public void Successful_renewal_moves_the_safety_deadline_forward()
    {
        var timeProvider = new ManualTimeProvider();
        var tracker = new CapacityLeaseValidityTracker(timeProvider, TimeSpan.FromSeconds(16));

        timeProvider.Advance(TimeSpan.FromSeconds(12));
        tracker.MarkRenewed();
        timeProvider.Advance(TimeSpan.FromSeconds(10));

        Assert.False(tracker.HasCrossedSafetyDeadline());
        Assert.Equal(TimeSpan.FromSeconds(10), tracker.GetElapsedSinceLastRenewal());

        timeProvider.Advance(TimeSpan.FromSeconds(6));

        Assert.True(tracker.HasCrossedSafetyDeadline());
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Volatile.Read(ref _timestamp);

        public void Advance(TimeSpan duration)
            => Interlocked.Add(ref _timestamp, duration.Ticks);
    }
}
