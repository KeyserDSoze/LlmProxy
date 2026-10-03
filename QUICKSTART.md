# LlmProxy Quickstart

Sono disponibili due percorsi operativi:

- **Minimal** — `docs/quickstart.md`: LlmProxy + PostgreSQL, ideale per la prima prova.
- **Full stack** — `docs/full-stack.md`: LlmProxy + PostgreSQL + Redis + OpenTelemetry Collector + Tempo + Loki + Prometheus + Grafana, tutto auto-wired tramite Docker Compose.

La guida minimal copre installazione Docker su Linux/Windows, GHCR privato, configurazione `.env`, inference node/vLLM reale o mock, Admin UI, `/v1/*`, Entra opzionale e troubleshooting.

Per preparare il full stack su Linux:

```bash
bash docker/scripts/full-stack-init.sh
```

Su Windows/PowerShell:

```powershell
.\docker\scripts\full-stack-init.ps1
```

Gli script generano i secret locali; il Compose imposta automaticamente gli indirizzi interni di PostgreSQL, Redis e OpenTelemetry. Restano da indicare solo gli input esterni/operator-specifici, in particolare endpoint inference node e model id vLLM.
