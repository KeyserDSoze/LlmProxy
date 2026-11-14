namespace LlmProxy.NodeAgent;

public sealed record GpuInventory(
    string Name,
    double MemoryTotalGiB,
    double MemoryFreeGiB,
    string? DriverVersion,
    string? ComputeCapability,
    string MemoryType = "dedicated",
    double? UtilizationPercent = null,
    double? TemperatureCelsius = null,
    double? PowerWatts = null);

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
    HostReadiness? Readiness = null,
    double? CpuUtilizationPercent = null);

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
    string? GpuDevices = null,
    int? MaxOutputTokens = null,
    bool StartAfterInstall = false,
    double? GpuMemoryUtilization = null);

public sealed record ManagedModelRecord
{
    public required string InstallationId { get; init; }
    public required string CatalogModelId { get; init; }
    public required string ProviderModelName { get; init; }
    public string Runtime { get; init; } = "vllm";
    public int? MaxNumSeqs { get; init; }
    public int? MaxModelLen { get; init; }
    public int? MaxOutputTokens { get; init; }
    public string? SourceRevision { get; init; }
    // llama.cpp: repository-relative GGUF selected and downloaded at SourceRevision.
    public string? ModelFile { get; init; }
    public string? MmprojFile { get; init; }
    public string? KvCacheDtype { get; init; }
    public double? CpuOffloadGiB { get; init; }
    public double? GpuMemoryUtilization { get; init; }
    public string? GpuDevices { get; init; }
    public required int Port { get; init; }
    public required int TensorParallelSize { get; init; }
    public IReadOnlyList<string> ExtraArguments { get; init; } = [];
    /// <summary>
    /// What the operator wants: true after a verified start, false after a stop. A reboot or a crash leaves it true, which is how
    /// the Agent knows to start the model again. Null on records from older Agents: they count as wanted only if they were running.
    /// </summary>
    public bool? DesiredRunning { get; init; }
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
    string? GpuDevices = null,
    int? MaxOutputTokens = null,
    string? SourceRevision = null,
    double? GpuMemoryUtilization = null,
    string? ModelFile = null,
    FamilyProfileInfo? FamilyProfile = null,
    bool? DesiredRunning = null,
    /// <summary><c>waiting</c>, <c>starting</c>, <c>retrying</c> or <c>gave_up</c> while the Agent restarts a model that should be running.</summary>
    string? AutoStart = null,
    string? AutoStartDetail = null);

public sealed record ManagedModelsResponse(IReadOnlyList<ManagedModelState> Models);

public sealed record ManagedProfileUpdate(
    int MaxModelLen, int MaxNumSeqs, int? MaxOutputTokens,
    double? GpuMemoryUtilization = null);

public sealed record RuntimeEngineJobRequest(string Action);
