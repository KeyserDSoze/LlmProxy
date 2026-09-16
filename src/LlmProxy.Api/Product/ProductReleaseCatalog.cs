using System.Reflection;

namespace LlmProxy.Api.Product;

public static class ProductReleaseCatalog
{
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

        var releases = BuildReleases();
        var currentRelease = releases.FirstOrDefault(release => release.Version == version) ?? releases[0];

        return new ProductReleaseInfo(
            "LlmProxy",
            version,
            version.Contains('-', StringComparison.Ordinal) ? "preview" : "stable",
            currentRelease.ReleasedOn,
            buildRevision,
            builtAtUtc,
            releases);
    }

    private static IReadOnlyList<ProductRelease> BuildReleases() =>
    [
        new ProductRelease(
            "0.2.0-preview.4",
            new DateOnly(2026, 9, 16),
            "Consolidated Linux production deployment",
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Added"] =
                [
                    "Linux production deployment now has one documented full-stack path with PostgreSQL, Redis and bundled observability.",
                    "A production environment template and end-to-end Linux runbook cover private bootstrap, DGX connectivity, Entra/public exposure, backup, update and rollback.",
                    "Production deployment stages runtime Compose and observability assets under /opt/llmproxy so containers do not depend on a transient runner workspace.",
                    "Cloudflare Tunnel can be enabled as an optional Compose profile when a tunnel token is configured."
                ],
                ["Changed"] =
                [
                    "The production deploy script and GitHub Actions deploy workflow now use the Redis-enabled full stack instead of the legacy minimal Compose overlay.",
                    "Production deployment validates Compose configuration and requires both liveness and readiness checks before reporting success."
                ],
                ["Security"] =
                [
                    "Grafana can bind to loopback independently from the gateway, and public-tunnel guidance requires Entra protection before exposing administrative surfaces."
                ]
            }),
        new ProductRelease(
            "0.2.0-preview.3",
            new DateOnly(2026, 9, 16),
            "Validated tagged releases",
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Added"] =
                [
                    "Exact SemVer tag publication requires evidence that the same source SHA already completed CI successfully from a push to main.",
                    "Release manifests record the validating CI run ID alongside image digest, source SHA, build timestamp, SBOM and provenance evidence.",
                    "The tagged-release validation predicate has positive and negative CI fixtures so the gate is tested without creating a real immutable tag."
                ],
                ["Changed"] =
                [
                    "A direct version tag can no longer publish an exact-version GHCR image solely because the tag matches compiled version metadata."
                ],
                ["Security"] =
                [
                    "Exact versioned container releases are tied to previously validated main source rather than trusting tag creation alone."
                ]
            }),
        new ProductRelease(
            "0.2.0-preview.2",
            new DateOnly(2026, 9, 16),
            "Container SBOM and provenance",
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Added"] =
                [
                    "GHCR container publication emits an SPDX software bill of materials as an OCI attestation.",
                    "Published images include explicit SLSA/BuildKit provenance alongside source SHA and build timestamp identity.",
                    "The publish workflow records the immutable registry digest for every pushed image."
                ],
                ["Changed"] =
                [
                    "Container publication verifies the pushed digest directly in GHCR and reads both SBOM and provenance back from the registry before succeeding."
                ],
                ["Security"] =
                [
                    "Release consumers can inspect image dependency inventory and build provenance without relying only on mutable tags."
                ]
            }),
        new ProductRelease(
            "0.2.0-preview.1",
            new DateOnly(2026, 9, 16),
            "Historical usage rollups",
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Added"] =
                [
                    "Daily PostgreSQL usage rollups preserve request/token/error accounting after granular request metrics age out.",
                    "Raw request-metric retention and historical rollup retention are independently configurable; defaults are 90 and 730 days.",
                    "Usage reporting transparently combines recent raw metrics with historical rollups without double counting.",
                    "Admin Usage & Governance supports reporting windows up to 730 days and shows whether historical rollups contributed to the result."
                ],
                ["Changed"] =
                [
                    "Usage reporting windows are defined as UTC calendar days so daily historical rollups have deterministic boundaries.",
                    "Request-metric cleanup rolls complete UTC days into durable aggregates before deleting the corresponding raw rows."
                ]
            }),
        new ProductRelease(
            "0.1.0-preview.1",
            new DateOnly(2026, 9, 16),
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
            })
    ];

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
