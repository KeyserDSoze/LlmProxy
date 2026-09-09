using LlmProxy.Domain.Routing;

namespace LlmProxy.Application.Routing;

public sealed class RoutingTuningState
{
    private RoutingTuningSettings _current;

    public RoutingTuningState(RoutingTuningSettings initialSettings)
    {
        ArgumentNullException.ThrowIfNull(initialSettings);
        initialSettings.Validate();
        _current = initialSettings;
    }

    public RoutingTuningSettings Current => Volatile.Read(ref _current);

    public void Set(RoutingTuningSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        Volatile.Write(ref _current, settings);
    }
}
