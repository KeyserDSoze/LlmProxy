using System.Runtime.InteropServices;

namespace LlmProxy.NodeAgent;

public sealed class HardwareInventoryReader(NodeAgentOptions options, ProcessRunner runner)
{
    private readonly object _cpuLock = new();
    private (long Total, long Idle)? _lastCpuSample;
    public async Task<HardwareInventory> ReadAsync(CancellationToken cancellationToken)
    {
        var (memoryTotal, memoryAvailable) = ReadMemory();
        var drive = ResolveDrive(options.DataDirectory);
        var gpus = await ReadGpusAsync(cancellationToken);
        var cpuUtilization = ReadCpuUtilization();
        var docker = await TryDockerVersionAsync(cancellationToken);
        var issues = new List<string>();
        var dockerReady = docker is not null && await IsCommandAvailableAsync(options.DockerExecutable,
            ["info", "--format", "{{.ServerVersion}}"], cancellationToken);
        if (docker is null) issues.Add("Docker executable missing: the installer can provision Docker on supported Linux distributions.");
        else if (!dockerReady) issues.Add("Docker daemon is not available: start or repair docker.service.");
        var nvidiaHardware = gpus.Count > 0 || HasNvidiaPciDevice();
        var driverPresent = gpus.Count > 0 || Directory.Exists("/proc/driver/nvidia");
        if (nvidiaHardware && !driverPresent)
            issues.Add("NVIDIA GPU detected on PCI bus, but the kernel driver is missing or unloaded; schedule driver installation/reboot on the host.");
        var toolkitReady = !nvidiaHardware ||
            (await IsCommandAvailableAsync("nvidia-ctk", ["--version"], cancellationToken) &&
             await IsNvidiaDockerRuntimeConfiguredAsync(cancellationToken));
        if (driverPresent && gpus.Count == 0)
            issues.Add("NVIDIA driver was detected but nvidia-smi did not return usable accelerator data.");
        if (gpus.Any(gpu => gpu.MemoryType == "unknown"))
            issues.Add("GPU memory capacity is unknown; compatibility requires manual verification.");
        if (nvidiaHardware && !toolkitReady)
            issues.Add("NVIDIA Container Toolkit missing: the Linux host preparer can install it for supported distributions.");
        var readiness = new HostReadiness(docker is not null, dockerReady,
            driverPresent, toolkitReady, issues);

        return new HardwareInventory(
            Environment.MachineName,
            RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture.ToString(),
            Environment.ProcessorCount,
            ToGiB(memoryTotal),
            ToGiB(memoryAvailable),
            ToGiB(drive.TotalSize),
            ToGiB(drive.AvailableFreeSpace),
            gpus,
            "docker",
            docker,
            readiness,
            cpuUtilization);
    }

    private static (long TotalBytes, long AvailableBytes) ReadMemory()
    {
        const string path = "/proc/meminfo";
        if (File.Exists(path))
        {
            long totalKb = 0;
            long availableKb = 0;
            foreach (var line in File.ReadLines(path))
            {
                if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
                    totalKb = ParseMemInfoKb(line);
                else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                    availableKb = ParseMemInfoKb(line);
            }

            if (totalKb > 0)
                return (totalKb * 1024, Math.Max(0, availableKb) * 1024);
        }

        var info = GC.GetGCMemoryInfo();
        var total = Math.Max(0, info.TotalAvailableMemoryBytes);
        return (total, total);
    }

    private async Task<IReadOnlyList<GpuInventory>> ReadGpusAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await runner.RunAsync(
                "nvidia-smi",
                ["--query-gpu=name,memory.total,memory.free,driver_version,compute_cap", "--format=csv,noheader,nounits"],
                TimeSpan.FromSeconds(8),
                cancellationToken);
            // Older drivers may not expose compute_cap. Do not lose GPU discovery if
            // only that optional field is unsupported.
            if (!result.Success)
                result = await runner.RunAsync(
                    "nvidia-smi",
                    ["--query-gpu=name,memory.total,memory.free,driver_version", "--format=csv,noheader,nounits"],
                    TimeSpan.FromSeconds(8),
                    cancellationToken);
            IReadOnlyList<GpuInventory> parsed = result.Success ? ParseNvidiaSmiCsv(result.StandardOutput) : [];
            if (parsed.Count == 0)
            {
                // Query GPU discovery can fail on driver/version combinations.
                // Count devices using the driver-supported inventory output instead.
                var list = await runner.RunAsync("nvidia-smi", ["-L"], TimeSpan.FromSeconds(8), cancellationToken);
                if (list.Success) parsed = ParseNvidiaSmiList(list.StandardOutput);
            }
            return parsed.Count == 0 ? parsed : await AttachGpuTelemetryAsync(parsed, cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            return [];
        }
    }

    // NVIDIA GB10 (DGX Spark) uses shared CPU/GPU RAM. nvidia-smi reports [N/A]
    // for memory.total/free even when the accelerator and CUDA driver work.
    // Keep the detected GPU without claiming that it has dedicated VRAM.
    public static IReadOnlyList<GpuInventory> ParseNvidiaSmiCsv(string output)
    {
        var rows = new List<GpuInventory>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 3 || string.IsNullOrWhiteSpace(parts[0])) continue;

            var totalKnown = double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var totalMiB);
            var freeKnown = double.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var freeMiB);
            var isUnified = parts[0].Contains("GB10", StringComparison.OrdinalIgnoreCase);
            var memoryType = isUnified ? "unified" : totalKnown && freeKnown ? "dedicated" : "unknown";
            rows.Add(new GpuInventory(parts[0],
                memoryType == "dedicated" ? totalMiB / 1024d : 0,
                memoryType == "dedicated" ? freeMiB / 1024d : 0,
                parts.Length > 3 ? parts[3] : null,
                parts.Length > 4 ? parts[4] : null,
                memoryType));
        }
        return rows;
    }

    public static IReadOnlyList<GpuInventory> ParseNvidiaSmiList(string output)
    {
        var rows = new List<GpuInventory>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // nvidia-smi -L: GPU 0: NVIDIA GB10 (UUID: GPU-...)
            var match = System.Text.RegularExpressions.Regex.Match(line,
                @"^GPU\s+\d+:\s+(?<name>.+?)\s+\(UUID:", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var name = match.Groups["name"].Value.Trim();
                rows.Add(new GpuInventory(name, 0, 0, null, null,
                    name.Contains("GB10", StringComparison.OrdinalIgnoreCase) ? "unified" : "unknown"));
            }
        }
        return rows;
    }

    private async Task<IReadOnlyList<GpuInventory>> AttachGpuTelemetryAsync(
        IReadOnlyList<GpuInventory> rows, CancellationToken token)
    {
        try
        {
            var response = await runner.RunAsync("nvidia-smi",
                ["--query-gpu=utilization.gpu,temperature.gpu,power.draw", "--format=csv,noheader,nounits"],
                TimeSpan.FromSeconds(8), token);
            if (!response.Success) return rows;
            var lines = response.StandardOutput.Split('\n',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length != rows.Count) return rows;
            var result = new List<GpuInventory>(rows.Count);
            for (var index = 0; index < rows.Count; index++)
            {
                var parts = lines[index].Split(',', StringSplitOptions.TrimEntries);
                static double? Read(string? value) =>
                    double.TryParse(value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var measured)
                        && double.IsFinite(measured) && measured >= 0 ? measured : null;
                result.Add(rows[index] with
                {
                    UtilizationPercent = parts.Length > 0 ? Read(parts[0]) : null,
                    TemperatureCelsius = parts.Length > 1 ? Read(parts[1]) : null,
                    PowerWatts = parts.Length > 2 ? Read(parts[2]) : null
                });
            }
            return result;
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
        { return rows; }
    }

    private double? ReadCpuUtilization()
    {
        try
        {
            var line = File.ReadLines("/proc/stat").FirstOrDefault();
            if (line is null || !line.StartsWith("cpu ", StringComparison.Ordinal)) return null;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();
            if (parts.Length < 5) return null;
            var values = parts.Select(long.Parse).ToArray();
            var total = values.Sum();
            var idle = values[3] + values[4];
            lock (_cpuLock)
            {
                var previous = _lastCpuSample;
                _lastCpuSample = (total, idle);
                if (previous is null || total <= previous.Value.Total) return null;
                return Math.Clamp(100d * (1d - (idle - previous.Value.Idle) /
                    (double)(total - previous.Value.Total)), 0, 100);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or OverflowException)
        { return null; }
    }

    private static bool HasNvidiaPciDevice()
    {
        const string root = "/sys/bus/pci/devices";
        if (!Directory.Exists(root)) return false;
        try
        {
            return Directory.EnumerateDirectories(root).Any(path =>
                File.Exists(Path.Combine(path, "vendor")) &&
                string.Equals(File.ReadAllText(Path.Combine(path, "vendor")).Trim(), "0x10de",
                    StringComparison.OrdinalIgnoreCase));
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private async Task<bool> IsNvidiaDockerRuntimeConfiguredAsync(CancellationToken token)
    {
        try
        {
            var result = await runner.RunAsync(options.DockerExecutable,
                ["info", "--format", "{{json .Runtimes}}"], TimeSpan.FromSeconds(8), token);
            return result.Success && result.StandardOutput.Contains("nvidia", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is InvalidOperationException or
            System.ComponentModel.Win32Exception or TimeoutException) { return false; }
    }

    private async Task<bool> IsCommandAvailableAsync(string executable, string[] args, CancellationToken token)
    {
        try
        {
            var result = await runner.RunAsync(executable, args, TimeSpan.FromSeconds(8), token);
            return result.Success;
        }
        catch (Exception e) when (e is InvalidOperationException or
            System.ComponentModel.Win32Exception or TimeoutException) { return false; }
    }

    private async Task<string?> TryDockerVersionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await runner.RunAsync(options.DockerExecutable, ["--version"], TimeSpan.FromSeconds(8), cancellationToken);
            return result.Success ? result.StandardOutput.Trim() : null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            return null;
        }
    }

    private static long ParseMemInfoKb(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && long.TryParse(parts[1], out var value) ? value : 0;
    }

    private static DriveInfo ResolveDrive(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        return new DriveInfo(string.IsNullOrWhiteSpace(root) ? "/" : root);
    }

    private static double ToGiB(long bytes) => bytes / 1024d / 1024d / 1024d;
}
