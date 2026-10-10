using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LlmProxy.NodeAgent;

public sealed class DockerModelRuntimeManager(
    NodeAgentOptions options,
    ProcessRunner runner,
    ManagedModelRegistry registry,
    IHttpClientFactory httpClientFactory)
{
    private readonly SemaphoreSlim _operations = new(1, 1);

    private async Task<bool> HasGpuRuntimeAsync(CancellationToken token)
    {
        if (!options.UseNvidiaGpus) return false;
        try
        {
            var driver = await runner.RunAsync("nvidia-smi", ["-L"], TimeSpan.FromSeconds(5), token);
            if (!driver.Success || string.IsNullOrWhiteSpace(driver.StandardOutput)) return false;
            var docker = await runner.RunAsync(options.DockerExecutable,
                ["info", "--format", "{{json .Runtimes}}"], TimeSpan.FromSeconds(5), token);
            return docker.Success && docker.StandardOutput.Contains("nvidia", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is InvalidOperationException or
            System.ComponentModel.Win32Exception or TimeoutException) { return false; }
    }

    public async Task<ManagedModelsResponse> ListAsync(CancellationToken cancellationToken)
    {
        var rows = await registry.ReadAsync(cancellationToken);
        var states = new List<ManagedModelState>(rows.Count);
        foreach (var row in rows)
        {
            var running = await IsRunningAsync(row, cancellationToken);
            var status = running ? "running" : row.Status is "installing" or "error" ? row.Status : "stopped";
            states.Add(ToState(row, status, running ? RuntimeAddress(row.Port) : null));
        }
        return new ManagedModelsResponse(states);
    }

    public async Task<ManagedModelState> InstallAsync(InstallRequest request, CancellationToken cancellationToken, Action<TransferProgress>? report = null)
    {
        ManagedRuntimeProfiles.Validate(request);
        await _operations.WaitAsync(cancellationToken);
        try
        {
            var rows = await registry.ReadAsync(cancellationToken);
            var existing = ManagedRuntimeProfiles.FindMatchingInstallation(rows, request);
            if (existing is not null)
            {
                var running = await IsRunningAsync(existing, cancellationToken);
                return ToState(existing, running ? "running" : existing.Status,
                    running ? RuntimeAddress(existing.Port) : null);
            }

            // A different runtime profile receives a distinct installation and port.
            Directory.CreateDirectory(options.ModelCacheDirectory);
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
                report?.Invoke(new TransferProgress("container-image", null, "Fetching runtime image", null, null));
                if (record.Runtime == "airllm")
                {
                    var buildContext = Path.Combine(AppContext.BaseDirectory, "airllm-runtime");
                    if (!File.Exists(Path.Combine(buildContext, "Dockerfile")))
                        throw new InvalidOperationException("AirLLM assets are missing from the installed Agent release.");
                    report?.Invoke(new TransferProgress("container-image", null,
                        "Building AirLLM experimental serving image", null, null));
                    await PullCommandAsync(["build", "--pull", "-t",
                        ManagedRuntimeProfiles.Image(record, options), buildContext], cancellationToken,
                        line => ParseTransferLine(line, "container-image", report));
                }
                else await PullWithProgressAsync(ManagedRuntimeProfiles.Image(record, options), cancellationToken, report);
                if (options.PrefetchModels && record.Runtime != "llama.cpp")
                {
                    report?.Invoke(new TransferProgress("model-weights", null, "Reading model file metadata", null, null));
                    var script = """
import json
from huggingface_hub import HfApi, hf_hub_download
repo = MODEL_ID
siblings = HfApi().model_info(repo, files_metadata=True).siblings
files = [s for s in siblings if s.rfilename and not s.rfilename.lower().endswith(('.md', '.png', '.jpg', '.jpeg', '.gitattributes', '.onnx', '.h5', '.ot'))]
total = sum(s.size or 0 for s in files)
known = bool(files) and all(s.size is not None for s in files)
done = 0
for index, entry in enumerate(files):
    hf_hub_download(repo_id=repo, filename=entry.rfilename, cache_dir='/root/.cache/huggingface')
    done += entry.size or 0
    print('LLMPROXY_PROGRESS:' + json.dumps({'percent': round(100*done/total, 2) if known and total else round(100*(index+1)/len(files), 2), 'completedBytes': done if known else None, 'totalBytes': total if known else None, 'label': (entry.rfilename[:100] + (' (files)' if not known else ''))}), flush=True)
""".Replace("MODEL_ID", JsonSerializer.Serialize(record.ProviderModelName));
                    await PullCommandAsync(
                    [
                        "run", "--rm",
                        "--entrypoint", "python",
                        "-v", $"{Path.GetFullPath(options.ModelCacheDirectory)}:/root/.cache/huggingface",
                        record.Runtime == "airllm" ? ManagedRuntimeProfiles.Image(record, options) : options.DockerImage,
                        "-c", script
                    ], cancellationToken, line => ParseTransferLine(line, "model-weights", report));
                }

                report?.Invoke(new TransferProgress("ready", 100, "Runtime and model files prepared", null, null));
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
            var gpuReady = await HasGpuRuntimeAsync(cancellationToken);
            if (options.UseNvidiaGpus && !gpuReady && record.Runtime is "vllm" or "sglang" or "airllm")
                throw new InvalidOperationException(
                    "The NVIDIA GPU runtime is unavailable. Repair Docker/NVIDIA Toolkit from LLMProxy Admin before starting this model.");
            if (gpuReady)
            {
                args.Add("--gpus");
                args.Add("all");
            }
            args.Add("-v");
            args.Add($"{Path.GetFullPath(options.ModelCacheDirectory)}:{(record.Runtime == "llama.cpp" ? "/root/.cache/llama.cpp" : "/root/.cache/huggingface")}");
            if (record.Runtime == "airllm")
            {
                args.AddRange(["-e", "AIRLLM_MODEL_ID=" + record.ProviderModelName,
                    "-e", "AIRLLM_MAX_QUEUED=" + (record.MaxNumSeqs ?? 1),
                    "-e", "AIRLLM_MAX_CONTEXT=" + (record.MaxModelLen ?? 8192)]);
            }
            args.Add("-p");
            var containerPort = record.Runtime == "llama.cpp" ? 8080 : record.Runtime == "sglang" ? 30000 : 8000;
            var bindHost = options.ConnectionMode == "outbound" &&
                !string.IsNullOrWhiteSpace(options.GatewayBaseAddress) ? "127.0.0.1:" : "";
            args.Add($"{bindHost}{record.Port}:{containerPort}");
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

    private async Task PullWithProgressAsync(string image, CancellationToken token, Action<TransferProgress>? report) =>
        await PullCommandAsync(["pull", image], token, line => ParseTransferLine(line, "container-image", report));

    private async Task PullCommandAsync(IReadOnlyList<string> args, CancellationToken token, Action<string> progress)
    {
        var result = await runner.RunStreamingAsync(options.DockerExecutable, args,
            TimeSpan.FromMinutes(options.CommandTimeoutMinutes), token, progress);
        if (!result.Success)
            throw new InvalidOperationException($"Docker download failed ({result.ExitCode}): {Trim(result.StandardError)}");
    }

    // Percentages describe the current Docker layer or the measured Hugging Face file set.
    // Never invent an overall progress percentage when the upstream lacks total bytes.
    private static void ParseTransferLine(string line, string stage, Action<TransferProgress>? report)
    {
        if (report is null) return;
        if (line.StartsWith("LLMPROXY_PROGRESS:", StringComparison.Ordinal))
        {
            try
            {
                var data = JsonSerializer.Deserialize<ProgressSnapshot>(line["LLMPROXY_PROGRESS:".Length..],
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (data is not null)
                    report(new TransferProgress(stage, data.Percent, data.Label ?? "Downloading weights", data.CompletedBytes, data.TotalBytes));
            }
            catch (JsonException) {}
            return;
        }
        // Docker pull prints per-layer numbers, not a reliable total image percentage.
        // Provide visible per-layer progress, explicitly labelled as such.
        var numbers = Regex.Match(line, @"([0-9]+(?:\.[0-9]+)?)\s*(B|kB|MB|GB)\s*/\s*([0-9]+(?:\.[0-9]+)?)\s*(B|kB|MB|GB)", RegexOptions.IgnoreCase);
        if (numbers.Success)
        {
            static double Factor(string unit) => unit.ToUpperInvariant() switch {
                "GB" => 1000d*1000d*1000d, "MB" => 1000d*1000d, "KB" => 1000d, _ => 1d };
            var current = (long)(double.Parse(numbers.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) * Factor(numbers.Groups[2].Value));
            var total = (long)(double.Parse(numbers.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture) * Factor(numbers.Groups[4].Value));
            if (total > 0)
            {
                report(new TransferProgress(stage, Math.Round(Math.Min(100, current * 100d / total), 1),
                    "Docker layer " + line.Trim()[..Math.Min(20, line.Trim().Length)], current, total));
            }
        }
    }

    private sealed record ProgressSnapshot(double? Percent, long? CompletedBytes, long? TotalBytes, string? Label);

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
