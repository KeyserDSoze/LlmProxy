# Product versioning and release notes

LlmProxy uses **Semantic Versioning (SemVer)** from the first formal preview baseline onward.

## Current version

```text
0.2.0-preview.2
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

The validator requires valid SemVer in root `Directory.Build.props`, the same version in `src/LlmProxy.Admin/package.json`, and a matching release section in `CHANGELOG.md`.

Validate a candidate tag/version with:

```bash
bash docker/scripts/validate-release-version.sh 0.2.0-preview.2
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
0.2.0-preview.2  GHCR SBOM/provenance + immutable digest verification
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
9. for any container publication, require successful post-push digest/SBOM/provenance verification;
10. record exact green commit/run IDs only after validation;
11. create a matching Git tag only when the project owner wants an immutable distributable release.

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

## Container tag rules

A green CI workflow on `main` publishes:

```text
main
sha-<7 chars>
```

A Git tag such as:

```text
v0.2.0-preview.2
```

must match compiled version `0.2.0-preview.2` exactly. A matching prerelease tag publishes:

```text
0.2.0-preview.2
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

## OCI SBOM and provenance contract

`0.2.0-preview.2` adds registry-native supply-chain evidence to every publication produced by `.github/workflows/container.yml`.

Buildx runs with:

```text
sbom: true
provenance: mode=max
```

The publication gate does not trust mutable tags or only the local build result. After push it:

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
sbom
provenance
attestations[] { manifestDigest, predicateType }
```

Validated `0.2.0-preview.2` evidence:

```text
commit                d134603672f361474bac9ea330f3bd1a142b5dfa
CI                    35080118201 SUCCESS
Publish GHCR          35080565404 SUCCESS
image digest          sha256:cc26617a5860e126957da2d0e59c8cd8accd1cd3280576d991819ddc2001d880
attestation manifest  sha256:83457ab3eb69c4aac638874daed1f2cf396fa157001f2be9257e48d0d067253d
SBOM predicate        https://spdx.dev/Document
provenance predicate  https://slsa.dev/provenance/v1
release artifact      10440082178
artifact digest       sha256:149071900ed52b2ffc471281c639f1c9264b40c40bb9729172d47411f12a35a6
```

The first verifier used Buildx convenience rendering for provenance and failed even though BuildKit had generated and pushed the attestation. The final verifier intentionally follows OCI-native descriptors and predicate annotations instead; this is the canonical release gate.

## Current release validation

Version `0.2.0-preview.2` product/release bits are validated by:

```text
commit        d134603672f361474bac9ea330f3bd1a142b5dfa
CI            35080118201 SUCCESS
Publish GHCR  35080565404 SUCCESS
```

The latest relevant distributed-runtime Full Stack remains `35075387186 SUCCESS`, because `0.2.0-preview.2` changes release engineering rather than runtime behavior.

This main publish updates only `main` + `sha-<7>`. The exact `0.2.0-preview.2` tag remains reserved for an explicit matching Git tag.

## Next release-hardening increment

The next useful internal release-engineering step is **not** more SBOM/provenance work. It is to make the immutable exact-SemVer tag path consume an equivalent validation gate before publication, so a versioned Git tag cannot bypass CI just because it was pushed directly. A GitHub Release may then be created only after the validated tag publication succeeds.