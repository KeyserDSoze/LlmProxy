namespace LlmProxy.NodeAgent;

/// <summary>Runs only the release-installed fixed package preflight, never admin-supplied commands.</summary>
public sealed class NodeHostPreparer(ProcessRunner runner, HardwareInventoryReader inventory)
{
    private readonly SemaphoreSlim _exclusive = new(1, 1);
    public async Task<PrepareResult> RunAsync(CancellationToken token)
    {
        if (!await _exclusive.WaitAsync(0, token))
            return new PrepareResult(false, "already_running");
        try
        {
            const string executable = "/opt/llmproxy-node-agent/prepare-node-host.sh";
            if (!OperatingSystem.IsLinux() || !File.Exists(executable))
                return new PrepareResult(false, "host_preparer_unavailable");
            var result = await runner.RunAsync("/bin/bash", [executable],
                TimeSpan.FromMinutes(20), token);
            var updated = await inventory.ReadAsync(token);
            return new PrepareResult(result.Success && updated.Readiness?.DockerDaemonReady == true,
                result.Success ? (updated.Readiness?.DockerDaemonReady == true ? "ready" : "docker_unavailable")
                    : "host_preparation_failed");
        }
        catch (TimeoutException) { return new PrepareResult(false, "host_preparation_timeout"); }
        finally { _exclusive.Release(); }
    }
}
public sealed record PrepareResult(bool Success, string Code);
