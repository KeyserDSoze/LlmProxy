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
```

`src/LlmProxy.Admin/package.json` should stay aligned with the product version for the bundled Admin application, but the runtime API/assembly version remains authoritative.

## Version rules

While the product remains below `1.0.0`:

- increment the prerelease/minor line for meaningful new product capabilities or contract changes;
- increment the patch/prerelease sequence for bug fixes and hardening that do not intentionally change public behavior;
- call out incompatible API/configuration behavior explicitly under `Changed` or `Breaking` in the release notes;
- never silently reuse a published version number for different bits.

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
6. add/update tests for version and release-note rendering;
7. run standard CI and any affected Full Stack smoke suites;
8. only after green evidence, record the validated commit/run IDs in `docs/project-status.md` and `docs/development-log.md`;
9. tag/publish the version when the project owner wants a distributable release.

## Build identity

`/api/admin/product` exposes optional `buildRevision` and `builtAtUtc` fields. Deployments may supply:

```text
LLMPROXY_BUILD_SHA
LLMPROXY_BUILD_DATE
```

When available, these identify the exact build without changing the SemVer product version. If no explicit build SHA is supplied, a source revision embedded by the .NET build may be used. The Admin UI displays `local / unknown` when no build revision is available.

## UI

The Admin UI exposes a persistent version badge linking to:

```text
/admin/releases
```

The page shows current version/channel/build identity and the versioned patch notes, so operators can see what changed in the product they are running without reading repository engineering logs.
