# Password-protected single-command first install

Every validated immutable release builds the protected **test** deployment
assets in the **Publish container** reusable workflow job. The job uses the
GitHub Actions environment `test`; ensure it exists **before** releasing.

Go to **GitHub → Settings → Environments → test** and create:

| GitHub Environment setting | Type | Name | Value |
|---|---|---|---|
| Variable | Variable | `ENTRA_TENANT_ID` | Microsoft Entra directory ID |
| Variable | Variable | `ENTRA_CLIENT_ID` | Microsoft Entra application/client ID |
| Variable | Variable | `ENTRA_SUPER_ADMINS` | Comma-separated admin email/UPN identifiers |
| Secret | Secret | `ENTRA_CLIENT_SECRET` | Fresh Microsoft Entra client secret |
| Secret | Secret | `CLOUDFLARE_TUNNEL_TOKEN` | Fresh token for the Cloudflare tunnel |
| Secret | Secret | `password` | New, high-entropy install passphrase **at least 20 characters** |

`ENTRA_ENABLED=true` is fixed in the generated installer. The private keys are
injected only into the asset encryption step through Actions step environment,
never into Git or the Docker image. Release publication **fails closed** if
required values are missing: no incomplete unprotected installer is published.
Environment approval rules may delay release publication, by design.
Rotate any previously posted or exposed secret before using this feature.

Each release embeds its **exact** version into the two published public assets:

- `test.sh`: unencrypted **credential-free** launcher. Fetches ciphertext,
  validates its SHA-256, prompts interactively via `/dev/tty`, decrypts with
  GnuPG, checks shell syntax and executes the decrypted script.
- `test.install.sh.gpg`: password-encrypted installation script containing the
  pinned version and necessary credentials. GnuPG uses AES-256 with
  iterated SHA-512 S2K and a modification-detection check (MDC).
- `test.sh.sha256` and `test.install.sh.gpg.sha256`: checksums.

On a new Linux server (root/sudo access, outbound GitHub/Cloudflare network),
run exactly:

```bash
curl -fsSL https://github.com/KeyserDSoze/LlmProxy/releases/latest/download/test.sh | bash
```

The launcher asks `Password for environment test:` without echoing it.
It downloads ciphertext **from the version embedded by that release**, verifies
its checksum, decrypts to a root/user-private temporary folder, downloads and
verifies the **same pinned version** of the official bootstrap script, and
performs the non-interactive Linux install. Installation creates and enables
`llmproxy.service` under systemd and runs Cloudflare Tunnel in Compose.

The plain bootstrap command remains available without environment settings.
Do **not** use this public launcher to convey a short or reused password:
a publicly downloadable encrypted asset enables offline password guessing.
The GitHub sha256 file detects transport damage, but is **not a digital signature**
and does not defend against an attacker who can replace both release assets.
Limit who can publish releases and protect the `test` GitHub environment.

The decrypted install script and password exist briefly in process memory
and a `0700` temporary directory on the destination host. Root can always
read running processes; on an untrusted host encryption at rest is not
a substitute for host security.

To refresh credentials **or** the install password, update environment secrets,
publish a **new** version, and never overwrite older immutable release assets.
Older publicly available encrypted payloads remain decryptable with their
original password until GitHub removes those releases.
