# Encrypted per-environment installation and update

Every immutable LLMProxy release publishes password-protected **test**
initial-install and update scripts, using the **test** GitHub Actions
Environment. Configure the Environment *before* publication.

Go to **GitHub → Settings → Environments → test → Environment secrets**.
All six values are **Secrets** (do not create any Environment Variables):

| GitHub Environment Secret name | Value |
|---|---|
| `ENTRA_TENANT_ID` | Microsoft Entra directory/tenant ID |
| `ENTRA_CLIENT_ID` | Microsoft Entra application/client ID |
| `ENTRA_SUPER_ADMINS` | Admin UPN/emails separated by commas **or** semicolons; `oid:GUID` also supported |
| `ENTRA_CLIENT_SECRET` | Rotated Microsoft Entra application Client Secret |
| `CLOUDFLARE_TUNNEL_TOKEN` | Rotated dedicated Cloudflare Tunnel connector token |
| `PASSWORD` | Any non-empty install/update decryption password; one character is accepted |

The same `PASSWORD` decrypts both scripts for the **same release**.
`ENTRA_ENABLED=true` is built in. Version comes from the immutable release
tag. The release job refuses publication if a required Secret is missing.
**Never commit or paste real credential values into logs or source code.**
Rotate any credential pasted into chat previously before publication.

### New installation (one Linux command)

```bash
curl -fsSL https://github.com/KeyserDSoze/LlmProxy/releases/latest/download/test.sh | bash
```

Prompts for the password interactively using `/dev/tty`, decrypts the
release-pinned encrypted script, then presents a **first-install-only port
selection** for Grafana (default TCP host port `3000`) and the LLMProxy
gateway (default TCP host port `8080`). **Enter** accepts the displayed
default; type another available port to use it instead. Port conflicts
are reported before deployment. On an interrupted first install, previously
chosen values in `/opt/llmproxy/.env` become the new defaults.
Already-installed hosts and the update command never prompt and retain
their current ports. Port selections are explicitly handed across `sudo`
and written to the persistent private environment file.

Changing only the **host** LLMProxy port does **not** change the container's
internal port: the bundled Cloudflare Tunnel normally still connects to
`http://llmproxy:8080`. Only change Cloudflare's origin if you have explicitly
configured it to connect through the host port instead.

The bootstrap installs just the **LLMProxy control plane** (gateway/API,
Admin UI, PostgreSQL, Redis, Grafana and observability, and optionally the
Cloudflare Tunnel). It does **not** install inference engines, any LLM model
weights, or a GPU runtime. With zero GPU/LLM hosts configured it sets
`BOOTSTRAP_ENABLED=false`, starts with an empty model catalog and lets
administrators later pair Linux hardware and deploy models from Admin.
The installer verifies the pinned official bootstrap before executing it.

### Safe update of an existing installation (one Linux command)

```bash
curl -fsSL https://github.com/KeyserDSoze/LlmProxy/releases/latest/download/test-update.sh | bash
```

Prompts for the **same GitHub Environment Secret `PASSWORD` that was used
when publishing the latest release**. Decrypts the update payload and runs
`sudo -E llmproxyctl update <release-version>` with the five deployment
values exported. The built-in updater resolves and applies **all published
intermediate releases**, runs migrations, preserves PostgreSQL/Redis volumes,
model inventory and the existing application configuration, and writes changed
Entra ID, Super Admin and Cloudflare Tunnel settings to the existing private
`/opt/llmproxy/.env` through the normal Linux installer. Compose recreates
affected containers; the Cloudflare service remains isolated to LLMProxy's own
Compose project. The Node Agent/other Cloudflare tunnels are not removed.

**Updating to an already installed release is supported**: invoking the
update command with the same version re-applies its pinned environment
Secrets and recreates containers, without skipping future migrations.

**Secrets in published releases are immutable snapshots.** Changing GitHub
Environment Secrets alone does *not* update any installed server or retroactively
alter the latest GitHub Release. After changing any Secret, publish a **new
version**, then run the update command. Old encrypted assets remain publicly
available and decryptable with their own original password; rotate leaked
tokens at their upstream providers and remove old release assets if required.

### Published assets per release

- `test.sh`, `test.sh.sha256`: publicly downloadable first-install launcher
- `test.install.sh.gpg`, `test.install.sh.gpg.sha256`: AES-256 encrypted first-install payload
- `test-update.sh`, `test-update.sh.sha256`: publicly downloadable update launcher
- `test-update.install.sh.gpg`, `test-update.install.sh.gpg.sha256`: AES-256 encrypted update payload

The launcher validates the ciphertext checksum, decrypts into a user-private
temporary directory, checks Bash syntax and executes it. It never prints the
password. GnuPG uses AES-256 and iterated SHA-512 S2K with MDC; an incorrect
password fails before executing decrypted code. Checksums detect corruption
but **are not independent digital signatures**. Restrict GitHub release
write access and protect the `test` environment. **Short or predictable passwords
are insecure:** the public ciphertext permits offline password guessing and
may expose Entra and Cloudflare credentials. A long, random passphrase is
strongly recommended even though it is no longer mandatory.
Protect the Linux host: privileged processes can access decrypted script
contents and the resulting `0600` application environment file.
