using System.Runtime.InteropServices;

namespace LlmProxy.NodeAgent;

public sealed class HardwareInventoryReader(NodeAgentOptions options, ProcessRunner runner)
{
    public async Task<HardwareInventory> ReadAsync(CancellationToken cancellationToken)
    {
        var (memoryTotal, memoryAvailable) = ReadMemory();
        var drive = ResolveDrive(options.DataDirectory);
        var gpus = await ReadGpusAsync(cancellationToken);
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
            readiness);
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
            return result.Success ? ParseNvidiaSmiCsv(result.StandardOutput) : [];
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
