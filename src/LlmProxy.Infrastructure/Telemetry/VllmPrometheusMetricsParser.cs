using System.Globalization;

namespace LlmProxy.Infrastructure.Telemetry;

public sealed record VllmPrometheusMetrics(
    string? ModelName,
    double RunningRequests,
    double WaitingRequests,
    double? KvCacheUsageRatio,
    double? PromptTokensTotal,
    double? GenerationTokensTotal);

public static class VllmPrometheusMetricsParser
{
    public static bool TryParse(string exposition, out VllmPrometheusMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(exposition);

        var running = 0d;
        var waiting = 0d;
        var promptTokens = 0d;
        var generationTokens = 0d;
        double? kvCache = null;
        string? modelName = null;
        var recognized = 0;
        var hasCurrentKvMetric = false;

        foreach (var rawLine in exposition.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                continue;
            }

            var metricWithLabels = parts[0];
            var labelStart = metricWithLabels.IndexOf('{');
            var metricName = labelStart >= 0 ? metricWithLabels[..labelStart] : metricWithLabels;
            modelName ??= TryReadLabel(metricWithLabels, "model_name");

            switch (metricName)
            {
                case "vllm:num_requests_running":
                    running += value;
                    recognized++;
                    break;
                case "vllm:num_requests_waiting":
                    waiting += value;
                    recognized++;
                    break;
                case "vllm:kv_cache_usage_perc":
                    kvCache = kvCache is null ? value : Math.Max(kvCache.Value, value);
                    hasCurrentKvMetric = true;
                    recognized++;
                    break;
                case "vllm:gpu_cache_usage_perc" when !hasCurrentKvMetric:
                    kvCache = kvCache is null ? value : Math.Max(kvCache.Value, value);
                    recognized++;
                    break;
                case "vllm:prompt_tokens_total":
                    promptTokens += value;
                    recognized++;
                    break;
                case "vllm:generation_tokens_total":
                    generationTokens += value;
                    recognized++;
                    break;
            }
        }

        metrics = new VllmPrometheusMetrics(
            modelName,
            Math.Max(0d, running),
            Math.Max(0d, waiting),
            kvCache is null ? null : Math.Max(0d, kvCache.Value),
            recognized == 0 ? null : Math.Max(0d, promptTokens),
            recognized == 0 ? null : Math.Max(0d, generationTokens));

        return recognized > 0;
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
