# Product versioning and release notes

LlmProxy uses **Semantic Versioning (SemVer)** from the first formal preview baseline onward.

## Current version

```text
0.2.0-preview.3
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
                                        mechanical version consistency check
docker/scripts/validate-release-main-ci.sh
                                        publication-source CI guard
```

The runtime/assembly version is authoritative; Admin package version must stay aligned.

## Mechanical consistency checks

Run:

```bash
bash docker/scripts/validate-release-version.sh
```

The version validator requires valid SemVer in root `Directory.Build.props`, the same version in `src/LlmProxy.Admin/package.json`, and a matching release section in `CHANGELOG.md`.

Validate a candidate tag/version with:

```bash
bash docker/scripts/validate-release-version.sh 0.2.0-preview.3
```

A mismatch exits non-zero. Standard CI also exercises an intentionally invalid candidate so the blocking branch is tested before any real Git tag is created.

The publication-source guard is:

```bash
bash docker/scripts/validate-release-main-ci.sh <40-char-source-sha> <workflow-runs.json>
```

It accepts only a `CI` run whose event is `push`, branch is `main`, `head_sha` exactly matches the publication source and conclusion is `success`. CI includes positive and negative fixtures for wrong branch, failed CI and wrong SHA without creating a real immutable tag.

## Version rules while pre-1.0

- meaningful new capability or intentional public contract change -> new preview/minor line;
- compatible hardening/fix on the same preview line -> advance prerelease/patch sequence;
- incompatible behavior must be explicit under `Changed` or `Breaking`;
- never silently reuse a **tagged/published exact version** for different product bits.

The formal sequence so far is:

```text
0.1.0-preview.1  initial versioned product baseline
0.2.0-preview.1  historical usage rollups + long-window reporting
0.2.0-preview.2  GHCR SBOM/provenance + immutable digest verification
0.2.0-preview.3  source-validated main/tag container publication
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
9. for any container publication, query Actions and require successful `CI` push/main evidence for the exact source SHA before GHCR login;
10. require successful post-push digest/SBOM/provenance verification;
11. record exact green commit/run IDs only after validation;
12. create a matching Git tag/GitHub Release only when the project owner wants an immutable distributable release.

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

## Container tag and source-validation rules

A validated `main` SHA publishes:

```text
main
sha-<7 chars>
```

A Git tag such as:

```text
v0.2.0-preview.3
```

must satisfy **both** conditions before publication:

1. tag version matches compiled version `0.2.0-preview.3` exactly;
2. the tagged source SHA already has a successful repository `CI` run produced by a push to `main`.

A matching prerelease tag may then publish:

```text
0.2.0-preview.3
sha-<7 chars>
```

Prereleases intentionally do **not** update stable-looking aliases such as `0.2` or `1.2`.

A stable tag may additionally publish its major/minor alias, for example:

```text
1.2.3
1.2
sha-<7 chars>
```

The same Actions-API source gate is used for ordinary main publications and Git-tag publications. For a `workflow_run`-triggered main publication, the API-selected CI run ID must equal the workflow run that triggered publication. This makes the real main publish exercise the same API/JSON/guard path that a future tag uses.

A docs-only/main commit cannot overwrite an exact version tag: exact version tags are only emitted from matching Git tag events. No exact `0.2.0-preview.3` Git tag has been created yet.

## OCI SBOM and provenance contract

Every publication produced by `.github/workflows/container.yml` includes registry-native supply-chain evidence.

Buildx runs with:

```text
sbom: true
provenance: mode=max
```

The post-push gate:

1. requires a valid immutable `sha256:<64>` digest from Buildx;
2. reads `IMAGE@DIGEST` back from GHCR as raw OCI JSON;
3. requires an OCI image index with at least one runnable image manifest;
4. finds descriptors whose annotation `vnd.docker.reference.type` is `attestation-manifest`;
5. resolves every attestation manifest by digest;
6. requires `application/vnd.in-toto+json` layers;
7. verifies an SPDX predicate exactly equal to `https://spdx.dev/Document`;
8. verifies an SLSA predicate beginning with `https://slsa.dev/provenance/`;
9. when the OCI manifest includes a `subject`, verifies that subject is one of the runnable image manifests in the root index;
10. writes and uploads `release-manifest.json`.

The release manifest records:

```text
image
digest
version
sourceSha
builtAtUtc
validatingCiRunId
sbom
provenance
attestations[] { manifestDigest, predicateType }
```

Validated `0.2.0-preview.3` evidence:

```text
commit                e9c8805e8473d3ad4df118d6a623ccef08723761
CI                    35083646699 SUCCESS
Publish GHCR          35084132389 SUCCESS
image digest          sha256:6a7d082ef05d86851926beaf876b933de0fab96255ec25f3ad5ee84a7ac414ec
attestation manifest  sha256:6d60bd6cb26cce447e403081ae1aa6129920f2716a0a1ccfb579b196054997a9
SBOM predicate        https://spdx.dev/Document
provenance predicate  https://slsa.dev/provenance/v1
release artifact      10441770750
artifact digest       sha256:5315122e2df9238702655332273e48744a08b895c2fcca25cbe7dcd6ff42d13d
validating CI run     35083646699
```

The first `preview.2` verifier used Buildx convenience rendering for provenance and failed even though BuildKit had generated and pushed the attestation. The canonical verifier follows OCI-native descriptors and predicate annotations instead.

## Current release validation

Version `0.2.0-preview.3` product/release bits are validated by:

```text
commit        e9c8805e8473d3ad4df118d6a623ccef08723761
CI            35083646699 SUCCESS
Publish GHCR  35084132389 SUCCESS
```

The latest relevant distributed-runtime Full Stack remains `35075387186 SUCCESS`, because `0.2.0-preview.3` changes release engineering rather than runtime behavior.

This main publish updates only `main` + `sha-<7>`. An exact `0.2.0-preview.3` tag remains reserved for an explicit matching Git tag that points at previously validated `main` source.

## Next release action

Repository-supported release gating is complete for the current MVP: source validation before GHCR, version/tag consistency, immutable digest capture and post-push SBOM/provenance verification are all implemented and validated. Creating an actual immutable Git tag and GitHub Release is now an explicit product-owner publication decision, not an unfinished engineering prerequisite.