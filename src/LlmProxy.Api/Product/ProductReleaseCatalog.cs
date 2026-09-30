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

        var historicalReleases = BuildReleases();
        var matchingRelease = historicalReleases.FirstOrDefault(release => release.Version == version);
        var currentRelease = matchingRelease ?? BuildAutomatedRelease(version, builtAtUtc);
        IReadOnlyList<ProductRelease> releases = matchingRelease is null
            ? new[] { currentRelease }.Concat(historicalReleases).ToArray()
            : historicalReleases;

        return new ProductReleaseInfo(
            "LlmProxy",
            version,
            version.Contains('-', StringComparison.Ordinal) ? "preview" : "stable",
            currentRelease.ReleasedOn,
            buildRevision,
            builtAtUtc,
            releases);
    }

    private static ProductRelease BuildAutomatedRelease(string version, DateTimeOffset? builtAtUtc) =>
        new(
            version,
            DateOnly.FromDateTime((builtAtUtc ?? DateTimeOffset.UtcNow).UtcDateTime),
            "Automated immutable main release",
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Changed"] =
                [
                    "This immutable distribution release was generated automatically from a validated main commit."
                ],
                ["Security"] =
                [
                    "Publication occurs only after the source commit completes the repository CI gate, including the distributed full-stack acceptance suite."
                ]
            });

    private static IReadOnlyList<ProductRelease> BuildReleases() =>
    [
        new ProductRelease(
            "0.2.0-preview.8",
            new DateOnly(2026, 9, 30),
            "Release-based Linux distribution and ARM64 publication",
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Added"] =
                [
                    "Immutable GitHub Releases can carry a checksummed Linux operator bundle with the production Compose assets, observability configuration and versioned installer tooling.",
                    "The llmproxyctl command provides status, health, logs, lifecycle, doctor, explicit update and local rollback operations while preserving host-owned configuration and Docker volumes.",
                    "An owner-triggered GitHub Actions workflow creates an immutable version tag only after the exact main SHA has successful CI and Full stack smoke evidence.",
                    "Tagged container publication targets both linux/amd64 and linux/arm64 so the same release can run on conventional Linux hosts and NVIDIA DGX Spark-class ARM64 systems.",
                    "Inference nodes can store a write-only upstream bearer credential encrypted with AES-GCM; health, model discovery, metrics, maintenance warm-up and inference use that credential without forwarding the client-facing LlmProxy API key.",
                    "Same-host llama.cpp/vLLM installation validates host.docker.internal through Docker's bridge gateway so a loopback-only runtime fails before deployment with an actionable bind-address error."
                ],
                ["Changed"] =
                [
                    "A tagged publication can create the matching GitHub Release and attach the Linux bundle, bootstrap script, SHA-256 checksums and existing release-manifest evidence.",
                    "Release installation no longer requires a long-lived Git checkout; the existing Linux installer remains the privileged host/deployment implementation inside each immutable bundle."
                ],
                ["Security"] =
                [
                    "Release bundles are SHA-256 verified before privileged installation and exact release image tags cannot be overridden by installer arguments.",
                    "Upstream bearer plaintext is never returned by node APIs or persisted in PostgreSQL/Redis; only AES-GCM ciphertext is durable/runtime state and the stable encryption key remains an external recovery secret.",
                    "Private-repository download credentials remain external to the LlmProxy application environment and exact release tags are refused when they already exist."
                ]
            }),
        new ProductRelease(
            "0.2.0-preview.7",
            new DateOnly(2026, 9, 21),
            "Aggregated Entra user request quotas",
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Added"] =
                [
                    "Administrators can configure request-rate policies for an Entra user across all personal API keys, optionally scoped to one logical model.",
                    "The personal user portal exposes effective aggregate request-limit metadata in read-only form.",
                    "User-level policies use the existing PostgreSQL transactional outbox, Redis shared runtime state and local L1 configuration path."
                ],
                ["Changed"] =
                [
                    "Request admission evaluates applicable user and credential policies together with AND semantics and atomically increments their fixed-window counters only when every applicable policy permits the request."
                ],
                ["Security"] =
                [
                    "User quota scope is keyed by stable Entra tenant and object identifiers; mutable usernames remain display metadata only.",
                    "Monetary spend limits remain intentionally unsupported until an explicit pricing or chargeback model is configured."
                ]
            }),
        new ProductRelease(
            "0.2.0-preview.6",
            new DateOnly(2026, 9, 21),
            "Entra-owned personal API keys",
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Added"] =
                [
                    "Users with the LlmProxy.User Entra application role can create, list, rotate and revoke multiple personal inference API keys through a self-service /api/me contract.",
                    "Personal API keys are bound to the creator's stable Entra tenant and object identifiers, while existing administrator-created credentials remain supported as service credentials.",
                    "Self-service usage reporting attributes request and token metadata to the current user through the existing credential-level telemetry and historical rollups.",
                    "Administrators can inspect personal-key ownership and user credential summaries through dedicated identity administration endpoints.",
                    "The React control plane exposes a My API Keys portal for normal Entra users without loading administrative APIs."
                ],
                ["Changed"] =
                [
                    "The Entra authorization model now distinguishes LlmProxy.User self-service access from LlmProxy.Reader operational read access and LlmProxy.Admin administration.",
                    "Credential runtime snapshots carry stable owner identity so key-to-user attribution follows normal local and Redis runtime-state publication."
                ],
                ["Security"] =
                [
                    "Credential ownership authorization uses immutable Entra tid and oid claims rather than mutable usernames or email addresses.",
                    "Raw personal API keys are returned only once at creation or rotation and continue to be persisted only as HMAC hashes plus non-secret metadata."
                ]
            }),
        new ProductRelease(
            "0.2.0-preview.5",
            new DateOnly(2026, 9, 16),
            "Production environment acceptance evidence",
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Added"] =
                [
                    "A production environment acceptance command validates the actual Linux host, VM-to-DGX connectivity and deployed OpenAI-compatible surfaces after installation.",
                    "Acceptance evidence records metadata-only PASS/FAIL, HTTP status, content type and timing for direct DGX and gateway Models, Chat, Responses and SSE probes.",
                    "A dedicated acceptance runbook defines pass criteria and the remaining benchmark, Entra, Cloudflare, Copilot and deployment-runner follow-on work."
                ],
                ["Changed"] =
                [
                    "Target-host acceptance is now an executable, repeatable evidence step rather than only a manual checklist."
                ],
                ["Fixed"] =
                [
                    "DGX health acceptance validates the canonical vLLM status-only /health response without incorrectly requiring a JSON body or content type."
                ],
                ["Security"] =
                [
                    "Acceptance evidence excludes prompts, request and response bodies, generated model output and API secrets, and the script rejects generated evidence if a supplied bearer secret appears in it."
                ]
            }),
        new ProductRelease(
            "0.2.0-preview.4",
            new DateOnly(2026, 9, 16),
            "Consolidated Linux production deployment",
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Added"] =
                [
                    "Linux production deployment now has one documented full-stack path with PostgreSQL, Redis and bundled observability.",
                    "A cross-distribution Linux host installer can install or preserve Docker Engine and Compose v2, prepare /opt/llmproxy, generate initial secrets, validate DGX connectivity and run the canonical full-stack deployment.",
                    "A production environment template and end-to-end Linux runbook cover private bootstrap, DGX connectivity, Entra/public exposure, backup, update and rollback.",
                    "Production deployment stages runtime Compose and observability assets under /opt/llmproxy/runtime so containers do not depend on a transient runner workspace.",
                    "Cloudflare Tunnel can be enabled as an optional Compose profile when a tunnel token is configured."
                ],
                ["Changed"] =
                [
                    "The production deploy script and GitHub Actions deploy workflow now use the Redis-enabled full stack instead of the legacy minimal Compose overlay.",
                    "Production deployment validates Compose configuration and requires both liveness and readiness checks before reporting success."
                ],
                ["Security"] =
                [
                    "Grafana can bind to loopback independently from the gateway, generated installer secrets are not printed, and public-tunnel guidance requires Entra protection before exposing administrative surfaces."
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
