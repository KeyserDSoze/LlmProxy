using System.Globalization;

namespace LlmProxy.Infrastructure.Telemetry;

public sealed record DcgmPrometheusMetrics(
    int GpuCount,
    double? AverageGpuUtilizationPercent,
    double? MaxGpuUtilizationPercent,
    double? FramebufferUsedMiB,
    double? FramebufferFreeMiB,
    double? FramebufferUsageRatio,
    double? MaxTemperatureCelsius,
    double? TotalPowerUsageWatts);

public static class DcgmPrometheusMetricsParser
{
    public static bool TryParse(string exposition, out DcgmPrometheusMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(exposition);

        var gpuIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var utilizationSamples = new List<double>();
        var framebufferUsed = 0d;
        var framebufferFree = 0d;
        var framebufferTotal = 0d;
        var temperatureSamples = new List<double>();
        var powerSamples = new List<double>();
        var hasFramebufferUsed = false;
        var hasFramebufferFree = false;
        var hasFramebufferTotal = false;
        var recognized = 0;

        foreach (var rawLine in exposition.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
                !double.IsFinite(value))
            {
                continue;
            }

            var metricWithLabels = parts[0];
            var labelStart = metricWithLabels.IndexOf('{');
            var metricName = labelStart >= 0 ? metricWithLabels[..labelStart] : metricWithLabels;

            switch (metricName)
            {
                case "DCGM_FI_DEV_GPU_UTIL":
                    utilizationSamples.Add(Math.Clamp(value, 0d, 100d));
                    RegisterGpu(metricWithLabels, gpuIds);
                    recognized++;
                    break;
                case "DCGM_FI_DEV_FB_USED":
                    framebufferUsed += Math.Max(0d, value);
                    hasFramebufferUsed = true;
                    RegisterGpu(metricWithLabels, gpuIds);
                    recognized++;
                    break;
                case "DCGM_FI_DEV_FB_FREE":
                    framebufferFree += Math.Max(0d, value);
                    hasFramebufferFree = true;
                    RegisterGpu(metricWithLabels, gpuIds);
                    recognized++;
                    break;
                case "DCGM_FI_DEV_FB_TOTAL":
                    framebufferTotal += Math.Max(0d, value);
                    hasFramebufferTotal = true;
                    RegisterGpu(metricWithLabels, gpuIds);
                    recognized++;
                    break;
                case "DCGM_FI_DEV_GPU_TEMP":
                    temperatureSamples.Add(value);
                    RegisterGpu(metricWithLabels, gpuIds);
                    recognized++;
                    break;
                case "DCGM_FI_DEV_POWER_USAGE":
                    powerSamples.Add(Math.Max(0d, value));
                    RegisterGpu(metricWithLabels, gpuIds);
                    recognized++;
                    break;
            }
        }

        double? used = hasFramebufferUsed ? framebufferUsed : null;
        double? free = hasFramebufferFree ? framebufferFree : null;
        double? framebufferUsageRatio = null;
        if (used is double usedValue)
        {
            var total = hasFramebufferTotal
                ? framebufferTotal
                : hasFramebufferFree
                    ? usedValue + framebufferFree
                    : 0d;

            if (total > 0d)
            {
                framebufferUsageRatio = Math.Clamp(usedValue / total, 0d, 1d);
            }
        }

        metrics = new DcgmPrometheusMetrics(
            recognized == 0 ? 0 : Math.Max(1, gpuIds.Count),
            utilizationSamples.Count == 0 ? null : utilizationSamples.Average(),
            utilizationSamples.Count == 0 ? null : utilizationSamples.Max(),
            used,
            free,
            framebufferUsageRatio,
            temperatureSamples.Count == 0 ? null : temperatureSamples.Max(),
            powerSamples.Count == 0 ? null : powerSamples.Sum());

        return recognized > 0;
    }

    private static void RegisterGpu(string metricWithLabels, HashSet<string> gpuIds)
    {
        var gpu = TryReadLabel(metricWithLabels, "gpu")
            ?? TryReadLabel(metricWithLabels, "UUID");
        if (!string.IsNullOrWhiteSpace(gpu))
        {
            gpuIds.Add(gpu);
        }
    }

    private static string? TryReadLabel(string metricWithLabels, string labelName)
    {
        var marker = $"{labelName}=\"";
        var start = metricWithLabels.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        var end = metricWithLabels.IndexOf('"', start);
        return end > start ? metricWithLabels[start..end] : null;
    }
}
