using System.Net;
using System.Net.Http.Json;
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
                if (running || existing.Status == "stopped")
                    return ToState(existing, running ? "running" : existing.Status,
                        running ? RuntimeAddress(existing.Port) : null);
                // A failed/cancelled/reboot-interrupted install must be retryable.
                // Keep the downloaded cache, but rebuild the incomplete installation.
                await registry.RemoveAsync(existing.InstallationId, cancellationToken);
                rows = await registry.ReadAsync(cancellationToken);
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
                MaxOutputTokens = request.MaxOutputTokens,
                KvCacheDtype = request.KvCacheDtype,
                CpuOffloadGiB = request.CpuOffloadGiB,
                GpuDevices = request.GpuDevices,
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
                    await EnsureAirLlmImageAsync(record, cancellationToken, report);
                else if (record.Runtime == "faster-whisper")
                    await EnsureWhisperImageAsync(record, cancellationToken, report);
                else if (record.Runtime is "laya" or "kokoro" or "diffusers")
                    await EnsureMediaImageAsync(record, cancellationToken, report);
                else await PullWithProgressAsync(ManagedRuntimeProfiles.Image(record, options), cancellationToken, report);
                if ((options.PrefetchModels || record.Runtime is "faster-whisper" or "laya" or "kokoro" or "diffusers") && record.Runtime != "llama.cpp")
                {
                    report?.Invoke(new TransferProgress("model-weights", null, "Reading model file metadata", null, null));
                    var script = """
import json
from huggingface_hub import HfApi, hf_hub_download
from fnmatch import fnmatch
repo = MODEL_ID
info = HfApi().model_info(repo, files_metadata=True)
revision = info.sha
print('LLMPROXY_SOURCE_SHA:' + revision, flush=True)
siblings = info.siblings
if repo == 'segmind/SSD-1B':
    # Never download pickle checkpoints; restrict to needed Diffusers components.
    allow = ('model_index.json', 'scheduler/*.json', 'tokenizer/*',
             'tokenizer_2/*', 'text_encoder/*.json', 'text_encoder/*.safetensors',
             'text_encoder_2/*.json', 'text_encoder_2/*.safetensors',
             'unet/*.json', 'unet/*.safetensors', 'vae/*.json', 'vae/*.safetensors',
             'feature_extractor/*.json')
    files = [s for s in siblings if s.rfilename and
             s.rfilename.lower().endswith(('.json', '.safetensors', '.txt', '.model', '.vocab', '.merges')) and
             any(fnmatch(s.rfilename, pattern) for pattern in allow)]
    if not any(s.rfilename == 'model_index.json' for s in files) or not any(s.rfilename.startswith('unet/') and s.rfilename.endswith('.safetensors') for s in files):
        raise RuntimeError('Pinned Diffusers model lacks required safe components')
else:
    files = [s for s in siblings if s.rfilename and not s.rfilename.lower().endswith(('.md', '.png', '.jpg', '.jpeg', '.gitattributes', '.onnx', '.h5', '.ot'))]
total = sum(s.size or 0 for s in files)
known = bool(files) and all(s.size is not None for s in files)
done = 0
for index, entry in enumerate(files):
    hf_hub_download(repo_id=repo, filename=entry.rfilename, revision=revision, cache_dir='/root/.cache/huggingface/hub')
    done += entry.size or 0
    print('LLMPROXY_PROGRESS:' + json.dumps({'percent': round(100*done/total, 2) if known and total else round(100*(index+1)/len(files), 2), 'completedBytes': done if known else None, 'totalBytes': total if known else None, 'label': (entry.rfilename[:100] + (' (files)' if not known else ''))}), flush=True)
""".Replace("MODEL_ID", JsonSerializer.Serialize(record.ProviderModelName));
                    string? cachedSourceRevision = null;
                    await PullCommandAsync(
                    [
                        "run", "--rm",
                        "--entrypoint", "python",
                        ..(record.Runtime is "laya" or "diffusers" ? new[] { "-e", "HF_HUB_OFFLINE=0" } : Array.Empty<string>()),
                        "-v", $"{Path.GetFullPath(options.ModelCacheDirectory)}:/root/.cache/huggingface",
                        record.Runtime is "airllm" or "faster-whisper" or "laya" or "kokoro" or "diffusers"
                            ? ManagedRuntimeProfiles.Image(record, options) : options.DockerImage,
                        "-c", script
                    ], cancellationToken, line =>
                    {
                        if (line.StartsWith("LLMPROXY_SOURCE_SHA:", StringComparison.Ordinal))
                        {
                            var revision = line["LLMPROXY_SOURCE_SHA:".Length..].Trim();
                            if (Regex.IsMatch(revision, "^[a-fA-F0-9]{40}$")) cachedSourceRevision = revision.ToLowerInvariant();
                        }
                        ParseTransferLine(line, "model-weights", report);
                    });
                    if (cachedSourceRevision is not null)
                        record = record with { SourceRevision = cachedSourceRevision };
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

    private async Task EnsureAirLlmImageAsync(ManagedModelRecord record,
        CancellationToken token, Action<TransferProgress>? report = null)
    {
        // A changed server.py must not use the previous AirLLM serving image.
        // Image version is bumped for adapter API changes; local weights persist.
        var image = ManagedRuntimeProfiles.Image(record, options);
        var present = await runner.RunAsync(options.DockerExecutable,
            ["image", "inspect", image], TimeSpan.FromSeconds(10), token);
        if (present.Success) return;
        var directory = Path.Combine(AppContext.BaseDirectory, "airllm-runtime");
        var file = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture ==
            System.Runtime.InteropServices.Architecture.Arm64 ? "Dockerfile.arm64" : "Dockerfile";
        if (!File.Exists(Path.Combine(directory, file)))
            throw new InvalidOperationException("AirLLM runtime adapter " + file + " is not installed.");
        report?.Invoke(new TransferProgress("container-image", null,
            "Building updated AirLLM serving image without deleting model weights", null, null));
        await PullCommandAsync(["build", "--pull", "--file", Path.Combine(directory, file),
            "-t", image, directory], token, line => ParseTransferLine(line, "container-image", report));
    }

    private async Task EnsureWhisperImageAsync(ManagedModelRecord record,
        CancellationToken token, Action<TransferProgress>? report = null)
    {
        var image = ManagedRuntimeProfiles.Image(record, options);
        var present = await runner.RunAsync(options.DockerExecutable,
            ["image", "inspect", image], TimeSpan.FromSeconds(10), token);
        if (present.Success) return;
        var directory = Path.Combine(AppContext.BaseDirectory, "faster-whisper-runtime");
        var dockerfile = Path.Combine(directory, "Dockerfile");
        if (!File.Exists(dockerfile))
            throw new InvalidOperationException("Whisper runtime adapter is not installed in the Node Agent release.");
        report?.Invoke(new TransferProgress("container-image", null,
            "Building CPU Whisper adapter for host architecture (x64/arm64)", null, null));
        await PullCommandAsync(["build", "--pull", "--file", dockerfile, "-t", image, directory],
            token, line => ParseTransferLine(line, "container-image", report));
    }

    // Locally build versioned, allowlisted serving adapters from the signed Agent bundle.
    private async Task EnsureMediaImageAsync(ManagedModelRecord record,
        CancellationToken token, Action<TransferProgress>? report = null)
    {
        var image = ManagedRuntimeProfiles.Image(record, options);
        var present = await runner.RunAsync(options.DockerExecutable,
            ["image", "inspect", image], TimeSpan.FromSeconds(10), token);
        if (present.Success) return;
        var folder = record.Runtime switch {
            "laya" => "laya-runtime",
            "kokoro" => "kokoro-runtime",
            "diffusers" => "diffusers-runtime",
            _ => throw new ArgumentException("Unsupported media runtime")
        };
        var directory = Path.Combine(AppContext.BaseDirectory, folder);
        var file = record.Runtime == "diffusers" &&
            System.Runtime.InteropServices.RuntimeInformation.OSArchitecture ==
            System.Runtime.InteropServices.Architecture.Arm64 ? "Dockerfile.arm64" : "Dockerfile";
        var dockerfile = Path.Combine(directory, file);
        if (!File.Exists(dockerfile))
            throw new InvalidOperationException("Managed runtime adapter missing: " + folder + "/" + file);
        report?.Invoke(new TransferProgress("container-image", null,
            "Building experimental " + record.Runtime + " image for this host", null, null));
        await PullCommandAsync(["build", "--pull", "--file", dockerfile, "-t", image, directory],
            token, line => ParseTransferLine(line, "container-image", report));
    }

    public async Task<ManagedModelState> StartAsync(string installationId, CancellationToken cancellationToken)
    {
        await _operations.WaitAsync(cancellationToken);
        try
        {
            var record = await RequireRecordAsync(installationId, cancellationToken);
            if (record.Runtime == "airllm")
                await EnsureAirLlmImageAsync(record, cancellationToken);
            else if (record.Runtime == "faster-whisper")
                await EnsureWhisperImageAsync(record, cancellationToken);
            else if (record.Runtime is "laya" or "kokoro" or "diffusers")
                await EnsureMediaImageAsync(record, cancellationToken);
            var name = ContainerName(record);
            await RunDockerIgnoringFailureAsync(["rm", "-f", name], cancellationToken);

            var args = new List<string> { "run", "-d", "--name", name, "--restart", "unless-stopped", "--ipc=host" };
            var gpuReady = await HasGpuRuntimeAsync(cancellationToken);
            if (record.Runtime == "diffusers" && !gpuReady)
                throw new InvalidOperationException("Image generation requires a working NVIDIA driver and Docker GPU runtime.");
            if (options.UseNvidiaGpus && !gpuReady && record.Runtime is "vllm" or "sglang" or "airllm")
                throw new InvalidOperationException(
                    "The NVIDIA GPU runtime is unavailable. Repair Docker/NVIDIA Toolkit from LLMProxy Admin before starting this model.");
            if (gpuReady && record.Runtime is not ("faster-whisper" or "laya" or "kokoro"))
            {
                args.Add("--gpus");
                args.Add(string.IsNullOrWhiteSpace(record.GpuDevices) ? "all" : "device=" + record.GpuDevices);
            }
            args.Add("-v");
            args.Add($"{Path.GetFullPath(options.ModelCacheDirectory)}:{(record.Runtime == "llama.cpp" ? "/root/.cache/llama.cpp" : "/root/.cache/huggingface")}");
            if (record.Runtime == "faster-whisper")
            {
                args.AddRange(["-e", "WHISPER_MODEL_ID=" + record.ProviderModelName,
                    "-e", "WHISPER_CPU_THREADS=4"]);
                if (record.SourceRevision is { Length: 40 } sha)
                    args.AddRange(["-e", "WHISPER_MODEL_REVISION=" + sha]);
            }
            if (record.Runtime is "laya" or "kokoro" or "diffusers")
            {
                if (record.SourceRevision is not { Length: 40 } sha ||
                    !Regex.IsMatch(sha, "^[a-f0-9]{40}$"))
                    throw new InvalidOperationException("Managed media runtime requires a pinned downloaded checkpoint.");
                if (record.Runtime == "laya")
                {
                    var snapshot = "/root/.cache/huggingface/hub/models--convaiinnovations--laya/snapshots/" + sha;
                    args.AddRange(["-e", "LAYA_MODELS=english", "-e", "LAYA_DEFAULT_MODEL=english",
                        "-e", "LAYA_EXTRA_MODELS=" + JsonSerializer.Serialize(
                            new Dictionary<string, string> { ["english"] = snapshot }),
                        "-e", "LAYA_MAX_CONCURRENT=1", "-e", "HF_HUB_OFFLINE=1"]);
                }
                if (record.Runtime == "kokoro")
                    args.AddRange(["-e", "KOKORO_MODEL_REVISION=" + sha, "-e", "HF_HUB_OFFLINE=1"]);
                if (record.Runtime == "diffusers")
                    args.AddRange(["-e", "DIFFUSERS_MODEL_REVISION=" + sha, "-e", "HF_HUB_OFFLINE=1"]);
            }
            if (record.Runtime == "airllm")
            {
                args.AddRange(["-e", "AIRLLM_MODEL_ID=" + record.ProviderModelName,
                    "-e", "AIRLLM_MAX_QUEUED=" + (record.MaxNumSeqs ?? 1),
                    "-e", "AIRLLM_MAX_CONTEXT=" + (record.MaxModelLen ?? 8192),
                    "-e", "AIRLLM_MAX_OUTPUT_TOKENS=" + (record.MaxOutputTokens ?? 256)]);
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
                if (record.Runtime == "laya") await VerifyLayaInferenceAsync(record.Port, cancellationToken);
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

    public async Task<ManagedModelState> ConfigureAsync(string installationId,
        ManagedProfileUpdate update, CancellationToken cancellationToken)
    {
        await _operations.WaitAsync(cancellationToken);
        try
        {
            var record = await RequireRecordAsync(installationId, cancellationToken);
            if (await IsRunningAsync(record, cancellationToken))
                throw new InvalidOperationException(
                    "Stop this installation from LLMProxy before editing runtime limits. Model files are kept.");
            var request = new InstallRequest(record.CatalogModelId, record.ProviderModelName,
                record.Port, record.TensorParallelSize, record.ExtraArguments,
                record.Runtime, update.MaxNumSeqs, update.MaxModelLen,
                record.KvCacheDtype, record.CpuOffloadGiB, null, record.GpuDevices,
                update.MaxOutputTokens);
            ManagedRuntimeProfiles.Validate(request);
            var changed = record with {
                MaxModelLen = update.MaxModelLen,
                MaxNumSeqs = update.MaxNumSeqs,
                MaxOutputTokens = update.MaxOutputTokens,
                Status = "stopped",
                RuntimeBaseAddress = null,
                Error = null
            };
            await registry.UpsertAsync(changed, cancellationToken);
            return ToState(changed, "stopped", null);
        }
        finally { _operations.Release(); }
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
        // Hugging Face tqdm emits carriage-return-delimited percent for the current file.
        // Distinguish *file* progress from overall completed-weight bytes, emitted separately.
        if (stage == "model-weights")
        {
            var fileProgress = Regex.Match(line,
                @"(?<file>[^:\r\n]{1,100}):\s*(?<percent>\d{1,3}(?:\.\d+)?)%\|",
                RegexOptions.IgnoreCase);
            if (fileProgress.Success &&
                double.TryParse(fileProgress.Groups["percent"].Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var percent))
            {
                report(new TransferProgress("model-file", Math.Min(100, percent),
                    "Downloading " + fileProgress.Groups["file"].Value.Trim(), null, null));
                return;
            }
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

    // /health may be 200 before English Laya weights load; check typed inference.
    private async Task VerifyLayaInferenceAsync(int port, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(options.StartupTimeoutMinutes));
        var client = httpClientFactory.CreateClient("runtime-health");
        var address = $"http://127.0.0.1:{port}";
        var probe = new {
            state = "The client was charged twice for a purchase.",
            questions = new Dictionary<string, object> {
                ["team"] = new {
                    type = "choice",
                    instructions = "Which team should handle this?",
                    criteria = new { billing = "billing and refunds", general = "general questions" }
                }
            }
        };
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            try
            {
                using var status = await client.GetAsync(address + "/health", timeout.Token);
                if (status.IsSuccessStatusCode)
                {
                    using var health = await JsonDocument.ParseAsync(
                        await status.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
                    if (health.RootElement.TryGetProperty("loaded", out var loaded) &&
                        loaded.ValueKind == JsonValueKind.Array &&
                        loaded.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String &&
                            item.GetString() == "english"))
                    {
                        using var response = await client.PostAsJsonAsync(
                            address + "/v1/systemone", probe, timeout.Token);
                        if (response.IsSuccessStatusCode)
                        {
                            using var result = await JsonDocument.ParseAsync(
                                await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
                            if (result.RootElement.TryGetProperty("answers", out var answers) &&
                                answers.ValueKind == JsonValueKind.Object &&
                                answers.TryGetProperty("team", out var answer) &&
                                answer.TryGetProperty("probabilities", out var probabilities) &&
                                probabilities.ValueKind == JsonValueKind.Object)
                                return;
                        }
                    }
                }
            }
            catch (HttpRequestException) { }
            catch (JsonException) { }
            await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token);
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
            record.Runtime, record.MaxNumSeqs, record.MaxModelLen, record.KvCacheDtype, record.CpuOffloadGiB,
            record.GpuDevices, record.MaxOutputTokens, record.SourceRevision);

    private static string Trim(string value) => value.Length <= 1200 ? value.Trim() : value[..1200].Trim();

}
