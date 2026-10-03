# Product versioning and immutable release train

LlmProxy has two related version identities:

1. **source-history version** — the legacy/manual feature-history value in `Directory.Build.props`, the Admin package and `CHANGELOG.md`;
2. **distribution release version** — the immutable SemVer assigned automatically to every validated push on `main`.

The distribution train starts at:

```text
0.0.1
```

and is the version operators install, update, roll back and see in packaged runtime build identity.

## Automatic release rule

Every push to `main` runs the complete `CI` workflow. That workflow contains:

- backend/unit/benchmark tests;
- frontend build, Vitest and Playwright;
- Docker/PostgreSQL integration suites;
- protected-upstream credential smoke;
- backup/restore validation;
- the distributed Redis/OpenTelemetry/full-stack gate.

If and only if that entire CI run succeeds, `.github/workflows/release.yml` automatically allocates one immutable release tag for that exact source SHA.

There is no manual **Run workflow** step and no bot commit that edits version files.

### One-time repository credential

GitHub's built-in `GITHUB_TOKEN` cannot be granted the repository **Workflows: write** permission. GitHub rejects creation of a tag/release that targets a commit which adds or changes workflow files when that permission is absent.

Configure one repository Actions secret once:

```text
RELEASE_TOKEN
```

Recommended credential: a fine-grained PAT restricted to this repository with:

```text
Contents   Read and write
Workflows  Read and write
```

The token is used only for immutable Git tag / GitHub Release operations. GHCR publication continues to use the short-lived `GITHUB_TOKEN` with package permissions.

The file `distribution/AUTOMATIC_RELEASE_SERIES` marks the first commit eligible for the generated release train. Commits before that marker are migration history and are skipped, so the first eligible green commit is `v0.0.1`.

Default increment:

```text
v0.0.1
v0.0.2
v0.0.3
...
```

The allocator reads existing stable `vMAJOR.MINOR.PATCH` tags and chooses the next unused version. Concurrent successful pushes race safely: tag creation is atomic and a loser refetches tags and retries with the next version.

## Choosing patch, minor or major

A validated `main` commit defaults to a **patch** increment.

To intentionally change the release line, include exactly one marker anywhere in the final commit message:

```text
release:patch
release:minor
release:major
```

Examples:

```text
feat: add provider pools release:minor
feat!: redesign public API release:major
fix: handle empty stream release:patch
```

If no marker is present, patch is used. More than one marker is rejected.

## No release on a red commit

A failed CI run creates:

- no version tag;
- no exact-version GHCR image;
- no GitHub Release.

Fix the problem and push a new commit to `main`; that successful push receives the next immutable version.

## One release per source SHA

Before allocating a version, the release workflow checks whether the source SHA already has a stable `vX.Y.Z` tag.

If it does, the workflow reuses that version instead of creating another version for the same bits. This makes workflow reruns idempotent at the Git tag layer.

Exact version tags are never moved or reused.

## Build/version injection

Source-history metadata is still mechanically checked by:

```bash
bash docker/scripts/validate-release-version.sh
```

A generated distribution version is validated with:

```bash
bash docker/scripts/validate-release-version.sh 0.0.1
```

The generated version does **not** need to equal the source-history version. During the production Docker build it is injected into the .NET assembly with `Version` / `InformationalVersion`, and into OCI labels.

Therefore a packaged `v0.0.7` runtime reports `0.0.7` through the product API even when the source-history baseline remains on the earlier preview-history line.

## Publication flow

```text
push main
   |
   v
CI (all gates, including distributed full stack)
   |
   | success
   v
automatic release workflow
   |
   +--> choose next SemVer
   +--> create immutable vX.Y.Z tag
   |
   v
reusable publication workflow
   |
   +--> verify exact source CI
   +--> verify tag -> source SHA
   +--> refuse existing exact GitHub Release/image tag
   +--> build linux/amd64 + linux/arm64
   +--> publish exact, major.minor, latest, main and sha-* aliases
   +--> verify OCI digest
   +--> verify SPDX SBOM
   +--> verify SLSA/BuildKit provenance
   +--> build checksummed Linux operator bundle
   +--> create immutable GitHub Release
```

The release workflow calls the publication workflow directly. It does not rely on a tag push made with `GITHUB_TOKEN` to trigger another workflow.

## Published container aliases

For release `0.0.7` from source `abcdef1...`:

```text
ghcr.io/keyserdsoze/llmproxy:0.0.7    exact immutable release
ghcr.io/keyserdsoze/llmproxy:0.0      moving major/minor alias
ghcr.io/keyserdsoze/llmproxy:latest   latest validated release
ghcr.io/keyserdsoze/llmproxy:main     latest validated main release
ghcr.io/keyserdsoze/llmproxy:sha-abcdef1
```

Deployment/update automation should prefer the exact version. The moving aliases are convenience/discovery aliases.

## Release assets

Every immutable GitHub Release contains:

- `llmproxy-<version>-linux.tar.gz`;
- SHA-256 checksum for the bundle;
- `llmproxy-bootstrap.sh`;
- SHA-256 checksum for the bootstrap;
- `llmproxy-update-plan.json` and its SHA-256 checksum, declaring the standard/custom host update contract and operator command;
- self-contained Node Agent release archives for x86_64 and ARM64;
- `release-manifest.json` with image/source/digest/SBOM/provenance evidence.

The Linux operator bundle also embeds the self-contained LlmProxy Update Agent for x86_64 and ARM64. Selecting a later target release through Admin update orchestration applies every intervening stable release in ascending order, so release-specific custom migrations are never skipped.

The application image index contains both:

```text
linux/amd64
linux/arm64
```

## Source-history line

The repository has historical feature baselines such as:

```text
0.1.0-preview.1
0.2.0-preview.1
...
0.2.0-preview.8
```

Those entries remain in `CHANGELOG.md` and the built-in release history because they document how the product evolved before automatic distribution releases were introduced. They are no longer the counter used to mint new GitHub Releases.

## Runtime identity

Runtime identity is exposed through:

```http
GET /api/admin/product
GET /healthz
```

Production images also carry:

```text
LLMPROXY_BUILD_SHA
LLMPROXY_BUILD_DATE
org.opencontainers.image.version
org.opencontainers.image.revision
org.opencontainers.image.created
```

For generated distribution versions that do not exist in the legacy source-history catalog, the product API synthesizes an `Automated immutable main release` entry while retaining the older feature-history entries.
