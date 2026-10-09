using System.Globalization;

namespace LlmProxy.Infrastructure.Telemetry;

public sealed record MultiRuntimeMetricValues(
    string Runtime, string? ModelName, double Running, double Waiting,
    double? CacheUsage, double? PromptTokens, double? GenerationTokens);

public static class MultiRuntimePrometheusMetricsParser
{
    public static bool TryParse(string exposition, out MultiRuntimeMetricValues metrics)
    {
        ArgumentNullException.ThrowIfNull(exposition);
        if (VllmPrometheusMetricsParser.TryParse(exposition, out var vllm))
        {
            metrics = new("vllm", vllm.ModelName, vllm.RunningRequests, vllm.WaitingRequests,
                vllm.KvCacheUsageRatio, vllm.PromptTokensTotal, vllm.GenerationTokensTotal);
            return true;
        }

        string? runtime = null, model = null;
        double running = 0, waiting = 0, prompt = 0, generated = 0;
        double? cache = null;
        bool promptSeen = false, genSeen = false;
        var recognized = 0;
        foreach (var raw in exposition.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2 || !double.TryParse(tokens[1], NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value) || value < 0)
                continue;
            var rawName = tokens[0];
            var metric = rawName.Split('{')[0];
            var candidate = metric.StartsWith("sglang:", StringComparison.Ordinal) ? "sglang" :
                metric.StartsWith("llamacpp:", StringComparison.Ordinal) ? "llama.cpp" : null;
            if (candidate is null || (runtime is not null && runtime != candidate)) continue;
            var used = true;
            switch (metric)
            {
                case "sglang:num_running_reqs":
                case "llamacpp:requests_processing":
                    running += value; break;
                case "sglang:num_queue_reqs":
                case "llamacpp:requests_deferred":
                    waiting += value; break;
                case "sglang:token_usage":
                case "llamacpp:kv_cache_usage_ratio":
                    cache = cache is null ? value : Math.Max(cache.Value, value); break;
                case "llamacpp:prompt_tokens_total":
                    prompt += value; promptSeen = true; break;
                case "llamacpp:tokens_predicted_total":
                    generated += value; genSeen = true; break;
                default: used = false; break;
            }
            if (!used) continue;
            runtime ??= candidate;
            model ??= ReadModelName(rawName);
            recognized++;
        }

        metrics = new(runtime ?? "unknown", model, running, waiting,
            cache is null ? null : Math.Clamp(cache.Value, 0, 1),
            promptSeen ? prompt : null, genSeen ? generated : null);
        return recognized > 0;
    }

    private static string? ReadModelName(string metricWithLabels)
    {
        const string label = "model_name=\"";
        var start = metricWithLabels.IndexOf(label, StringComparison.Ordinal);
        if (start < 0) return null;
        start += label.Length;
        var end = metricWithLabels.IndexOf('"', start);
        return end > start ? metricWithLabels[start..end] : null;
    }
}
