namespace LlmProxy.Benchmarking;

/// <summary>
/// Converts experimental measurements into advisory capacity evidence.
/// Intentionally never changes live node/deployment limits.
/// </summary>
public static class BenchmarkCapacityEvaluator
{
    public static BenchmarkCapacityRecommendation? Evaluate(BenchmarkReport report, BenchmarkOptions options)
    {
        if (options.MaxP95TtftMilliseconds is null && options.MinOutputTokensPerSecondPerSlot is null)
            return null;

        var decisions = new List<BenchmarkCapacityDecision>();
        int? candidate = null;
        var contiguous = true;
        foreach (var level in report.Levels.OrderBy(item => item.Concurrency))
        {
            var reasons = new List<string>();
            var minimumSamples = Math.Max(20, level.Concurrency * 2);
            if (level.Attempted < minimumSamples)
                reasons.Add($"needs >= {minimumSamples} requests for this concurrency; got {level.Attempted}");
            if (level.SuccessRatePercent < options.MinSuccessRatePercent)
                reasons.Add($"success {level.SuccessRatePercent:0.##}% below {options.MinSuccessRatePercent:0.##}%");
            if (options.MaxP95TtftMilliseconds is double ttftMax)
            {
                if (level.P95TtftMilliseconds is null)
                    reasons.Add("TTFT unavailable; cannot prove latency SLO");
                else if (level.P95TtftMilliseconds > ttftMax)
                    reasons.Add($"p95 TTFT {level.P95TtftMilliseconds:0}ms exceeds {ttftMax:0}ms");
            }
            if (options.MinOutputTokensPerSecondPerSlot is double minTps)
            {
                var averagePerSlot = level.OutputTokensPerSecond / level.Concurrency;
                if (averagePerSlot is null)
                    reasons.Add("output token usage unavailable; cannot prove per-slot throughput proxy");
                else if (averagePerSlot.Value < minTps)
                    reasons.Add($"aggregate output/slot {averagePerSlot:0.##} below {minTps:0.##} token/s");
            }

            var pass = reasons.Count == 0;
            decisions.Add(new BenchmarkCapacityDecision(level.Concurrency, pass, pass ? "Measured SLO passed (one run only)" : string.Join("; ", reasons)));
            if (!pass) contiguous = false;
            if (pass && contiguous) candidate = level.Concurrency;
        }

        var summary = candidate is null
            ? "No tested concurrency met every selected SLO. No safe capacity recommendation can be made."
            : $"Largest consecutively passing tested level: {candidate}. PROVISIONAL ONLY: repeat direct/gateway tests and compare context profiles before applying this value in Admin.";

        return new BenchmarkCapacityRecommendation(candidate, options.MinSuccessRatePercent,
            options.MaxP95TtftMilliseconds, options.MinOutputTokensPerSecondPerSlot, decisions, summary);
    }
}
