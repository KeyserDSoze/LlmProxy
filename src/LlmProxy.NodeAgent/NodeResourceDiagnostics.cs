using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LlmProxy.NodeAgent;

public sealed record ResourceContainer(string Name, string? InstallationId,
    string? ModelName, string? Runtime, double? DockerMemoryGiB);
public sealed record RuntimeMemoryShortfall(string InstallationId, string ModelName,
    int GpuIndex, double FreeAtStartupGiB, double RequestedAtStartupGiB,
    double ShortfallAtStartupGiB);
public sealed record ResourceDiagnosticSnapshot(DateTimeOffset ObservedAtUtc,
    IReadOnlyList<ResourceContainer> Containers,
    IReadOnlyList<RuntimeMemoryShortfall> RecentVllmMemoryFailures,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Read-only, bounded host snapshot. Docker cgroup RAM is not equivalent to
/// GPU allocation (particularly with GB10 unified memory). Never kill or
/// reconfigure a process based on these estimates.
/// </summary>
public sealed class NodeResourceDiagnostics(NodeAgentOptions options, ProcessRunner runner,
    ManagedModelRegistry registry)
{
    private static readonly Regex MemoryValue = new(
        @"(?<amount>\d+(?:\.\d+)?)\s*(?<unit>TiB|GiB|MiB|KiB|B)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VllmShortfall = new(
        @"Free memory on device cuda:(?<gpu>\d+) \((?<free>[\d.]+)/(?<total>[\d.]+) GiB\) on startup is less than desired GPU memory utilization \((?<fraction>[\d.]+), (?<requested>[\d.]+) GiB\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static double? ParseDockerMemoryGiB(string value)
    {
        var match = MemoryValue.Match(value);
        if (!match.Success || !double.TryParse(match.Groups["amount"].Value,
                NumberStyles.Float, CultureInfo.InvariantCulture, out var amount)) return null;
        return match.Groups["unit"].Value.ToLowerInvariant() switch
        {
            "tib" => amount * 1024,
            "gib" => amount,
            "mib" => amount / 1024,
            "kib" => amount / (1024 * 1024),
            "b" => amount / (1024d * 1024 * 1024),
            _ => null
        };
    }

    public static RuntimeMemoryShortfall? ParseRecentVllmShortfall(
        string log, string installationId, string modelName)
    {
        var match = VllmShortfall.Matches(log).Cast<Match>().LastOrDefault();
        if (match is null ||
            !int.TryParse(match.Groups["gpu"].Value, out var gpu) ||
            !double.TryParse(match.Groups["free"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var free) ||
            !double.TryParse(match.Groups["requested"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var requested))
            return null;
        return new RuntimeMemoryShortfall(installationId, modelName, gpu, free,
            requested, Math.Round(Math.Max(0, requested - free), 2));
    }

    public async Task<ResourceDiagnosticSnapshot> ReadAsync(CancellationToken token)
    {
        var records = await registry.ReadAsync(token);
        var byName = records.ToDictionary(x => "llmproxy-" + x.InstallationId,
            StringComparer.Ordinal);
        var containers = new List<ResourceContainer>();
        var failures = new List<RuntimeMemoryShortfall>();
        var warnings = new List<string>();
        try
        {
            var result = await runner.RunAsync(options.DockerExecutable,
                ["stats", "--no-stream", "--format", "{{json .}}"],
                TimeSpan.FromSeconds(15), token);
            if (!result.Success)
                warnings.Add("Docker stats unavailable; process memory attribution is incomplete.");
            else
            {
                foreach (var line in result.StandardOutput.Split('\n',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(100))
                {
                    try
                    {
                        using var data = JsonDocument.Parse(line);
                        var name = data.RootElement.TryGetProperty("Name", out var key)
                            ? key.GetString() : null;
                        if (string.IsNullOrEmpty(name)) continue;
                        var memory = data.RootElement.TryGetProperty("MemUsage", out var mem)
                            ? ParseDockerMemoryGiB(mem.GetString() ?? "") : null;
                        byName.TryGetValue(name, out var managed);
                        containers.Add(new ResourceContainer(name,
                            managed?.InstallationId, managed?.ProviderModelName, managed?.Runtime,
                            memory));
                    }
                    catch (JsonException) { /* One bad stats row is not a fabricated zero. */ }
                }
            }
        }
        catch (Exception error) when (error is InvalidOperationException or TimeoutException or
            System.ComponentModel.Win32Exception)
        {
            warnings.Add("Docker stats unavailable: " + error.GetType().Name);
        }

        // Recent history only: the free/required values are from an earlier
        // failed attempt, not a live reservation or promise of fit.
        foreach (var record in records.Where(x => x.Runtime == "vllm").Take(8))
        {
            try
            {
                var log = await runner.RunAsync(options.DockerExecutable,
                    ["logs", "--since", "30m", "--tail", "120", "llmproxy-" + record.InstallationId],
                    TimeSpan.FromSeconds(6), token);
                if (!log.Success) continue;
                var issue = ParseRecentVllmShortfall(log.StandardError + "\n" + log.StandardOutput,
                    record.InstallationId, record.ProviderModelName);
                if (issue is not null) failures.Add(issue);
            }
            catch (Exception error) when (error is InvalidOperationException or TimeoutException or
                System.ComponentModel.Win32Exception) { /* Docker logs optional. */ }
        }
        warnings.Add("Docker stats estimates cgroup RAM, not per-process GPU memory; on DGX Spark CPU/GPU memory is shared. Other host processes and file cache are not individually attributed.");
        return new ResourceDiagnosticSnapshot(DateTimeOffset.UtcNow, containers, failures, warnings);
    }
}
