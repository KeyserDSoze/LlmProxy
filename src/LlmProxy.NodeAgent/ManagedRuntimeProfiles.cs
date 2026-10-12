using System.Globalization;
using System.Text.RegularExpressions;

namespace LlmProxy.NodeAgent;

/// <summary>Explicit, inspectable launch profiles. No shell interpretation or implicit model conversion.</summary>
public static class ManagedRuntimeProfiles
{
    public static void Validate(InstallRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.CatalogModelId) || request.CatalogModelId.Length > 200)
            throw new ArgumentException("catalogModelId is required and must be at most 200 characters.");
        if (string.IsNullOrWhiteSpace(request.ProviderModelName) || request.ProviderModelName.Length > 300)
            throw new ArgumentException("providerModelName is required and must be at most 300 characters.");
        if (request.Runtime is not ("vllm" or "llama.cpp" or "sglang" or "airllm" or "faster-whisper"))
            throw new ArgumentException("Unsupported runtime. Only vllm, llama.cpp, sglang and experimental airllm are managed.");
        if (request.TensorParallelSize < 1 || request.TensorParallelSize > 64)
            throw new ArgumentOutOfRangeException(nameof(request.TensorParallelSize));
        if (request.Port is < 1024 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(request.Port));
        if (request.ExtraArguments is { Count: > 64 } || request.ExtraArguments?.Any(arg => arg.Length > 500) == true)
            throw new ArgumentException("Too many or too-long runtime arguments.");
        if (request.MaxNumSeqs is < 1 or > 128)
            throw new ArgumentOutOfRangeException(nameof(request.MaxNumSeqs), "Allowed concurrent sequence configuration: 1–128.");
        if (request.MaxModelLen is < 256 or > 262144)
            throw new ArgumentOutOfRangeException(nameof(request.MaxModelLen));
        if (request.MaxOutputTokens is not null &&
            (request.Runtime != "airllm" || request.MaxOutputTokens is < 1 or > 4096))
            throw new ArgumentOutOfRangeException(nameof(request.MaxOutputTokens),
                "Maximum output token configuration is supported by AirLLM only (1–4096).");
        if (request.Runtime == "airllm" && request.MaxOutputTokens is int output &&
            output > (request.MaxModelLen ?? 8192))
            throw new ArgumentOutOfRangeException(nameof(request.MaxOutputTokens),
                "Maximum output cannot exceed the configured model context.");
        if (request.KvCacheDtype is not null && request.KvCacheDtype is not ("auto" or "fp8"))
            throw new ArgumentException("KV cache dtype must be auto or fp8.");
        if (request.CpuOffloadGiB is < 0 or > 1024 or double.NaN or double.PositiveInfinity or double.NegativeInfinity)
            throw new ArgumentOutOfRangeException(nameof(request.CpuOffloadGiB));

        if (!string.IsNullOrEmpty(request.GpuDevices))
        {
            if (request.GpuDevices.Length > 80 ||
                !Regex.IsMatch(request.GpuDevices, @"^[0-9]{1,2}(?:,[0-9]{1,2})*$"))
                throw new ArgumentException("GPU assignment must be comma-separated numeric indices, e.g. 0,1.");
            var devices = request.GpuDevices.Split(',');
            if (devices.Distinct(StringComparer.Ordinal).Count() != devices.Length)
                throw new ArgumentException("GPU assignment may not repeat a device.");
            if (request.Runtime is "vllm" or "sglang" && devices.Length < request.TensorParallelSize)
                throw new ArgumentException("Assigned GPU count is smaller than requested tensor parallel size.");
            if (request.Runtime == "airllm" && devices.Length != 1)
                throw new ArgumentException("AirLLM experimental runtime uses exactly one selected GPU.");
        }

        if (request.Runtime == "sglang" && (request.KvCacheDtype is not null || request.CpuOffloadGiB is not null))
            throw new ArgumentException("vLLM-only KV cache and CPU weight offload flags are not supported for SGLang.");
        if (request.Runtime == "airllm")
        {
            if (request.TensorParallelSize != 1)
                throw new ArgumentException("Experimental AirLLM is single worker, tensor-parallel size must be one.");
            if (request.KvCacheDtype is not null || request.CpuOffloadGiB is not null)
                throw new ArgumentException("vLLM-only flags are not supported for AirLLM.");
            if (request.ExtraArguments is { Count: > 0 })
                throw new ArgumentException("Arbitrary AirLLM startup arguments are not permitted.");
            if (!Regex.IsMatch(request.ProviderModelName, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$"))
                throw new ArgumentException("AirLLM requires a valid Hugging Face repository ID.");
        }
        if (request.Runtime == "faster-whisper")
        {
            if (request.CatalogModelId is not ("whisper-tiny-cpu" or "whisper-base-cpu" or "whisper-small-cpu") ||
                request.ProviderModelName is not ("Systran/faster-whisper-tiny" or "Systran/faster-whisper-base" or "Systran/faster-whisper-small") ||
                (request.CatalogModelId switch {
                    "whisper-tiny-cpu" => request.ProviderModelName != "Systran/faster-whisper-tiny",
                    "whisper-base-cpu" => request.ProviderModelName != "Systran/faster-whisper-base",
                    "whisper-small-cpu" => request.ProviderModelName != "Systran/faster-whisper-small",
                    _ => true
                }))
                throw new ArgumentException("Whisper runtime only allows matching curated CTranslate2 checkpoints.");
            if (request.TensorParallelSize != 1 || request.GpuDevices is not null ||
                request.MaxNumSeqs is not null and not 1 ||
                request.MaxModelLen is not null || request.MaxOutputTokens is not null ||
                request.CpuOffloadGiB is not null || request.KvCacheDtype is not null ||
                request.ExtraArguments is { Count: > 0 })
                throw new ArgumentException("CPU Whisper runtime accepts no Chat/GPU/token settings; concurrency is one.");
        }
        if (request.Runtime == "llama.cpp")
        {
            if (request.TensorParallelSize != 1)
                throw new ArgumentException("llama.cpp does not use vLLM tensor parallel size.");
            if (request.KvCacheDtype is not null || request.CpuOffloadGiB is not null)
                throw new ArgumentException("vLLM KV cache and CPU weight offload flags cannot be applied to llama.cpp.");
            if (!Regex.IsMatch(request.ProviderModelName, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(:[A-Za-z0-9_.-]+)?$"))
                throw new ArgumentException("llama.cpp requires a trusted Hugging Face GGUF repo[/model] with optional :quant tag.");
        }
    }

    public static ManagedModelRecord? FindMatchingInstallation(IReadOnlyList<ManagedModelRecord> rows, InstallRequest request) =>
        rows.FirstOrDefault(record =>
            string.Equals(record.CatalogModelId, request.CatalogModelId.Trim(), StringComparison.OrdinalIgnoreCase) &&
            Matches(record, request) &&
            (request.Port is null || record.Port == request.Port));

    public static bool Matches(ManagedModelRecord record, InstallRequest request) =>
        record.Runtime == request.Runtime &&
        record.ProviderModelName == request.ProviderModelName.Trim() &&
        record.TensorParallelSize == request.TensorParallelSize &&
        record.MaxNumSeqs == request.MaxNumSeqs &&
        record.MaxModelLen == request.MaxModelLen &&
        record.MaxOutputTokens == request.MaxOutputTokens &&
        record.KvCacheDtype == request.KvCacheDtype &&
        record.CpuOffloadGiB == request.CpuOffloadGiB &&
        record.GpuDevices == request.GpuDevices &&
        record.ExtraArguments.SequenceEqual(request.ExtraArguments ?? []);

    public static string Image(ManagedModelRecord record, NodeAgentOptions options) =>
        record.Runtime switch
        {
            "llama.cpp" => options.LlamaCppDockerImage,
            "sglang" => options.SglangDockerImage,
            "airllm" => "llmproxy-airllm:0.3",
            "faster-whisper" => "llmproxy-faster-whisper:0.1",
            _ => options.DockerImage
        };

    public static IReadOnlyList<string> Arguments(ManagedModelRecord record, NodeAgentOptions options)
    {
        var args = new List<string>();
        if (record.Runtime is "airllm" or "faster-whisper")
        {
            // Entrypoint in the locally-built, allowlisted serving image.
            // Queued requests must never be mistaken for GPU-parallel inference.
        }
        else if (record.Runtime == "llama.cpp")
        {
            args.AddRange(["--hf-repo", record.ProviderModelName, "--alias", record.ProviderModelName,
                "--host", "0.0.0.0", "--port", "8080", "--cont-batching", "--metrics"]);
            if (options.UseNvidiaGpus) args.AddRange(["--n-gpu-layers", "-1"]);
            if (record.MaxNumSeqs is int parallel) args.AddRange(["--parallel", parallel.ToString(CultureInfo.InvariantCulture)]);
            if (record.MaxModelLen is int context) args.AddRange(["--ctx-size", context.ToString(CultureInfo.InvariantCulture)]);
        }
        else if (record.Runtime == "sglang")
        {
            args.AddRange(["python3", "-m", "sglang.launch_server", "--model-path", record.ProviderModelName,
                "--host", "0.0.0.0", "--port", "30000", "--tp-size",
                record.TensorParallelSize.ToString(CultureInfo.InvariantCulture), "--enable-metrics"]);
            if (record.MaxNumSeqs is int parallel) args.AddRange(["--max-running-requests", parallel.ToString(CultureInfo.InvariantCulture)]);
            if (record.MaxModelLen is int context) args.AddRange(["--context-length", context.ToString(CultureInfo.InvariantCulture)]);
        }
        else
        {
            args.AddRange(["--model", record.ProviderModelName, "--tensor-parallel-size",
                record.TensorParallelSize.ToString(CultureInfo.InvariantCulture)]);
            if (record.MaxNumSeqs is int parallel) args.AddRange(["--max-num-seqs", parallel.ToString(CultureInfo.InvariantCulture)]);
            if (record.MaxModelLen is int context) args.AddRange(["--max-model-len", context.ToString(CultureInfo.InvariantCulture)]);
            if (record.KvCacheDtype is not null) args.AddRange(["--kv-cache-dtype", record.KvCacheDtype]);
            if (record.CpuOffloadGiB is double cpu) args.AddRange(["--cpu-offload-gb", cpu.ToString(CultureInfo.InvariantCulture)]);
        }
        args.AddRange(record.ExtraArguments);
        return args;
    }
}
