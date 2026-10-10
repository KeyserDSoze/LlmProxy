namespace LlmProxy.NodeAgent;

public sealed record GpuInventory(
    string Name,
    double MemoryTotalGiB,
    double MemoryFreeGiB,
    string? DriverVersion,
    string? ComputeCapability);

public sealed record HostReadiness(
    bool DockerInstalled,
    bool DockerDaemonReady,
    bool NvidiaDriverDetected,
    bool NvidiaToolkitReady,
    IReadOnlyList<string> Issues);

public sealed record HardwareInventory(
    string Hostname,
    string? OperatingSystem,
    string? Architecture,
    int CpuLogicalCores,
    double SystemMemoryTotalGiB,
    double SystemMemoryAvailableGiB,
    double DiskTotalGiB,
    double DiskAvailableGiB,
    IReadOnlyList<GpuInventory> Gpus,
    string? Runtime,
    string? RuntimeVersion,
    HostReadiness? Readiness = null);

public sealed record InstallRequest(
    string CatalogModelId,
    string ProviderModelName,
    int? Port,
    int TensorParallelSize,
    IReadOnlyList<string>? ExtraArguments,
    string Runtime = "vllm",
    int? MaxNumSeqs = null,
    int? MaxModelLen = null,
    string? KvCacheDtype = null,
    double? CpuOffloadGiB = null,
    string? PublicName = null,
    string? GpuDevices = null);

public sealed record ManagedModelRecord
{
    public required string InstallationId { get; init; }
    public required string CatalogModelId { get; init; }
    public required string ProviderModelName { get; init; }
    public string Runtime { get; init; } = "vllm";
    public int? MaxNumSeqs { get; init; }
    public int? MaxModelLen { get; init; }
    public string? KvCacheDtype { get; init; }
    public double? CpuOffloadGiB { get; init; }
    public string? GpuDevices { get; init; }
    public required int Port { get; init; }
    public required int TensorParallelSize { get; init; }
    public IReadOnlyList<string> ExtraArguments { get; init; } = [];
    public string Status { get; set; } = "stopped";
    public string? RuntimeBaseAddress { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset InstalledAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record ManagedModelState(
    string InstallationId,
    string CatalogModelId,
    string ProviderModelName,
    string Status,
    string? RuntimeBaseAddress,
    int Port,
    string? Error,
    string Runtime = "vllm",
    int? MaxNumSeqs = null,
    int? MaxModelLen = null,
    string? KvCacheDtype = null,
    double? CpuOffloadGiB = null,
    string? GpuDevices = null);

public sealed record ManagedModelsResponse(IReadOnlyList<ManagedModelState> Models);
