# Product versioning and release notes

LlmProxy uses **Semantic Versioning (SemVer)** from the first formal preview baseline onward.

## Current version

```text
0.1.0-preview.1
```

The product is intentionally pre-1.0 while DGX/Copilot/Entra/Cloudflare acceptance is still external to repository CI. The current version is compiled into the .NET assemblies through root `Directory.Build.props` and surfaced at runtime by:

```http
GET /api/admin/product
GET /healthz
```

The React Admin UI reads the version from `/api/admin/product`; it does not hard-code the displayed runtime version.

## Sources of truth

```text
Directory.Build.props                  compiled product version
src/LlmProxy.Api/Product/              runtime release catalog / Admin API
CHANGELOG.md                            human-readable product changelog
src/LlmProxy.Admin/src/ReleaseNotesPage.tsx
                                        rendered Admin release-notes experience
docker/scripts/validate-release-version.sh
                                        mechanical version consistency check
```

`src/LlmProxy.Admin/package.json` stays aligned with the product version for the bundled Admin application, while the runtime API/assembly version remains authoritative.

## Mechanical consistency check

Run:

```bash
bash docker/scripts/validate-release-version.sh
```

The validator requires:

- a valid SemVer `<Version>` in root `Directory.Build.props`;
- the same version in `src/LlmProxy.Admin/package.json`;
- a matching release section in `CHANGELOG.md`.

To validate a candidate Git tag as well:

```bash
bash docker/scripts/validate-release-version.sh 0.1.0-preview.1
```

A mismatch exits non-zero. Standard CI exercises both the accepted current version and an intentionally mismatched candidate, so the release-blocking branch is tested before any real tag is pushed.

## Version rules

While the product remains below `1.0.0`:

- increment the prerelease/minor line for meaningful new product capabilities or contract changes;
- increment the patch/prerelease sequence for bug fixes and hardening that do not intentionally change public behavior;
- call out incompatible API/configuration behavior explicitly under `Changed` or `Breaking` in release notes;
- never silently reuse a **published/tagged** version number for different bits.

Before a preview is actually tagged/published, its baseline may still be completed with release-engineering hardening as long as the release notes and validation checkpoint are updated before publication.

After `1.0.0`, standard SemVer compatibility rules apply:

```text
MAJOR  incompatible public contract change
MINOR  backward-compatible feature
PATCH  backward-compatible fix
```

## Required release-note categories

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

Release notes describe product/operator-visible behavior, not every internal refactor. Engineering detail and CI evidence continue to live in `docs/development-log.md` and `docs/project-status.md`.

## Release checklist

For every product version:

1. choose the new SemVer version;
2. update root `Directory.Build.props`;
3. keep `src/LlmProxy.Admin/package.json` aligned;
4. add the release entry to `CHANGELOG.md`;
5. update `ProductReleaseCatalog` so `/api/admin/product` and the UI render the same release notes;
6. run `docker/scripts/validate-release-version.sh`;
7. add/update tests for version and release-note rendering;
8. run standard CI and any affected Full Stack smoke suites;
9. only after green evidence, record the validated commit/run IDs in `docs/project-status.md` and `docs/development-log.md`;
10. tag/publish the version when the project owner wants a distributable release.

## Container build identity

Published/CI production images receive:

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

`/api/admin/product` consumes the build SHA/date environment values, so `/admin/releases` can identify the exact running build without changing SemVer.

The container publishing workflow validates the Git tag against the compiled product version before building a tagged image. A tag such as:

```text
v0.1.0-preview.1
```

must match product version `0.1.0-preview.1` exactly or publication fails.

### Image aliases

For `main` builds:

```text
main
sha-<7 chars>
```

For prerelease tags:

```text
0.1.0-preview.1
sha-<7 chars>
```

For stable tags, the workflow may additionally publish the stable major/minor alias, for example:

```text
1.2.3
1.2
sha-<7 chars>
```

Prereleases intentionally do **not** update `0.1`, `1.2`, or other stable-looking aliases.

## UI

The Admin UI exposes a persistent version badge linking to:

```text
/admin/releases
```

The page shows current version/channel/build identity and versioned patch notes, so operators can see exactly what changed in the product they are running without reading repository engineering logs.
