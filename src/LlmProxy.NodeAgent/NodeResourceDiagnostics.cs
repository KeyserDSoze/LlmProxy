using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LlmProxy.NodeAgent;

public sealed record ResourceContainer(string Name, string? InstallationId,
    string? ModelName, string? Runtime, double? DockerMemoryGiB);
public sealed record HostGpuProcess(int GpuIndex, int Pid, string ProcessName,
    string ProcessType, double? ReportedGpuMemoryGiB);
public sealed record RuntimeMemoryShortfall(string InstallationId, string ModelName,
    int GpuIndex, double FreeAtStartupGiB, double RequestedAtStartupGiB,
    double ShortfallAtStartupGiB);
public sealed record ResourceDiagnosticSnapshot(DateTimeOffset ObservedAtUtc,
    IReadOnlyList<ResourceContainer> Containers,
    IReadOnlyList<RuntimeMemoryShortfall> RecentVllmMemoryFailures,
    IReadOnlyList<HostGpuProcess> GpuProcesses,
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
    // Fallback for GB10 hosts where --query-compute-apps has incomplete
    // per-process memory while the classic nvidia-smi table shows it.
    private static readonly Regex SmiProcessRow = new(
        @"^\|\s*(?<gpu>\d+)\s+(?:N/A|\d+)\s+(?:N/A|\d+)\s+(?<pid>\d+)\s+(?<kind>[CG]+)\s+(?<name>.+?)\s+(?<mem>\d+(?:\.\d+)?)MiB\s*\|\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.CultureInvariant);
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

    /// <summary>Parse the structured NVIDIA query without executing arbitrary process commands.</summary>
    public static IReadOnlyList<HostGpuProcess> ParseComputeGpuProcesses(string output)
    {
        var result = new List<HostGpuProcess>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(128))
        {
            var parts = line.Split(',', 3, StringSplitOptions.TrimEntries);
            if (parts.Length != 3 || !int.TryParse(parts[0], out var pid) || pid < 1)
                continue;
            var name = Path.GetFileName(parts[1].Trim());
            if (string.IsNullOrWhiteSpace(name)) name = "unknown";
            var gib = double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var mib)
                && mib >= 0 && double.IsFinite(mib) ? mib / 1024d : (double?)null;
            result.Add(new HostGpuProcess(0, pid, name.Length > 120 ? name[..120] : name, "C", gib));
        }
        return result;
    }

    /// <summary>Fallback parser for NVIDIA's displayed GPU processes table.</summary>
    public static IReadOnlyList<HostGpuProcess> ParseDisplayedGpuProcesses(string output)
    {
        var result = new List<HostGpuProcess>();
        foreach (Match match in SmiProcessRow.Matches(output).Cast<Match>().Take(128))
        {
            if (!int.TryParse(match.Groups["gpu"].Value, out var index) ||
                !int.TryParse(match.Groups["pid"].Value, out var pid) ||
                !double.TryParse(match.Groups["mem"].Value, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var mib)) continue;
            var name = Path.GetFileName(match.Groups["name"].Value.Trim());
            result.Add(new HostGpuProcess(index, pid, name.Length > 120 ? name[..120] : name,
                match.Groups["kind"].Value, mib / 1024d));
        }
        return result;
    }

    private async Task<IReadOnlyList<HostGpuProcess>> ReadGpuProcessesAsync(
        List<string> warnings, CancellationToken token)
    {
        try
        {
            var query = await runner.RunAsync("nvidia-smi",
                ["--query-compute-apps=pid,process_name,used_gpu_memory",
                    "--format=csv,noheader,nounits"],
                TimeSpan.FromSeconds(7), token);
            var structured = query.Success ? ParseComputeGpuProcesses(query.StandardOutput) : [];
            // The classic table includes graphics processes and can report GB10
            // memory where the structured query says N/A. Never sum these readings
            // with RAM or Docker stats; they overlap.
            var display = await runner.RunAsync("nvidia-smi", [],
                TimeSpan.FromSeconds(7), token);
            var displayed = display.Success ? ParseDisplayedGpuProcesses(display.StandardOutput) : [];
            if (displayed.Count > 0)
            {
                // Prefer the display value when both represent the same PID.
                var byPid = structured.ToDictionary(process => process.Pid);
                foreach (var process in displayed)
                {
                    if (!byPid.TryGetValue(process.Pid, out var current) ||
                        current.ReportedGpuMemoryGiB is null)
                        byPid[process.Pid] = process;
                }
                return byPid.Values.OrderByDescending(process => process.ReportedGpuMemoryGiB ?? -1)
                    .ThenBy(process => process.Pid).Take(128).ToArray();
            }
            if (!query.Success && !display.Success)
                warnings.Add("NVIDIA process inventory unavailable; GPU process ownership is unknown.");
            else if (structured.Count == 0)
                warnings.Add("NVIDIA returned no GPU process rows; this does not prove the host uses zero GPU memory.");
            return structured;
        }
        catch (Exception error) when (error is InvalidOperationException or TimeoutException or
            System.ComponentModel.Win32Exception)
        {
            warnings.Add("NVIDIA GPU process inventory unavailable: " + error.GetType().Name);
            return [];
        }
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

        var gpuProcesses = await ReadGpuProcessesAsync(warnings, token);

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
        warnings.Add("GPU process memory (NVIDIA) overlaps RAM and Docker stats on unified-memory hosts. PID is diagnostic only, not proof of which model/manager owns the process. Other host RAM and file cache cannot be attributed.");
        return new ResourceDiagnosticSnapshot(DateTimeOffset.UtcNow, containers, failures, gpuProcesses, warnings);
    }
}
