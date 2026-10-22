using System.Text.RegularExpressions;

namespace LlmProxy.NodeAgent;

/// <summary>
/// Upgrade preexisting managed GPU containers in place to remove the older
/// unlimited Docker restart policy. This never stops or removes containers.
/// </summary>
public sealed class ManagedRuntimeRestartGuard(
    NodeAgentOptions options, ProcessRunner runner, ManagedModelRegistry registry,
    ILogger<ManagedRuntimeRestartGuard> logger) : BackgroundService
{
    public static bool RequiresManualGpuRestart(string runtime, bool useNvidiaGpu) =>
        runtime is "vllm" or "sglang" or "airllm" or "diffusers" ||
        runtime == "llama.cpp" && useNvidiaGpu;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var record in await registry.ReadAsync(stoppingToken))
                {
                    if (!RequiresManualGpuRestart(record.Runtime, options.UseNvidiaGpus) ||
                        !Regex.IsMatch(record.InstallationId, "^[a-z0-9-]{1,64}$",
                            RegexOptions.CultureInvariant)) continue;
                    var name = "llmproxy-" + record.InstallationId;
                    var before = await runner.RunAsync(options.DockerExecutable,
                        ["inspect", "--format", "{{.HostConfig.RestartPolicy.Name}}", name],
                        TimeSpan.FromSeconds(10), stoppingToken);
                    if (!before.Success || before.StandardOutput.Trim() is "" or "no") continue;
                    var updated = await runner.RunAsync(options.DockerExecutable,
                        ["update", "--restart=no", name], TimeSpan.FromSeconds(15), stoppingToken);
                    if (updated.Success)
                        logger.LogWarning("Disabled unlimited restarts on legacy managed GPU container {Container}.", name);
                    else logger.LogWarning("Failed to disable restarts on {Container}.", name);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning(error, "GPU restart policy reconciliation failed."); }
            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
