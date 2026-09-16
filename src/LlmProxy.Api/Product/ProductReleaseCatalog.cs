using System.Reflection;

namespace LlmProxy.Api.Product;

public static class ProductReleaseCatalog
{
    private static readonly DateOnly InitialVersionedReleaseDate = new(2026, 9, 16);

    public static ProductReleaseInfo GetInfo()
    {
        var assembly = typeof(ProductReleaseCatalog).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        var assemblyVersion = assembly.GetName().Version?.ToString() ?? "0.0.0";
        var version = string.IsNullOrWhiteSpace(informationalVersion)
            ? assemblyVersion
            : informationalVersion.Split('+', 2, StringSplitOptions.None)[0];
        var versionParts = informationalVersion?.Split('+', 2, StringSplitOptions.None);
        var embeddedRevision = versionParts is { Length: 2 } ? versionParts[1] : null;
        var buildRevision = FirstNonEmpty(
            Environment.GetEnvironmentVariable("LLMPROXY_BUILD_SHA"),
            Environment.GetEnvironmentVariable("GITHUB_SHA"),
            embeddedRevision);
        var builtAtUtc = DateTimeOffset.TryParse(Environment.GetEnvironmentVariable("LLMPROXY_BUILD_DATE"), out var parsedBuildDate)
            ? parsedBuildDate
            : (DateTimeOffset?)null;

        var release = new ProductRelease(
            version,
            InitialVersionedReleaseDate,
            "Initial versioned preview baseline",
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Added"] =
                [
                    "OpenAI-compatible Chat Completions and Responses surfaces with SSE streaming and cancellation.",
                    "Logical-model routing across DGX/vLLM with weighted least loaded, round robin and weighted round robin strategies.",
                    "Distributed runtime state using PostgreSQL as durable truth, Redis as shared L2/coordination and local RAM as request-path L1.",
                    "Physical capacity admission with Redis leases, fail-closed coordination and active lease-loss cancellation.",
                    "Caller governance with HMAC-backed credentials, Usage Groups, request-rate limits and output-token budgets.",
                    "Credential rotation with one-time replacement secrets and cross-replica runtime propagation.",
                    "Metadata-only request metrics, audit, vLLM runtime telemetry and optional DGX hardware telemetry.",
                    "Repository-supported PostgreSQL backup/restore operators for Bash and PowerShell with destructive clean-target verification.",
                    "Safe node maintenance flow with distributed admission pre-block, drain-to-zero, health/models/warm-up validation and controlled resume.",
                    "Product version and release notes surfaced through the Admin API and UI."
                ],
                ["Changed"] =
                [
                    "Node maintenance is now the supported path for runtime/model upgrades; a draining node cannot re-enter routing until validation succeeds.",
                    "Release management now follows SemVer while the product remains pre-1.0."
                ],
                ["Fixed"] =
                [
                    "Closed the cross-replica drain race by enforcing the maintenance marker inside distributed capacity admission.",
                    "Removed the unsafe legacy drain path from normal administration; callers are directed to the maintenance API."
                ],
                ["Security"] =
                [
                    "Raw prompts, generated content and API secrets remain excluded from persistent telemetry and audit by default.",
                    "Inference credentials are persisted as HMAC hashes; raw credential material is returned only at creation or rotation time."
                ]
            });

        return new ProductReleaseInfo(
            "LlmProxy",
            version,
            version.Contains('-', StringComparison.Ordinal) ? "preview" : "stable",
            InitialVersionedReleaseDate,
            buildRevision,
            builtAtUtc,
            [release]);
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
}

public sealed record ProductReleaseInfo(
    string Product,
    string Version,
    string Channel,
    DateOnly ReleasedOn,
    string? BuildRevision,
    DateTimeOffset? BuiltAtUtc,
    IReadOnlyList<ProductRelease> Releases);

public sealed record ProductRelease(
    string Version,
    DateOnly ReleasedOn,
    string Title,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Sections);
