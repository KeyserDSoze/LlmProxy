using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LlmProxy.NodeAgent;

public sealed class DockerModelRuntimeManager(
    NodeAgentOptions options,
    ProcessRunner runner,
    ManagedModelRegistry registry,
    IHttpClientFactory httpClientFactory,
    GpuRuntimeSafety gpuSafety,
    GgufModelStore gguf,
    HuggingFaceCredentialStore huggingFace)
{
    // Short critical sections only (registry reservation, container create/remove).
    // Long downloads, image builds and health waits hold a per-installation guard instead,
    // so one slow model never blocks Stop of another.
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly HashSet<string> _busy = new(StringComparer.Ordinal);

    private IDisposable AcquireInstallation(string installationId)
    {
        lock (_busy)
        {
            if (!_busy.Add(installationId))
                throw new InvalidOperationException(
                    "Another install, start, stop or edit operation is already running for this model. " +
                    "Wait for it to finish or cancel its job first.");
        }
        return new InstallationLease(() => { lock (_busy) _busy.Remove(installationId); });
    }

    private bool IsBusy(string installationId)
    {
        lock (_busy) return _busy.Contains(installationId);
    }

    private sealed class InstallationLease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

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
        ManagedModelRecord record;
        await _operations.WaitAsync(cancellationToken);
        try
        {
            var rows = await registry.ReadAsync(cancellationToken);
            var existing = ManagedRuntimeProfiles.FindMatchingInstallation(rows, request);
            if (existing is not null)
            {
                if (IsBusy(existing.InstallationId))
                    throw new InvalidOperationException(
                        "This model profile is already being installed or started on the Agent.");
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

            record = new ManagedModelRecord
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
                GpuMemoryUtilization = request.GpuMemoryUtilization,
                GpuDevices = request.GpuDevices,
                Port = port,
                TensorParallelSize = request.TensorParallelSize,
                ExtraArguments = request.ExtraArguments?.ToArray() ?? [],
                Status = "installing"
            };
            await registry.UpsertAsync(record, cancellationToken);
        }
        finally
        {
            _operations.Release();
        }

        using var lease = AcquireInstallation(record.InstallationId);
        {
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
                    try
                    {
                    await PullCommandAsync(
                    [
                        "run", "--rm",
                        "--entrypoint", "python3",
                        ..(huggingFace.Token is null ? Array.Empty<string>() : new[] { "-e", "HF_TOKEN" }),
                        ..(record.Runtime is "laya" or "kokoro" or "diffusers" ? new[] { "-e", "HF_HUB_OFFLINE=0" } : Array.Empty<string>()),
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
                    }, huggingFace.ProcessEnvironment());
                    }
                    catch (InvalidOperationException error) when (
                        HuggingFaceCredentialStore.ExplainAccessFailure(record.ProviderModelName, error.Message,
                            huggingFace.Token is not null) is { } explained)
                    {
                        throw new InvalidOperationException(explained, error);
                    }
                    if (cachedSourceRevision is not null)
                        record = record with { SourceRevision = cachedSourceRevision };
                }
                if (record.Runtime == "llama.cpp")
                {
                    // Resolve repo[:quant] to an exact GGUF at a pinned commit and download it now,
                    // with progress and actionable errors, instead of a lazy -hf download at start.
                    var prepared = await gguf.PrepareAsync(record.ProviderModelName, null, report, cancellationToken);
                    record = record with
                    {
                        SourceRevision = prepared.Revision,
                        ModelFile = prepared.ModelFile,
                        MmprojFile = prepared.MmprojFile
                    };
                }

                report?.Invoke(new TransferProgress("ready", 100, "Runtime and model files prepared", null, null));
                if (await registry.FindAsync(record.InstallationId, CancellationToken.None) is null)
                    throw new InvalidOperationException("The installation was removed while its files were being prepared.");
                record = record with { Status = "stopped", Error = null };
                await registry.UpsertAsync(record, cancellationToken);
                return ToState(record, "stopped", null);
            }
            catch (Exception exception)
            {
                if (await registry.FindAsync(record.InstallationId, CancellationToken.None) is not null)
                {
                    record = record with { Status = "error", Error = exception.Message };
                    await registry.UpsertAsync(record, CancellationToken.None);
                }
                throw;
            }
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

    public async Task<ManagedModelState> StartAsync(string installationId, CancellationToken cancellationToken,
        Action<string>? progress = null)
    {
        using var lease = AcquireInstallation(installationId);
        var record = await RequireRecordAsync(installationId, cancellationToken);
        if (record.Status == "installing")
            throw new InvalidOperationException(
                "Model files are still being prepared for this installation. Wait for its download job to finish.");
        try
        {
            if (record.Runtime == "airllm")
                await EnsureAirLlmImageAsync(record, cancellationToken);
            else if (record.Runtime == "faster-whisper")
                await EnsureWhisperImageAsync(record, cancellationToken);
            else if (record.Runtime is "laya" or "kokoro" or "diffusers")
                await EnsureMediaImageAsync(record, cancellationToken);
            else if (record.Runtime == "llama.cpp" && !gguf.IsPrepared(record))
            {
                // Installations made before pinned GGUF downloads (or whose cache was cleaned)
                // are prepared here, so llama-server never starts without a resolved model file.
                progress?.Invoke("Preparing GGUF weights in the persistent model cache before start.");
                var prepared = await gguf.PrepareAsync(record.ProviderModelName,
                    record.ModelFile is null ? null : record.SourceRevision,
                    step => progress?.Invoke(step.Percent is double percent
                        ? $"{step.Detail} · {percent:0.#}%" : step.Detail),
                    cancellationToken);
                record = record with
                {
                    SourceRevision = prepared.Revision,
                    ModelFile = prepared.ModelFile,
                    MmprojFile = prepared.MmprojFile
                };
                await registry.UpsertAsync(record, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            record = record with { Status = "error", RuntimeBaseAddress = null,
                Error = "Preparation before Docker start failed: " + exception.Message };
            await registry.UpsertAsync(record, CancellationToken.None);
            throw;
        }

        var name = ContainerName(record);
        var gpuReady = await HasGpuRuntimeAsync(cancellationToken);
        if (record.Runtime == "diffusers" && !gpuReady)
            throw new InvalidOperationException("Image generation requires a working NVIDIA driver and Docker GPU runtime.");
        if (options.UseNvidiaGpus && !gpuReady && record.Runtime is "vllm" or "sglang" or "airllm")
            throw new InvalidOperationException(
                "The NVIDIA GPU runtime is unavailable. Repair Docker/NVIDIA Toolkit from LLMProxy Admin before starting this model.");
        var gpuRuntime = ManagedRuntimeRestartGuard.RequiresManualGpuRestart(
            record.Runtime, options.UseNvidiaGpus) && gpuReady;
        if (gpuRuntime) await gpuSafety.EnsureAllowedAsync(cancellationToken);
        var args = new List<string> { "run", "-d", "--name", name,
            "--restart", gpuRuntime ? "no" : "unless-stopped", "--ipc=host" };
        if (gpuReady && record.Runtime is not ("faster-whisper" or "laya" or "kokoro"))
        {
            args.Add("--gpus");
            args.Add(string.IsNullOrWhiteSpace(record.GpuDevices) ? "all" : "device=" + record.GpuDevices);
        }
        // One shared Hugging Face cache layout for every runtime, llama.cpp included.
        args.Add("-v");
        args.Add($"{Path.GetFullPath(options.ModelCacheDirectory)}:{GgufModelStore.ContainerCacheRoot}");
        if (record.Runtime == "llama.cpp")
            args.AddRange(["-e", "LLAMA_CACHE=" + GgufModelStore.ContainerCacheRoot + "/hub"]);
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
        args.AddRange(ManagedRuntimeProfiles.Arguments(record, options, gpuReady));

        try
        {
            progress?.Invoke("Starting Docker runtime; waiting for model /health. No automatic crash restarts.");
            await _operations.WaitAsync(cancellationToken);
            try
            {
                // Do not remove any previous container before thermal admission check.
                await RunDockerIgnoringFailureAsync(["rm", "-f", name], cancellationToken);
                await RequireDockerAsync(args, cancellationToken);
            }
            finally { _operations.Release(); }
            await WaitForHealthyAsync(record, cancellationToken);
            if (record.Runtime == "laya") await VerifyLayaInferenceAsync(record.Port, cancellationToken);
            record = record with { Status = "running", RuntimeBaseAddress = RuntimeAddress(record.Port), Error = null };
            await registry.UpsertAsync(record, cancellationToken);
            return ToState(record, "running", record.RuntimeBaseAddress);
        }
        catch (Exception exception)
        {
            var diagnostics = await ReadFailedDockerLogsAsync(name, record.InstallationId);
            // Failed startup must release GPU allocation and cannot leave a
            // crashing inference container behind.
            try
            {
                await runner.RunAsync(options.DockerExecutable, ["update", "--restart=no", name],
                    TimeSpan.FromSeconds(10), CancellationToken.None);
                await runner.RunAsync(options.DockerExecutable, ["rm", "-f", name],
                    TimeSpan.FromSeconds(20), CancellationToken.None);
            }
            catch (Exception) { /* Preserve the original startup failure. */ }
            var cause = exception is OperationCanceledException
                ? cancellationToken.IsCancellationRequested
                    ? "Startup cancelled by Agent stop/operator request; GPU container cleanup requested."
                    : $"Runtime did not become healthy within {options.StartupTimeoutMinutes} minutes."
                : exception.Message;
            var combined = cause + diagnostics;
            record = record with
            {
                Status = "error",
                Error = combined.Length > 3000 ? combined[..3000] : combined,
                RuntimeBaseAddress = null
            };
            await registry.UpsertAsync(record, CancellationToken.None);
            throw;
        }
    }

    private async Task<string> ReadFailedDockerLogsAsync(string name, string installationId)
    {
        try
        {
            var result = await runner.RunAsync(options.DockerExecutable,
                ["logs", "--tail", "600", name], TimeSpan.FromSeconds(12), CancellationToken.None);
            if (!result.Success) return "";
            var logs = DockerStartupDiagnostics.Sanitize(
                (result.StandardError + "\n" + result.StandardOutput).Trim());
            if (logs.Length == 0) return "";

            // Persist a bounded, token-redacted postmortem BEFORE docker rm.
            // Keep only the latest attempt per installation; no unbounded
            // logs and no need to preserve Docker restart loops.
            string? file = null;
            if (Regex.IsMatch(installationId, "^[a-z0-9-]{1,100}$"))
            {
                try
                {
                    var directory = Path.Combine(options.DataDirectory, "startup-logs");
                    Directory.CreateDirectory(directory);
                    if (OperatingSystem.IsLinux())
                        File.SetUnixFileMode(directory,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    file = Path.Combine(directory, installationId + ".log");
                    // Keep the latest ~48 KiB; the UI's extracted root cause
                    // comes from the entire captured (up to 600-line) output.
                    var bounded = logs.Length > 48000 ? logs[^48000..] : logs;
                    await File.WriteAllTextAsync(file, bounded, CancellationToken.None);
                    if (OperatingSystem.IsLinux())
                        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                catch (Exception) { file = null; /* Best effort. */ }
            }
            return DockerStartupDiagnostics.ForUi(logs, file);
        }
        catch (Exception) { return ""; }
    }

    // Stop must be observed in Docker, not inferred from a best-effort command.
    // Force may only target a registered, managed installation; it does not touch
    // Docker containers or GPU processes owned by other services.
    public async Task<ManagedModelState> StopAsync(string installationId,
        CancellationToken cancellationToken, bool force = false)
    {
        // Force stop is the emergency path and must work even while a start is hanging.
        using var lease = force ? null : AcquireInstallation(installationId);
        {
            var record = await RequireRecordAsync(installationId, cancellationToken);
            var name = ContainerName(record);
            var before = await runner.RunAsync(options.DockerExecutable,
                ["inspect", "-f", "{{.State.Running}}", name],
                TimeSpan.FromSeconds(10), cancellationToken);
            if (!before.Success && !DockerContainerMissing(before.StandardError))
                throw new InvalidOperationException(
                    "Cannot inspect managed Docker runtime before stopping: " + Trim(before.StandardError));
            if (before.Success)
            {
                var command = force ? new[] { "kill", name } : new[] { "stop", "--time", "30", name };
                var stopped = await runner.RunAsync(options.DockerExecutable, command,
                    TimeSpan.FromSeconds(55), cancellationToken);
                if (!stopped.Success)
                {
                    // An already-exited container can race with the stop command.
                    var check = await runner.RunAsync(options.DockerExecutable,
                        ["inspect", "-f", "{{.State.Running}}", name],
                        TimeSpan.FromSeconds(10), cancellationToken);
                    if (check.Success && check.StandardOutput.Trim() == "true" ||
                        !check.Success && !DockerContainerMissing(check.StandardError))
                        throw new InvalidOperationException(
                            "Managed Docker stop failed: " + Trim(stopped.StandardError));
                }
            }

            // Verify state on two reads so a crashing/restarting runtime does not
            // look stopped during the instant between restart-policy attempts.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var verify = await runner.RunAsync(options.DockerExecutable,
                    ["inspect", "-f", "{{.State.Running}}", name],
                    TimeSpan.FromSeconds(10), cancellationToken);
                if (!verify.Success && !DockerContainerMissing(verify.StandardError))
                    throw new InvalidOperationException(
                        "Cannot verify managed Docker stop: " + Trim(verify.StandardError));
                if (verify.Success && verify.StandardOutput.Trim() != "false")
                    throw new InvalidOperationException(
                        "Docker still reports the managed runtime as running. Memory is not confirmed released.");
                if (attempt == 0) await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
            record = record with { Status = "stopped", RuntimeBaseAddress = null, Error = null };
            await registry.UpsertAsync(record, cancellationToken);
            return ToState(record, "stopped", null);
        }
    }

    private static bool DockerContainerMissing(string message) =>
        message.Contains("No such object", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("No such container", StringComparison.OrdinalIgnoreCase);

    public async Task<ManagedModelState> ConfigureAsync(string installationId,
        ManagedProfileUpdate update, CancellationToken cancellationToken)
    {
        using var lease = AcquireInstallation(installationId);
        {
            var record = await RequireRecordAsync(installationId, cancellationToken);
            if (await IsRunningAsync(record, cancellationToken))
                throw new InvalidOperationException(
                    "Stop this installation from LLMProxy before editing runtime limits. Model files are kept.");
            var request = new InstallRequest(record.CatalogModelId, record.ProviderModelName,
                record.Port, record.TensorParallelSize, record.ExtraArguments,
                record.Runtime, update.MaxNumSeqs, update.MaxModelLen,
                record.KvCacheDtype, record.CpuOffloadGiB, null, record.GpuDevices,
                update.MaxOutputTokens, false, update.GpuMemoryUtilization);
            ManagedRuntimeProfiles.Validate(request);
            var changed = record with {
                MaxModelLen = update.MaxModelLen,
                MaxNumSeqs = update.MaxNumSeqs,
                MaxOutputTokens = update.MaxOutputTokens,
                GpuMemoryUtilization = update.GpuMemoryUtilization,
                Status = "stopped",
                RuntimeBaseAddress = null,
                Error = null
            };
            await registry.UpsertAsync(changed, cancellationToken);
            return ToState(changed, "stopped", null);
        }
    }

    public async Task RemoveAsync(string installationId, CancellationToken cancellationToken)
    {
        using var lease = AcquireInstallation(installationId);
        var record = await RequireRecordAsync(installationId, cancellationToken);
        var name = ContainerName(record);
        var removed = await runner.RunAsync(options.DockerExecutable, ["rm", "-f", name],
            TimeSpan.FromMinutes(2), cancellationToken);
        if (!removed.Success && !DockerContainerMissing(removed.StandardError))
            throw new InvalidOperationException(
                "Docker could not remove the managed container, so the installation is kept and still manageable: " +
                Trim(removed.StandardError));
        var verify = await runner.RunAsync(options.DockerExecutable, ["inspect", "-f", "{{.State.Running}}", name],
            TimeSpan.FromSeconds(10), cancellationToken);
        if (verify.Success)
            throw new InvalidOperationException(
                "Docker still reports the managed container after removal; the installation is kept and still manageable.");
        await registry.RemoveAsync(installationId, cancellationToken);
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

    private async Task PullCommandAsync(IReadOnlyList<string> args, CancellationToken token, Action<string> progress,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var result = await runner.RunStreamingAsync(options.DockerExecutable, args,
            TimeSpan.FromMinutes(options.CommandTimeoutMinutes), token, progress, environment);
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

    private async Task WaitForHealthyAsync(ManagedModelRecord record, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(options.StartupTimeoutMinutes));
        var client = httpClientFactory.CreateClient("runtime-health");
        var url = $"http://127.0.0.1:{record.Port}/health";

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

            var state = await runner.RunAsync(options.DockerExecutable,
                ["inspect", "-f", "{{.State.Running}} {{.State.ExitCode}}", ContainerName(record)],
                TimeSpan.FromSeconds(8), timeoutCts.Token);
            if (state.Success && state.StandardOutput.Trim().StartsWith("false ", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Model container exited during startup with exit code " + state.StandardOutput.Trim()["false ".Length..] +
                    ". Automatic GPU restart is disabled; review the diagnosis below before retrying.");
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
        // Docker container names accept ASCII only; never derive them from Unicode letters.
        var slug = new string(catalogModelId.Trim().ToLowerInvariant().Select(character =>
            char.IsAsciiLetterOrDigit(character) ? character : '-').ToArray()).Trim('-');
        if (slug.Length > 32) slug = slug[..32].TrimEnd('-');
        if (slug.Length == 0) slug = "model";
        return $"{slug}-{Guid.NewGuid():N}"[..(slug.Length + 9)];
    }

    private static ManagedModelState ToState(ManagedModelRecord record, string status, string? runtimeBaseAddress) =>
        new(record.InstallationId, record.CatalogModelId, record.ProviderModelName, status, runtimeBaseAddress, record.Port, record.Error,
            record.Runtime, record.MaxNumSeqs, record.MaxModelLen, record.KvCacheDtype, record.CpuOffloadGiB,
            record.GpuDevices, record.MaxOutputTokens, record.SourceRevision,
            record.GpuMemoryUtilization, record.ModelFile);

    private static string Trim(string value) => value.Length <= 1200 ? value.Trim() : value[..1200].Trim();

}
