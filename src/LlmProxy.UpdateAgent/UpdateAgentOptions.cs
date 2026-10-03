namespace LlmProxy.UpdateAgent;

public sealed record UpdateAgentOptions(
    string? BearerToken,
    string InstallDirectory,
    string BootstrapPath,
    string StateDirectory,
    int CommandTimeoutMinutes)
{
    public static UpdateAgentOptions From(IConfiguration configuration)
    {
        var section = configuration.GetSection("UpdateAgent");
        return new(
            section["BearerToken"],
            section["InstallDirectory"] ?? "/opt/llmproxy",
            section["BootstrapPath"] ?? "/usr/local/lib/llmproxy/bootstrap.sh",
            section["StateDirectory"] ?? "/var/lib/llmproxy-update-agent",
            ParseInt(section["CommandTimeoutMinutes"], 180, 5, 720));
    }

    private static int ParseInt(string? raw, int fallback, int minimum, int maximum) =>
        int.TryParse(raw, out var parsed) && parsed >= minimum && parsed <= maximum ? parsed : fallback;
}
