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
    string? GatewayBaseAddress,
    string? EnrollmentToken,
    string ConnectionMode)
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
            section["ConnectionMode"] ?? "outbound");
    }

    private static int ParseInt(string? raw, int fallback, int minimum, int maximum) =>
        int.TryParse(raw, out var parsed) && parsed >= minimum && parsed <= maximum ? parsed : fallback;

    private static bool ParseBool(string? raw, bool fallback) =>
        bool.TryParse(raw, out var parsed) ? parsed : fallback;
}
