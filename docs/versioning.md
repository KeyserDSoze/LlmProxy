# Product versioning and release notes

LlmProxy uses **Semantic Versioning (SemVer)** from the first formal preview baseline onward.

## Current version

```text
0.2.0-preview.1
```

The product remains pre-1.0 while real DGX/Copilot/Entra/Cloudflare acceptance is outside repository CI.

Runtime version/build identity is exposed by:

```http
GET /api/admin/product
GET /healthz
```

The React Admin UI reads version data from the backend and links to `/admin/releases`; it does not hard-code the displayed runtime version.

## Sources of truth

```text
Directory.Build.props                  compiled product version
src/LlmProxy.Admin/package.json        bundled Admin version
src/LlmProxy.Api/Product/              runtime release catalog
CHANGELOG.md                            product changelog
/admin/releases                        operator-visible release history
docker/scripts/validate-release-version.sh
                                        mechanical consistency check
```

The runtime/assembly version is authoritative; Admin package version must stay aligned.

## Mechanical consistency check

Run:

```bash
bash docker/scripts/validate-release-version.sh
```

The validator requires:

- valid SemVer in root `Directory.Build.props`;
- same version in `src/LlmProxy.Admin/package.json`;
- matching release section in `CHANGELOG.md`.

Validate a candidate tag/version with:

```bash
bash docker/scripts/validate-release-version.sh 0.2.0-preview.1
```

A mismatch exits non-zero. Standard CI also exercises an intentionally invalid candidate so the blocking branch is tested before any real Git tag is created.

## Version rules while pre-1.0

- meaningful new capability or intentional public contract change -> new preview/minor line;
- compatible hardening/fix on the same preview line -> advance prerelease/patch sequence;
- incompatible behavior must be explicit under `Changed` or `Breaking`;
- never silently reuse a **tagged/published exact version** for different product bits.

The formal sequence so far is:

```text
0.1.0-preview.1  initial versioned product baseline
0.2.0-preview.1  historical usage rollups + long-window reporting
```

## Release-note categories

Use the categories that apply:

```text
Added
Changed
Fixed
Security
Deprecated
Removed
Breaking
```

Release notes describe product/operator-visible behavior. Engineering detail and exact CI evidence belong in `docs/development-log.md` and `docs/project-status.md`.

## Release checklist

For each product version:

1. choose the new SemVer;
2. update root `Directory.Build.props`;
3. align `src/LlmProxy.Admin/package.json`;
4. add/update `CHANGELOG.md`;
5. update `ProductReleaseCatalog` so API/UI show the same release history;
6. run `docker/scripts/validate-release-version.sh`;
7. add/update backend/frontend release tests;
8. run standard CI and affected Full Stack smokes;
9. record exact green commit/run IDs only after validation;
10. create a matching Git tag only when the project owner wants an immutable distributable release.

## Container build identity

Production images receive:

```text
LLMPROXY_BUILD_SHA
LLMPROXY_BUILD_DATE
```

and OCI labels:

```text
org.opencontainers.image.version
org.opencontainers.image.revision
org.opencontainers.image.created
```

`/api/admin/product` exposes build revision/date, so `/admin/releases` can identify the exact running build without changing SemVer.

Current validated release-engineering checkpoint:

```text
commit        c37479bb474d44f9e36726bebba74cdf38e5661e
CI            35064353402 SUCCESS
Publish GHCR  35064707488 SUCCESS
```

## Container tag rules

A green CI workflow on `main` publishes:

```text
main
sha-<7 chars>
```

A Git tag such as:

```text
v0.2.0-preview.1
```

must match compiled version `0.2.0-preview.1` exactly. A matching prerelease tag publishes:

```text
0.2.0-preview.1
sha-<7 chars>
```

Prereleases intentionally do **not** update stable-looking aliases such as `0.2` or `1.2`.

A stable tag may additionally publish its major/minor alias, for example:

```text
1.2.3
1.2
sha-<7 chars>
```

A docs-only/main commit therefore cannot overwrite an exact version tag: exact version tags are only emitted from matching Git tag events.

## Current release validation

Version `0.2.0-preview.1` product bits are validated by:

```text
commit        5d66c7dcdae42955c6e26849aba84bed4787ff00
CI            35075387110 SUCCESS
Full Stack    35075387186 SUCCESS
Publish GHCR  35075788954 SUCCESS
```

This main publish updates only `main` + `sha-<7>`. The exact `0.2.0-preview.1` tag remains reserved for an explicit matching Git tag.

## Future supply-chain hardening

The next useful release-hardening layer, if pursued, is SBOM/provenance/attestation plus a formal immutable tagged-release workflow. This should complement—not replace—the existing source-SHA/build-date identity and tag/version guardrail.