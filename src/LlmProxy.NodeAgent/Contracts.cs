namespace LlmProxy.NodeAgent;

public sealed record GpuInventory(
    string Name,
    double MemoryTotalGiB,
    double MemoryFreeGiB,
    string? DriverVersion,
    string? ComputeCapability);

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
    string? RuntimeVersion);

public sealed record InstallRequest(
    string CatalogModelId,
    string ProviderModelName,
    int? Port,
    int TensorParallelSize,
    IReadOnlyList<string>? ExtraArguments);

public sealed record ManagedModelRecord
{
    public required string InstallationId { get; init; }
    public required string CatalogModelId { get; init; }
    public required string ProviderModelName { get; init; }
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
    string? Error);

public sealed record ManagedModelsResponse(IReadOnlyList<ManagedModelState> Models);
