using LlmProxy.Api.Admin;
using LlmProxy.NodeAgent;

namespace LlmProxy.UnitTests.Nodes;

public sealed class UnifiedMemoryCompatibilityTests
{
    [Fact]
    public void Gb10_with_na_memory_is_kept_as_unified_gpu()
    {
        const string actualSparkOutput = "NVIDIA GB10, [N/A], [N/A], 580.178.04, 12.1\n";

        var gpu = Assert.Single(HardwareInventoryReader.ParseNvidiaSmiCsv(actualSparkOutput));

        Assert.Equal("NVIDIA GB10", gpu.Name);
        Assert.Equal("580.178.04", gpu.DriverVersion);
        Assert.Equal("unified", gpu.MemoryType);
        Assert.Equal(0, gpu.MemoryTotalGiB); // Not a second dedicated VRAM pool.
        Assert.Equal(0, gpu.MemoryFreeGiB);
    }

    [Fact]
    public void Gb10_without_optional_compute_cap_is_still_detected()
    {
        var gpu = Assert.Single(HardwareInventoryReader.ParseNvidiaSmiCsv(
            "NVIDIA GB10, [N/A], [N/A], 580.178.04"));

        Assert.Equal("unified", gpu.MemoryType);
        Assert.Null(gpu.ComputeCapability);
    }

    [Fact]
    public void Ordinary_gpu_still_uses_dedicated_vram()
    {
        var gpu = Assert.Single(HardwareInventoryReader.ParseNvidiaSmiCsv(
            "NVIDIA RTX 6000 Ada, 49140, 45056, 570.0, 8.9"));

        Assert.Equal("dedicated", gpu.MemoryType);
        Assert.Equal(49140d / 1024d, gpu.MemoryTotalGiB);
        Assert.Equal(44d, gpu.MemoryFreeGiB);
    }

    [Fact]
    public void Unrecognized_na_gpu_memory_is_not_mistaken_for_zero_vram()
    {
        var gpu = Assert.Single(HardwareInventoryReader.ParseNvidiaSmiCsv(
            "Unrecognized accelerator, [N/A], [N/A], 570.0"));

        Assert.Equal("unknown", gpu.MemoryType);
        var hardware = SparkHardware([new NodeModelManagementEndpoints.GpuInventory(
            gpu.Name, gpu.MemoryTotalGiB, gpu.MemoryFreeGiB, gpu.DriverVersion, gpu.ComputeCapability, gpu.MemoryType)]);
        var compatibility = NodeModelManagementEndpoints.EvaluateCompatibility(
            DeployableModelCatalog.Find("qwen3-4b")!, hardware);

        Assert.Equal("unknown", compatibility.Status);
    }

    [Fact]
    public void Gb10_airllm_can_fit_with_only_one_shared_memory_budget()
    {
        var compatibility = NodeModelManagementEndpoints.EvaluateCompatibility(
            DeployableModelCatalog.Find("qwen3-4b-airllm")!, SparkHardware());

        Assert.Equal("fits", compatibility.Status);
        Assert.Contains("Unified CPU/GPU RAM", compatibility.Summary);
    }

    [Fact]
    public void Gb10_large_model_does_not_fit_when_combined_budget_is_exceeded()
    {
        var compatibility = NodeModelManagementEndpoints.EvaluateCompatibility(
            DeployableModelCatalog.Find("qwen3-32b")!, SparkHardware());

        Assert.Equal("insufficient", compatibility.Status);
        Assert.Contains(compatibility.Reasons, reason => reason.Contains("combined CPU/GPU unified memory"));
    }

    [Fact]
    public void Gb10_unified_memory_needs_extra_host_headroom()
    {
        var compatibility = NodeModelManagementEndpoints.EvaluateCompatibility(
            DeployableModelCatalog.Find("qwen3-4b-airllm")!,
            SparkHardware(availableRam: 25));

        Assert.Equal("insufficient", compatibility.Status);
        Assert.Contains(compatibility.Reasons, reason => reason.Contains("reserving"));
    }

    [Fact]
    public void Gb10_cannot_satisfy_two_gpu_requirement()
    {
        var model = DeployableModelCatalog.Find("qwen3-4b-airllm")! with { MinimumGpuCount = 2 };
        var compatibility = NodeModelManagementEndpoints.EvaluateCompatibility(model, SparkHardware());

        Assert.Equal("insufficient", compatibility.Status);
        Assert.Contains(compatibility.Reasons, reason => reason.Contains("2 GPU(s)"));
    }

    private static NodeModelManagementEndpoints.HardwareInventory SparkHardware(
        IReadOnlyList<NodeModelManagementEndpoints.GpuInventory>? gpus = null,
        double availableRam = 55.16349792480469) =>
        new(
            "spark-1d52", "Ubuntu 24.04.5 LTS", "Arm64", 20,
            121.62524795532227, availableRam, 1876.217758178711,
            1677.0659942626953,
            gpus ?? [new NodeModelManagementEndpoints.GpuInventory(
                "NVIDIA GB10", 0, 0, "580.178.04", null, "unified")],
            "docker", "Docker version 29.6.2", null);
}
