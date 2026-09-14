# LlmProxy Quickstart

La guida operativa completa per installare e provare LlmProxy su una VM Linux o su un PC Windows/Microsoft è qui:

**[`docs/quickstart.md`](docs/quickstart.md)**

Copre:

- installazione Docker Engine + Docker Compose su Ubuntu;
- Docker Desktop + WSL 2 su Windows;
- autenticazione a GitHub Container Registry privato;
- pull di `ghcr.io/keyserdsoze/llmproxy:main`;
- configurazione `.env`;
- PostgreSQL;
- avvio tramite `docker/docker-compose.quickstart.yml`;
- test con DGX/vLLM reale oppure con il mock repository;
- `/healthz`, `/readyz`, Admin UI e `/v1/*`;
- aggiornamento e reset dell'ambiente;
- prerequisiti .NET 10/Node solo per lo sviluppo da sorgente;
- setup opzionale Microsoft Entra ID;
- troubleshooting e checklist finale.
