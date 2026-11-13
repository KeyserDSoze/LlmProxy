namespace LlmProxy.Api.OpenAi;

/// <summary>
/// Bounds how long an inference node may stay silent. Without them a node that accepts the connection and
/// then stalls would hold a capacity slot for as long as the client waits. Defaults are deliberately generous
/// because reasoning models can think for minutes before the first byte of a non-streaming answer.
/// </summary>
public readonly record struct UpstreamTimeouts(TimeSpan ResponseHeaders, TimeSpan StreamIdle)
{
    public const string HeaderSecondsKey = "Gateway:UpstreamResponseHeaderTimeoutSeconds";
    public const string IdleSecondsKey = "Gateway:UpstreamIdleTimeoutSeconds";
    public static readonly TimeSpan DefaultResponseHeaders = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan DefaultStreamIdle = TimeSpan.FromMinutes(5);

    public static UpstreamTimeouts From(IConfiguration configuration) => new(
        Read(configuration[HeaderSecondsKey], DefaultResponseHeaders),
        Read(configuration[IdleSecondsKey], DefaultStreamIdle));

    internal static TimeSpan Read(string? raw, TimeSpan fallback) =>
        int.TryParse(raw, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds) &&
        seconds is >= 5 and <= 24 * 3600
            ? TimeSpan.FromSeconds(seconds)
            : fallback;
}
