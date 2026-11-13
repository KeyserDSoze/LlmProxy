using Microsoft.Extensions.Configuration;

namespace LlmProxy.NodeAgent;

public sealed record NodeAgentOptions(
    string? BearerToken,
    string DockerExecutable,
    string DockerImage,
    string DataDirectory,
    string ModelCacheDirectory,
    string AdvertiseHost,
    int PortStart,
    bool UseNvidiaGpus,
    bool PrefetchModels,
    int CommandTimeoutMinutes,
    int StartupTimeoutMinutes,
    string LlamaCppDockerImage,
    string SglangDockerImage,
    string? GatewayBaseAddress = null,
    string? EnrollmentToken = null,
    string ConnectionMode = "outbound",
    Guid? RecoveryNodeId = null,
    string? RecoveryToken = null,
    bool ForceRecovery = false,
    double GpuStartMaxTemperatureCelsius = 80,
    bool RequireGpuTemperatureSensor = false,
    string HuggingFaceEndpoint = "https://huggingface.co",
    string? HuggingFaceToken = null,
    bool ApplyModelFamilyProfiles = true)
{
    public static NodeAgentOptions From(IConfiguration configuration)
    {
        var section = configuration.GetSection("NodeAgent");
        return new(
            section["BearerToken"],
            section["DockerExecutable"] ?? "docker",
            section["DockerImage"] ?? "vllm/vllm-openai:latest",
            section["DataDirectory"] ?? "/var/lib/llmproxy-node-agent",
            section["ModelCacheDirectory"] ?? "/var/lib/llmproxy-node-agent/huggingface",
            string.IsNullOrWhiteSpace(section["AdvertiseHost"]) ? Environment.MachineName : section["AdvertiseHost"]!,
            ParseInt(section["PortStart"], 18000, 1024, 65500),
            ParseBool(section["UseNvidiaGpus"], true),
            ParseBool(section["PrefetchModels"], true),
            ParseInt(section["CommandTimeoutMinutes"], 60, 1, 360),
            ParseInt(section["StartupTimeoutMinutes"], 20, 1, 120),
            section["LlamaCppDockerImage"] ?? "ghcr.io/ggml-org/llama.cpp:server-cuda",
            section["SglangDockerImage"] ?? "lmsysorg/sglang:latest",
            section["GatewayBaseAddress"],
            section["EnrollmentToken"],
            section["ConnectionMode"] ?? "outbound",
            Guid.TryParse(section["RecoveryNodeId"], out var recoveryNodeId) ? recoveryNodeId : null,
            section["RecoveryToken"],
            ParseBool(section["ForceRecovery"], false),
            ParseTemperature(section["GpuStartMaxTemperatureCelsius"], 80),
            ParseBool(section["RequireGpuTemperatureSensor"], false),
            ParseEndpoint(section["HuggingFaceEndpoint"] ?? configuration["HF_ENDPOINT"]),
            NullIfBlank(section["HuggingFaceToken"]) ?? NullIfBlank(configuration["HF_TOKEN"]),
            ParseBool(section["ApplyModelFamilyProfiles"], true));
    }

    // Mirrors are allowed (HF_ENDPOINT), but only as an absolute http(s) root.
    private static string ParseEndpoint(string? raw) =>
        Uri.TryCreate(raw?.Trim(), UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) &&
        string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.UserInfo)
            ? uri.GetLeftPart(UriPartial.Path).TrimEnd('/')
            : "https://huggingface.co";

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int ParseInt(string? raw, int fallback, int minimum, int maximum) =>
        int.TryParse(raw, out var parsed) && parsed >= minimum && parsed <= maximum ? parsed : fallback;

    private static double ParseTemperature(string? raw, double fallback) =>
        double.TryParse(raw, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) &&
        double.IsFinite(parsed) && parsed is >= 60 and <= 100 ? parsed : fallback;

    private static bool ParseBool(string? raw, bool fallback) =>
        bool.TryParse(raw, out var parsed) ? parsed : fallback;
}
