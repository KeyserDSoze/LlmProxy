# Product versioning and release notes

LlmProxy uses **Semantic Versioning (SemVer)** from the first formal preview baseline onward.

## Current version

```text
0.2.0-preview.6
```

The product remains pre-1.0 while target-host, real DGX/Copilot/Entra/Cloudflare acceptance remains outside repository CI.

Runtime identity is exposed by:

```http
GET /api/admin/product
GET /healthz
```

The React Admin UI reads version data from the backend and links to `/admin/releases`.

## Sources of truth

```text
Directory.Build.props                  compiled product version
src/LlmProxy.Admin/package.json        bundled Admin version
src/LlmProxy.Api/Product/              runtime release catalog
CHANGELOG.md                            product changelog
/admin/releases                        operator-visible release history
docker/scripts/validate-release-version.sh
                                        mechanical version consistency check
docker/scripts/validate-release-main-ci.sh
                                        publication-source CI guard
```

The runtime/assembly version is authoritative; Admin package version must stay aligned.

## Mechanical checks

```bash
bash docker/scripts/validate-release-version.sh
bash docker/scripts/validate-release-version.sh 0.2.0-preview.6
bash docker/scripts/validate-release-main-ci.sh <40-char-source-sha> <workflow-runs.json>
```

The version validator requires valid SemVer in `Directory.Build.props`, the same Admin package version and a matching `CHANGELOG.md` section. CI also exercises an intentionally invalid candidate.

The publication-source guard accepts only a `CI` run whose event is `push`, branch is `main`, `head_sha` matches exactly and conclusion is `success`.

## Version rules while pre-1.0

- meaningful new capability or intentional public/operator contract change -> new preview/minor line;
- compatible hardening/fix on the same preview line -> advance prerelease/patch sequence;
- incompatible behavior must be explicit under `Changed` or `Breaking`;
- never silently reuse a **tagged/published exact version** for different product bits.

Formal sequence:

```text
0.1.0-preview.1  initial versioned product baseline
0.2.0-preview.1  historical usage rollups + long-window reporting
0.2.0-preview.2  GHCR SBOM/provenance + immutable digest verification
0.2.0-preview.3  source-validated main/tag container publication
0.2.0-preview.4  consolidated Linux production deployment + host installer
0.2.0-preview.5  production environment acceptance evidence
0.2.0-preview.6  Entra-owned personal API keys + user self-service
```

`0.2.0-preview.6` adds the Entra user role and personal API-key ownership/self-service contract. Until its implementation CI/publication evidence is recorded, the previous `0.2.0-preview.5` runtime checkpoint below remains the last validated registry baseline.

`0.2.0-preview.5` owns the environment-acceptance operator contract: target-host/DGX/gateway probes, metadata-only evidence, canonical bodyless vLLM `/health` handling and repository-supported acceptance automation. Subsequent handover/documentation wiring on this preview line does not create an immutable exact release and does not change inference semantics.

## Release-note categories

```text
Added
Changed
Fixed
Security
Deprecated
Removed
Breaking
```

Release notes describe product/operator-visible behavior. Engineering detail and exact evidence belong in `docs/development-log.md` and `docs/project-status.md`.

## Release checklist

For each product version:

1. choose the new SemVer;
2. update `Directory.Build.props`;
3. align `src/LlmProxy.Admin/package.json`;
4. update `CHANGELOG.md`;
5. update `ProductReleaseCatalog` and release UI/tests;
6. run the mechanical version validator;
7. run standard CI and affected Full Stack smokes;
8. for any container publication, require successful Actions-API source-CI validation before GHCR login;
9. require post-push digest/SBOM/provenance verification;
10. record exact green source/run/digest evidence only after validation;
11. update canonical handover docs;
12. create a matching Git tag/GitHub Release only when the project owner explicitly wants an immutable distributable release.

## Build identity

Production images receive:

```text
LLMPROXY_BUILD_SHA
LLMPROXY_BUILD_DATE
org.opencontainers.image.version
org.opencontainers.image.revision
org.opencontainers.image.created
```

`/api/admin/product` exposes build revision/date, so operators can identify the exact running build without changing SemVer.

## Container tag and source-validation rules

A validated `main` SHA publishes:

```text
main
sha-<7 chars>
```

A Git tag such as:

```text
v0.2.0-preview.5
```

must satisfy both conditions before publication:

1. tag version exactly matches compiled version `0.2.0-preview.5`;
2. the tagged source SHA already has successful repository `CI` from a push to `main`.

A matching prerelease tag may publish:

```text
0.2.0-preview.5
sha-<7 chars>
```

Prereleases do not update stable-looking aliases. A stable tag may additionally publish a major/minor alias.

The same Actions-API source gate is used for ordinary main and Git-tag publications. For a `workflow_run`-triggered publication, the API-selected CI run ID must equal the triggering CI run ID. The gate runs before GHCR login.

Docs/operator-only commits on `main` may republish mutable `main` and a new `sha-<7>` alias, but cannot overwrite an exact SemVer image because exact version tags are emitted only from matching Git tag events. No exact `v0.2.0-preview.5` tag has been created.

## OCI SBOM and provenance contract

Every publication produced by `.github/workflows/container.yml` includes registry-native supply-chain evidence.

Buildx:

```text
sbom: true
provenance: mode=max
```

The post-push gate requires an immutable digest, reads the pushed OCI index back from GHCR, resolves attestation manifests, verifies in-toto layers, verifies `https://spdx.dev/Document`, verifies an SLSA provenance predicate and uploads `release-manifest.json`.

Validated `0.2.0-preview.5` runtime evidence:

```text
commit                723c47d919a59cf95e447c071ef377ab92a06498
CI                    35099356925 SUCCESS
Publish GHCR          35099987458 SUCCESS
image digest          sha256:7b24e16d264c78eb9c6affa8eadf207c756d883799c8e0503b128ef4004ac1fa
attestation manifest  sha256:b15e45a4024235fd2ba28c6a7711ab64922da4be4003d68b8f7ec0eb78db7712
SBOM predicate        https://spdx.dev/Document
provenance predicate  https://slsa.dev/provenance/v1
release artifact      10448046779
artifact digest       sha256:c90c6ae1db7246afe34f3764543d0ec4a20eed7c6026cf8030e86cc55220562c
validating CI run     35099356925
```

For production acceptance/deployment, `sha-723c47d` remains the immutable image alias for this validated runtime checkpoint even if later documentation/operator commits move mutable `main`.

## Current release state

Repository-supported release gating is complete for the current MVP: source validation before GHCR, version/tag consistency, immutable digest capture and post-push SBOM/provenance verification are implemented and validated.

Creating an actual immutable Git tag/GitHub Release is an explicit product-owner publication decision, not an unfinished engineering prerequisite.
