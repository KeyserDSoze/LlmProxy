using LlmProxy.NodeAgent;

namespace LlmProxy.UnitTests.NodeAgent;

public sealed class GpuRuntimeSafetyTests
{
    [Fact]
    public void Warm_gpu_blocks_new_start_at_configured_threshold()
    {
        var hot = GpuRuntimeSafety.Evaluate([42, 82], 80, false, DateTimeOffset.UtcNow);
        Assert.True(hot.SensorAvailable);
        Assert.True(hot.StartupBlocked);
        Assert.Equal(82, hot.MaximumObservedCelsius);
        var cool = GpuRuntimeSafety.Evaluate([48], 80, false, DateTimeOffset.UtcNow);
        Assert.False(cool.StartupBlocked);
    }

    [Fact]
    public void Unavailable_temperature_is_not_construed_as_zero_degrees()
    {
        Assert.Empty(GpuRuntimeSafety.ParseTemperatures("N/A\n"));
        Assert.Empty(GpuRuntimeSafety.ParseTemperatures("35\nN/A\n"));
        var permissive = GpuRuntimeSafety.Evaluate([], 80, false, DateTimeOffset.UtcNow);
        Assert.False(permissive.SensorAvailable);
        Assert.Null(permissive.MaximumObservedCelsius);
        Assert.False(permissive.StartupBlocked);
        Assert.Contains("unavailable", permissive.Explanation);
        Assert.True(GpuRuntimeSafety.Evaluate([], 80, true, DateTimeOffset.UtcNow).StartupBlocked);
    }

    [Fact]
    public void Invalid_nvidia_values_are_not_silent_safety_approval()
    {
        Assert.Empty(GpuRuntimeSafety.ParseTemperatures("NaN"));
        Assert.Empty(GpuRuntimeSafety.ParseTemperatures("500"));
        Assert.Equal([51, 57], GpuRuntimeSafety.ParseTemperatures("51\n57\n"));
    }

    [Theory]
    [InlineData("vllm", true)]
    [InlineData("sglang", true)]
    [InlineData("airllm", true)]
    [InlineData("diffusers", true)]
    [InlineData("llama.cpp", true)]
    [InlineData("faster-whisper", false)]
    [InlineData("laya", false)]
    [InlineData("kokoro", false)]
    public void Managed_gpu_runtimes_have_no_implicit_restart(string runtime, bool protectedGpu)
    {
        Assert.Equal(protectedGpu, ManagedRuntimeRestartGuard.RequiresManualGpuRestart(runtime, true));
    }

    [Fact]
    public void Cpu_only_llama_cpp_does_not_require_gpu_restart_policy() =>
        Assert.False(ManagedRuntimeRestartGuard.RequiresManualGpuRestart("llama.cpp", false));
}
