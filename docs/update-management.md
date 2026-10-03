# Administrator update management

LlmProxy can manage its own control-plane upgrades from **Release Notes & Updates** without executing the update inside the gateway container.

## Architecture

```text
Admin browser
    |
    v
LlmProxy Admin API
    |
    | bearer-authenticated private host call
    v
LlmProxy Update Agent :9910 on Docker bridge
    |
    | starts the immutable release bootstrap
    v
/opt/llmproxy + Docker Compose
```

The Update Agent is a systemd service on the Linux control-plane host. It is deliberately separate from the LlmProxy container: replacing or restarting the gateway during deployment must not terminate the process performing the update. On supported Linux installations without systemd, the core LlmProxy install remains valid and manual `llmproxyctl update` remains available, but Admin scheduling/update-now controls report the host agent as unavailable.

The release installer generates a random Update Agent bearer, stores the agent copy in `/etc/llmproxy/update-agent.env`, and writes the gateway-side copy into protected `/opt/llmproxy/.env`. The agent listens on the Docker bridge address rather than all LAN interfaces.

## Administrator workflow

The Release Notes page shows:

- the currently running LlmProxy version;
- published stable GitHub Releases newer than the installed version;
- release publication time and release link;
- whether the release uses the standard or custom update plan;
- the operator command published with the release;
- whether a host restart is declared;
- the active scheduled/running update and recent job history.

Administrators with write access can choose **Update now**, select a local date/time for a one-off schedule, or persist an **automatic update policy**:

- **Manual** — only explicit administrator actions run updates;
- **ASAP** — checks for a newer stable release every five minutes and schedules the latest immediately;
- **Nightly** — once per night at the configured local time;
- **Weekly** — once per configured weekday/local time;
- **Monthly** — once per configured day-of-month/local time; days beyond the end of a month are clamped to that month's last day.

Nightly/weekly/monthly policy uses the administrator-selected IANA/system time zone. Changing one of those calendar policies starts with the next occurrence rather than immediately replaying the previous occurrence. Pending jobs can be cancelled. Starting an immediate update with force semantics replaces an existing pending schedule, but never interrupts an update that is already running.

When the selected target skips one or more published versions, LlmProxy builds an ascending upgrade chain and applies every intermediate immutable release in order. For example, an installation on `0.0.6` targeting `0.0.9` executes `0.0.7 → 0.0.8 → 0.0.9`. This guarantees that a custom migration attached to an intermediate release cannot be bypassed.

Read-only administrators can inspect versions and plans but cannot schedule, cancel or start an update.

## Per-release update plan

Every new immutable GitHub Release publishes:

```text
llmproxy-update-plan.json
llmproxy-update-plan.json.sha256
```

The source contract lives at `distribution/update-plan.json`.

Normal releases use:

```json
{
  "schemaVersion": 1,
  "mode": "standard",
  "title": "Standard immutable update",
  "description": "Uses the versioned LlmProxy installer and preserves host configuration and Docker volumes.",
  "requiresHostRestart": false,
  "operatorCommand": "sudo -E llmproxyctl update {version}"
}
```

The UI substitutes the concrete version into `operatorCommand`. This field is for operator visibility/documentation; the Admin UI never sends arbitrary shell text to the host.

## Custom update procedure

When a release needs host work outside the normal versioned installer—for example a structural service migration—the release may change the plan to:

```json
{
  "schemaVersion": 1,
  "mode": "custom",
  "title": "Service-layout migration",
  "description": "Migrates the host service layout before normal deployment.",
  "requiresHostRestart": false,
  "operatorCommand": "sudo -E llmproxyctl update {version}"
}
```

and add an executable repository file:

```text
distribution/update.sh
```

The release bundle checksum covers that script. During an upgrade the bootstrap accepts only the two known modes `standard` and `custom`; `custom` executes only the fixed bundled `distribution/update.sh` entry point. There is no API or UI for supplying shell commands.

A custom `update.sh` owns the release-specific migration and must leave the same postconditions as the normal installer: protected configuration preserved, Docker data preserved unless the release explicitly documents a migration, exact target image deployed, and gateway health/readiness validated.

## Scheduling and persistence

The automatic policy itself is durable PostgreSQL configuration and is evaluated by the gateway background worker. In multi-replica deployments a PostgreSQL advisory lock and the persisted last-check timestamp ensure only one replica claims each due check. Automatic policies always target the latest published stable release; they still submit the complete ascending upgrade path to the host agent.

The Update Agent persists its active job and the recent job history under:

```text
/var/lib/llmproxy-update-agent/state.json
```

A scheduled job therefore survives gateway restarts. If the Update Agent itself restarts while a job is marked running, that job is marked failed/indeterminate and the operator must verify `llmproxyctl version`, gateway health and the installer log before scheduling another update.

The normal installer log remains:

```text
/var/log/llmproxy/latest-install.log
```

## Manual equivalent

The supported manual path remains:

```bash
sudo -E llmproxyctl update VERSION
```

Starting with the release that contains safe manual chaining, `llmproxyctl update` first resolves every published stable release between the installed version and the target. It refuses the operation if it cannot prove a complete stable chain, then applies each release in ascending order. Each step uses that release's immutable bootstrap/update-plan, so a custom migration cannot be skipped.

The bootstrap download path retries transient GitHub/network failures (including connection resets) before failing, while still requiring the published SHA-256 checksum before extraction.

## Upgrade compatibility

The first release that introduces the Update Agent must itself use the standard installer, because older installed bootstrap versions do not understand per-release update plans yet. Installing that release deploys the host Update Agent and the new `llmproxyctl`; subsequent releases can use either update-plan mode.

Legacy host configuration migration and legacy `--dgx-*` installer aliases remain supported for upgrades from older installations.
