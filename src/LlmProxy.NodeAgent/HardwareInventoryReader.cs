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
            docker);
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
            if (!result.Success) return [];

            var rows = new List<GpuInventory>();
            foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = line.Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length < 3 ||
                    !double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var totalMiB) ||
                    !double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var freeMiB))
                    continue;

                rows.Add(new GpuInventory(
                    parts[0],
                    totalMiB / 1024d,
                    freeMiB / 1024d,
                    parts.Length > 3 ? parts[3] : null,
                    parts.Length > 4 ? parts[4] : null));
            }
            return rows;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            return [];
        }
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
