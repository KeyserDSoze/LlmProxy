using System.Globalization;

namespace LlmProxy.NodeAgent;

public sealed record GpuThermalStatus(DateTimeOffset CheckedAtUtc, bool SensorAvailable,
    double StartupLimitCelsius, double? MaximumObservedCelsius,
    bool StartupBlocked, string Explanation);

/// <summary>
/// Best-effort GPU admission guard; not a replacement for platform firmware,
/// airflow, PSU protection, or CPU/board thermal monitoring.
/// </summary>
public sealed class GpuRuntimeSafety(NodeAgentOptions options, ProcessRunner runner)
{
    public static IReadOnlyList<double> ParseTemperatures(string output)
    {
        var result = new List<double>();
        foreach (var line in output.Split('\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(64))
        {
            if (!double.TryParse(line, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var temperature) ||
                !double.IsFinite(temperature) || temperature is < -20 or > 130)
                return [];
            result.Add(temperature);
        }
        return result;
    }

    public static GpuThermalStatus Evaluate(IReadOnlyList<double> temperatures,
        double limit, bool requireSensor, DateTimeOffset observedAt)
    {
        if (temperatures.Count == 0)
            return new(observedAt, false, limit, null, requireSensor,
                requireSensor
                ? "GPU temperature sensor unavailable; strict mode blocks model start."
                : "GPU temperature sensor unavailable; thermal safety cannot be verified.");
        var highest = temperatures.Max();
        var blocked = highest >= limit;
        return new(observedAt, true, limit, highest, blocked,
            blocked ? $"GPU temperature {highest:F1} C exceeds the configured start threshold {limit:F1} C."
                    : $"GPU temperature {highest:F1} C is below the start threshold {limit:F1} C.");
    }

    public async Task<GpuThermalStatus> ReadAsync(CancellationToken token)
    {
        IReadOnlyList<double> temperatures = [];
        try
        {
            var result = await runner.RunAsync("nvidia-smi",
                ["--query-gpu=temperature.gpu", "--format=csv,noheader,nounits"],
                TimeSpan.FromSeconds(8), token);
            if (result.Success) temperatures = ParseTemperatures(result.StandardOutput);
        }
        catch (Exception error) when (error is InvalidOperationException or TimeoutException or
            System.ComponentModel.Win32Exception) { }
        return Evaluate(temperatures, options.GpuStartMaxTemperatureCelsius,
            options.RequireGpuTemperatureSensor, DateTimeOffset.UtcNow);
    }

    public async Task EnsureAllowedAsync(CancellationToken token)
    {
        var status = await ReadAsync(token);
        if (status.StartupBlocked)
            throw new InvalidOperationException("GPU startup blocked: " + status.Explanation +
                " Check cooling and hardware health before retrying.");
    }
}
