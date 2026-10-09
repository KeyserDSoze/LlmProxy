using System.Net;
using System.Text.Json;

namespace LlmProxy.NodeAgent;

public sealed class DockerModelRuntimeManager(
    NodeAgentOptions options,
    ProcessRunner runner,
    ManagedModelRegistry registry,
    IHttpClientFactory httpClientFactory)
{
    private readonly SemaphoreSlim _operations = new(1, 1);

    public async Task<ManagedModelsResponse> ListAsync(CancellationToken cancellationToken)
    {
        var rows = await registry.ReadAsync(cancellationToken);
        var states = new List<ManagedModelState>(rows.Count);
        foreach (var row in rows)
        {
            var running = await IsRunningAsync(row, cancellationToken);
            var status = running ? "running" : row.Status == "installing" ? "installing" : "stopped";
            states.Add(ToState(row, status, running ? RuntimeAddress(row.Port) : null));
        }
        return new ManagedModelsResponse(states);
    }

    public async Task<ManagedModelState> InstallAsync(InstallRequest request, CancellationToken cancellationToken)
    {
        ManagedRuntimeProfiles.Validate(request);
        await _operations.WaitAsync(cancellationToken);
        try
        {
            var existing = (await registry.ReadAsync(cancellationToken)).FirstOrDefault(item =>
                string.Equals(item.CatalogModelId, request.CatalogModelId, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                if (!ManagedRuntimeProfiles.Matches(existing, request))
                    throw new InvalidOperationException("This catalog model is already installed with a different runtime profile. Stop/remove its deployment before changing the profile.");
                var running = await IsRunningAsync(existing, cancellationToken);
                return ToState(existing, running ? "running" : "stopped", running ? RuntimeAddress(existing.Port) : null);
            }

            Directory.CreateDirectory(options.ModelCacheDirectory);
            var rows = await registry.ReadAsync(cancellationToken);
            var port = request.Port ?? AllocatePort(rows);
            if (rows.Any(item => item.Port == port))
                throw new InvalidOperationException($"Port {port} is already assigned to another managed model.");

            var record = new ManagedModelRecord
            {
                InstallationId = BuildInstallationId(request.CatalogModelId),
                CatalogModelId = request.CatalogModelId.Trim(),
                ProviderModelName = request.ProviderModelName.Trim(),
                Runtime = request.Runtime,
                MaxNumSeqs = request.MaxNumSeqs,
                MaxModelLen = request.MaxModelLen,
                KvCacheDtype = request.KvCacheDtype,
                CpuOffloadGiB = request.CpuOffloadGiB,
                Port = port,
                TensorParallelSize = request.TensorParallelSize,
                ExtraArguments = request.ExtraArguments?.ToArray() ?? [],
                Status = "installing"
            };
            await registry.UpsertAsync(record, cancellationToken);

            try
            {
                await RequireDockerAsync(["pull", ManagedRuntimeProfiles.Image(record, options)], cancellationToken);
                if (options.PrefetchModels && record.Runtime == "vllm")
                {
                    var script = $"from huggingface_hub import snapshot_download; snapshot_download({JsonSerializer.Serialize(record.ProviderModelName)})";
                    await RequireDockerAsync(
                    [
                        "run", "--rm",
                        "--entrypoint", "python",
                        "-v", $"{Path.GetFullPath(options.ModelCacheDirectory)}:/root/.cache/huggingface",
                        options.DockerImage,
                        "-c", script
                    ], cancellationToken);
                }

                record.Status = "stopped";
                record.Error = null;
                await registry.UpsertAsync(record, cancellationToken);
                return ToState(record, "stopped", null);
            }
            catch (Exception exception)
            {
                record.Status = "error";
                record.Error = exception.Message;
                await registry.UpsertAsync(record, cancellationToken);
                throw;
            }
        }
        finally
        {
            _operations.Release();
        }
    }

    public async Task<ManagedModelState> StartAsync(string installationId, CancellationToken cancellationToken)
    {
        await _operations.WaitAsync(cancellationToken);
        try
        {
            var record = await RequireRecordAsync(installationId, cancellationToken);
            var name = ContainerName(record);
            await RunDockerIgnoringFailureAsync(["rm", "-f", name], cancellationToken);

            var args = new List<string> { "run", "-d", "--name", name, "--restart", "unless-stopped", "--ipc=host" };
            if (options.UseNvidiaGpus)
            {
                args.Add("--gpus");
                args.Add("all");
            }
            args.Add("-v");
            args.Add($"{Path.GetFullPath(options.ModelCacheDirectory)}:{(record.Runtime == "llama.cpp" ? "/root/.cache/llama.cpp" : "/root/.cache/huggingface")}");
            args.Add("-p");
            args.Add($"{record.Port}:{(record.Runtime == "llama.cpp" ? 8080 : 8000)}");
            args.Add(ManagedRuntimeProfiles.Image(record, options));
            args.AddRange(ManagedRuntimeProfiles.Arguments(record, options));

            try
            {
                await RequireDockerAsync(args, cancellationToken);
                await WaitForHealthyAsync(record.Port, cancellationToken);
                record.Status = "running";
                record.RuntimeBaseAddress = RuntimeAddress(record.Port);
                record.Error = null;
                await registry.UpsertAsync(record, cancellationToken);
                return ToState(record, "running", record.RuntimeBaseAddress);
            }
            catch (Exception exception)
            {
                record.Status = "error";
                record.Error = exception.Message;
                record.RuntimeBaseAddress = null;
                await registry.UpsertAsync(record, cancellationToken);
                throw;
            }
        }
        finally
        {
            _operations.Release();
        }
    }

    public async Task<ManagedModelState> StopAsync(string installationId, CancellationToken cancellationToken)
    {
        await _operations.WaitAsync(cancellationToken);
        try
        {
            var record = await RequireRecordAsync(installationId, cancellationToken);
            await RunDockerIgnoringFailureAsync(["stop", ContainerName(record)], cancellationToken);
            record.Status = "stopped";
            record.RuntimeBaseAddress = null;
            record.Error = null;
            await registry.UpsertAsync(record, cancellationToken);
            return ToState(record, "stopped", null);
        }
        finally
        {
            _operations.Release();
        }
    }

    public async Task RemoveAsync(string installationId, CancellationToken cancellationToken)
    {
        await _operations.WaitAsync(cancellationToken);
        try
        {
            var record = await RequireRecordAsync(installationId, cancellationToken);
            await RunDockerIgnoringFailureAsync(["rm", "-f", ContainerName(record)], cancellationToken);
            await registry.RemoveAsync(installationId, cancellationToken);
        }
        finally
        {
            _operations.Release();
        }
    }

    private async Task<ManagedModelRecord> RequireRecordAsync(string installationId, CancellationToken cancellationToken) =>
        await registry.FindAsync(installationId, cancellationToken)
        ?? throw new KeyNotFoundException($"Managed installation '{installationId}' was not found.");

    private async Task<bool> IsRunningAsync(ManagedModelRecord record, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(
            options.DockerExecutable,
            ["inspect", "-f", "{{.State.Running}}", ContainerName(record)],
            TimeSpan.FromSeconds(10),
            cancellationToken);
        return result.Success && string.Equals(result.StandardOutput.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }

    private async Task RequireDockerAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(
            options.DockerExecutable,
            arguments,
            TimeSpan.FromMinutes(options.CommandTimeoutMinutes),
            cancellationToken);
        if (!result.Success)
            throw new InvalidOperationException($"Docker command failed ({result.ExitCode}): {Trim(result.StandardError)}");
    }

    private async Task RunDockerIgnoringFailureAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            await runner.RunAsync(options.DockerExecutable, arguments, TimeSpan.FromMinutes(2), cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
        {
        }
    }

    private async Task WaitForHealthyAsync(int port, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(options.StartupTimeoutMinutes));
        var client = httpClientFactory.CreateClient("runtime-health");
        var url = $"http://127.0.0.1:{port}/health";

        while (true)
        {
            timeoutCts.Token.ThrowIfCancellationRequested();
            try
            {
                using var response = await client.GetAsync(url, timeoutCts.Token);
                if ((int)response.StatusCode is >= 200 and < 300) return;
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(TimeSpan.FromSeconds(2), timeoutCts.Token);
        }
    }

    private int AllocatePort(IReadOnlyList<ManagedModelRecord> rows)
    {
        for (var port = options.PortStart; port <= 65535; port++)
            if (rows.All(item => item.Port != port)) return port;
        throw new InvalidOperationException("No free managed-runtime port remains in the configured range.");
    }

    private string RuntimeAddress(int port) => $"http://{options.AdvertiseHost}:{port}";

    private static string ContainerName(ManagedModelRecord record) => "llmproxy-" + record.InstallationId;

    private static string BuildInstallationId(string catalogModelId)
    {
        var slug = new string(catalogModelId.Trim().ToLowerInvariant().Select(character =>
            char.IsLetterOrDigit(character) ? character : '-').ToArray()).Trim('-');
        if (slug.Length > 32) slug = slug[..32];
        return $"{slug}-{Guid.NewGuid():N}"[..Math.Min(slug.Length + 9, slug.Length + 33)];
    }

    private static ManagedModelState ToState(ManagedModelRecord record, string status, string? runtimeBaseAddress) =>
        new(record.InstallationId, record.CatalogModelId, record.ProviderModelName, status, runtimeBaseAddress, record.Port, record.Error,
            record.Runtime, record.MaxNumSeqs, record.MaxModelLen, record.KvCacheDtype, record.CpuOffloadGiB);

    private static string Trim(string value) => value.Length <= 1200 ? value.Trim() : value[..1200].Trim();

}
