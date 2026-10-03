# Quickstart — installare e provare LlmProxy

Questa guida porta da una macchina vuota a una prima installazione funzionante di LlmProxy usando l'immagine privata pubblicata su GitHub Container Registry (GHCR).

È pensata per due scenari:

1. **VM Linux** — percorso raccomandato per test realistici e per avvicinarsi alla futura installazione on-prem.
2. **PC Windows / Microsoft** — percorso comodo per sviluppo e prove locali con Docker Desktop + WSL 2.

Per eseguire l'immagine precompilata **non servono .NET SDK, Node.js o Visual Studio**. Questi servono solo se vuoi compilare/modificare il sorgente.

---

## 1. Architettura del quickstart

```text
Browser / curl / client OpenAI-compatible
                |
                v
        http://<VM>:8080
                |
                v
+-----------------------------------+
| Docker host                        |
|                                    |
|  LlmProxy                          |
|  ghcr.io/keyserdsoze/llmproxy     |
|            |                       |
|            +----> PostgreSQL 18    |
|            |                       |
+------------|-----------------------+
             |
             | LAN / host
             v
      vLLM / mock inference
      DGX Spark o test server
```

Per il primo test puoi lasciare **Entra ID disabilitato** e usare una semplice API key di sviluppo. Entra diventa obbligatorio solo quando avvii LlmProxy con `ASPNETCORE_ENVIRONMENT=Production`.

---

# Parte A — VM Linux

## 2. Sistema operativo consigliato

Per i primi test consigliamo **Ubuntu Server 24.04 LTS x86_64/amd64**.

Docker supporta anche altre distribuzioni/architetture, ma questa guida usa Ubuntu per mantenere un percorso unico e ripetibile.

Documentazione ufficiale Docker:

- Docker Engine Ubuntu: https://docs.docker.com/engine/install/ubuntu/
- Docker Compose plugin: https://docs.docker.com/compose/install/linux/

## 3. Installare Docker Engine e Docker Compose su Ubuntu

Rimuovi eventuali pacchetti Docker non ufficiali/conflittuali:

```bash
sudo apt remove -y docker.io docker-compose docker-compose-v2 docker-doc docker-buildx podman-docker containerd runc || true
```

Installa i prerequisiti e la chiave ufficiale Docker:

```bash
sudo apt update
sudo apt install -y ca-certificates curl git jq
sudo install -m 0755 -d /etc/apt/keyrings
sudo curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
sudo chmod a+r /etc/apt/keyrings/docker.asc
```

Aggiungi il repository Docker:

```bash
sudo tee /etc/apt/sources.list.d/docker.sources >/dev/null <<EOF
Types: deb
URIs: https://download.docker.com/linux/ubuntu
Suites: $(. /etc/os-release && echo "${UBUNTU_CODENAME:-$VERSION_CODENAME}")
Components: stable
Architectures: $(dpkg --print-architecture)
Signed-By: /etc/apt/keyrings/docker.asc
EOF

sudo apt update
```

Installa Docker Engine, Buildx e Compose v2:

```bash
sudo apt install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
```

Verifica:

```bash
sudo systemctl status docker --no-pager
docker --version
sudo docker compose version
sudo docker run --rm hello-world
```

### Usare Docker senza `sudo` — opzionale

```bash
sudo usermod -aG docker "$USER"
```

Poi esci e rientra nella sessione SSH (oppure riavvia la VM) e verifica:

```bash
docker ps
```

> Nota di sicurezza: appartenere al gruppo `docker` equivale sostanzialmente ad avere privilegi elevati sulla macchina. Fallo solo per utenti amministrativi/operativi autorizzati.

---

## 4. Ottenere accesso all'immagine privata GHCR

L'immagine è pubblicata dal workflow GitHub in:

```text
ghcr.io/keyserdsoze/llmproxy:main
```

Per un package privato GitHub Container Registry richiede un **Personal Access Token (classic)**. Per il solo download assegna almeno:

```text
read:packages
```

Documentazione ufficiale GitHub:

- GHCR authentication: https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-container-registry
- Personal access tokens: https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens

Se il tuo account/organizzazione usa SSO, autorizza anche il token verso l'organizzazione quando richiesto.

### Login sicuro dalla VM

Evita di scrivere il token direttamente nella command line:

```bash
read -s GHCR_PAT
# incolla il PAT e premi Invio

echo "$GHCR_PAT" | docker login ghcr.io -u KeyserDSoze --password-stdin
unset GHCR_PAT
```

Output atteso:

```text
Login Succeeded
```

Prova il pull:

```bash
docker pull ghcr.io/keyserdsoze/llmproxy:main
```

Per test riproducibili puoi usare un tag immutabile associato a una build validata, per esempio:

```text
ghcr.io/keyserdsoze/llmproxy:sha-600ad42
```

`main` invece segue l'ultima build di `main` pubblicata dopo CI verde.

---

## 5. Recuperare i file di deployment

Se la VM ha accesso alla repository privata:

```bash
git clone https://github.com/KeyserDSoze/LlmProxy.git
cd LlmProxy
```

Per il quickstart servono principalmente:

```text
docker/docker-compose.quickstart.yml
docker/.env.quickstart.example
```

Crea un file locale `.env` che **non deve essere committato**:

```bash
cp docker/.env.quickstart.example docker/.env.quickstart
chmod 600 docker/.env.quickstart
```

Aprilo:

```bash
nano docker/.env.quickstart
```

---

## 6. Configurazione minima

Per una prima installazione modifica almeno questi valori:

```env
GHCR_OWNER=keyserdsoze
LLMPROXY_IMAGE_TAG=main
LLMPROXY_PORT=8080

POSTGRES_DB=llmproxy
POSTGRES_USER=llmproxy
POSTGRES_PASSWORD=UNA_PASSWORD_LUNGA_CASUALE

LLM_PROXY_API_KEY=UNA_API_KEY_DI_TEST_LUNGA
LLM_PROXY_API_KEY_PEPPER=UN_PEPPER_LUNGO_CASUALE

ASPNETCORE_ENVIRONMENT=Development
ENTRA_ENABLED=false

BOOTSTRAP_ENABLED=true
DGX_NODE_NAME=dgx-01
DGX_NODE_BASE_ADDRESS=http://10.0.0.21:8000
DGX_NODE_WEIGHT=1
DGX_NODE_MAX_CONCURRENCY=4

PUBLIC_MODEL_NAME=agic-code-fast
PROVIDER_MODEL_NAME=MODEL_ID_ESPOSTO_DA_VLLM
```

Puoi generare password/secret casuali su Linux, ad esempio:

```bash
openssl rand -hex 32
```

Generane uno diverso per PostgreSQL, inference API key e API-key pepper.

### Significato dei due model name

```text
PUBLIC_MODEL_NAME
    nome logico visto dal client/Copilot
    esempio: agic-code-fast

PROVIDER_MODEL_NAME
    model ID reale esposto da vLLM
    esempio: Qwen/...
```

Il client non deve conoscere il model ID fisico.

---

## 7. Collegare un DGX/vLLM reale

Dalla VM verifica prima il runtime direttamente:

```bash
curl -f http://10.0.0.21:8000/health
curl -f http://10.0.0.21:8000/v1/models | jq
```

Il valore `DGX_NODE_BASE_ADDRESS` è la **service root completa**. Sono validi per esempio:

```text
http://10.0.0.21:8000
http://10.0.0.21:8000/vllm
https://dgx01.internal:8443/inference
```

LlmProxy aggiunge automaticamente:

```text
/health
/metrics
/v1/models
/v1/chat/completions
/v1/responses
```

`PROVIDER_MODEL_NAME` deve corrispondere all'ID modello ritornato da `/v1/models`.

---

## 8. Provare senza DGX usando il mock repository

Se non hai ancora vLLM disponibile, puoi usare il mock HTTP usato dalla CI.

Prerequisito:

```bash
sudo apt install -y python3
```

Dalla root della repository:

```bash
python3 tests/backend/integration/mock_llm.py \
  --port 3450 \
  --prefix /primopath \
  --name mock-dgx-01
```

Lascia quel terminale aperto e modifica `.env.quickstart`:

```env
DGX_NODE_NAME=mock-dgx-01
DGX_NODE_BASE_ADDRESS=http://host.docker.internal:3450/primopath
PUBLIC_MODEL_NAME=agic-code-fast
PROVIDER_MODEL_NAME=bootstrap-model
HARDWARE_METRICS_ENABLED=false
```

Il compose quickstart configura `host.docker.internal` anche su Linux tramite `host-gateway`.

---

## 9. Avviare LlmProxy

Dalla root della repository:

```bash
docker compose \
  --env-file docker/.env.quickstart \
  -f docker/docker-compose.quickstart.yml \
  pull
```

Poi:

```bash
docker compose \
  --env-file docker/.env.quickstart \
  -f docker/docker-compose.quickstart.yml \
  up -d
```

Controlla:

```bash
docker compose \
  --env-file docker/.env.quickstart \
  -f docker/docker-compose.quickstart.yml \
  ps
```

Log applicativi:

```bash
docker compose \
  --env-file docker/.env.quickstart \
  -f docker/docker-compose.quickstart.yml \
  logs -f llmproxy
```

---

## 10. Health e readiness

Dalla VM:

```bash
curl -s http://localhost:8080/healthz | jq
curl -s http://localhost:8080/readyz | jq
```

Output attesi, indicativamente:

```json
{"status":"ok","service":"llmproxy"}
```

```json
{"status":"ready"}
```

`/readyz` verifica anche la raggiungibilità di PostgreSQL.

---

## 11. Aprire l'Admin UI

Da un browser che raggiunge la VM:

```text
http://IP-DELLA-VM:8080/admin/
```

Con:

```env
ASPNETCORE_ENVIRONMENT=Development
ENTRA_ENABLED=false
```

l'Admin UI è utilizzabile senza login Entra ed è adatta **solo a rete/test controllati**.

Non esporre questa modalità direttamente su Internet.

---

## 12. Prima chiamata OpenAI-compatible

Elenca i logical model:

```bash
curl -s \
  -H "Authorization: Bearer $LLM_PROXY_API_KEY" \
  http://localhost:8080/v1/models | jq
```

Se la variabile non è esportata nella shell:

```bash
export LLM_PROXY_API_KEY='la-stessa-api-key-del-file-env'
```

Chat Completions:

```bash
curl -s \
  -H "Authorization: Bearer $LLM_PROXY_API_KEY" \
  -H "Content-Type: application/json" \
  -d '{
    "model":"agic-code-fast",
    "messages":[{"role":"user","content":"hello from llmproxy"}]
  }' \
  http://localhost:8080/v1/chat/completions | jq
```

Streaming SSE:

```bash
curl -N \
  -H "Authorization: Bearer $LLM_PROXY_API_KEY" \
  -H "Content-Type: application/json" \
  -d '{
    "model":"agic-code-fast",
    "stream":true,
    "messages":[{"role":"user","content":"stream test"}]
  }' \
  http://localhost:8080/v1/chat/completions
```

Responses API:

```bash
curl -s \
  -H "Authorization: Bearer $LLM_PROXY_API_KEY" \
  -H "Content-Type: application/json" \
  -d '{
    "model":"agic-code-fast",
    "input":"responses api test"
  }' \
  http://localhost:8080/v1/responses | jq
```

---

## 13. Aggiornare LlmProxy

Quando una nuova build `main` supera la CI e viene pubblicata su GHCR:

```bash
docker compose \
  --env-file docker/.env.quickstart \
  -f docker/docker-compose.quickstart.yml \
  pull llmproxy


docker compose \
  --env-file docker/.env.quickstart \
  -f docker/docker-compose.quickstart.yml \
  up -d
```

Le migration EF Core vengono applicate automaticamente all'avvio.

PostgreSQL usa un volume Docker persistente, quindi il normale aggiornamento del container non cancella configurazione/audit/metriche.

---

## 14. Fermare o resettare l'ambiente

Fermare senza cancellare PostgreSQL:

```bash
docker compose \
  --env-file docker/.env.quickstart \
  -f docker/docker-compose.quickstart.yml \
  down
```

Riavviare:

```bash
docker compose \
  --env-file docker/.env.quickstart \
  -f docker/docker-compose.quickstart.yml \
  up -d
```

**Reset completo di test**, incluso PostgreSQL:

```bash
docker compose \
  --env-file docker/.env.quickstart \
  -f docker/docker-compose.quickstart.yml \
  down -v
```

> `down -v` cancella il database. Non usarlo su ambienti con dati da conservare.

---

# Parte B — Windows / Microsoft workstation

## 15. Installare WSL 2

Per Windows 10/11 usa preferibilmente WSL 2.

Apri PowerShell come amministratore:

```powershell
wsl --install
wsl --update
wsl --version
```

Può essere richiesto un riavvio.

Microsoft/Docker raccomandano una versione WSL aggiornata; Docker Desktop richiede almeno WSL 2.1.5 per il backend WSL 2.

Documentazione:

- Docker Desktop Windows: https://docs.docker.com/desktop/setup/install/windows-install/
- Docker + WSL 2: https://docs.docker.com/desktop/features/wsl/
- Microsoft WSL: https://learn.microsoft.com/windows/wsl/install

## 16. Installare Docker Desktop

Scarica Docker Desktop dalla pagina ufficiale:

https://docs.docker.com/desktop/setup/install/windows-install/

Per il nostro scenario usa **Linux containers** e il backend WSL 2.

Verifica da PowerShell:

```powershell
docker --version
docker compose version
docker run --rm hello-world
```

> Nota licenze: Docker Desktop può richiedere una sottoscrizione commerciale in organizzazioni che superano le soglie indicate nei termini Docker. Per VM Linux/server il percorso Docker Engine non dipende da Docker Desktop.

## 17. GHCR da Windows

In PowerShell:

```powershell
$env:GHCR_PAT = Read-Host "GitHub PAT" -AsSecureString
```

Il piping di `SecureString` a Docker non è immediato; per una macchina di sviluppo puoi usare GitHub CLI (`gh auth token`) oppure effettuare `docker login ghcr.io -u KeyserDSoze` e incollare il PAT quando Docker richiede la password.

Poi:

```powershell
docker pull ghcr.io/keyserdsoze/llmproxy:main
```

Il resto del quickstart può essere eseguito da una shell WSL/Ubuntu usando gli stessi file compose della Parte A.

---

# Parte C — Tool Microsoft opzionali per sviluppo

## 18. .NET 10 SDK — serve solo per compilare il sorgente

Se vuoi sviluppare/backend-testare fuori dal container, installa il **.NET 10 SDK**:

https://dotnet.microsoft.com/download/dotnet/10.0

Verifica:

```bash
dotnet --version
```

Il repository usa `global.json`; usa una versione .NET 10 compatibile con quella richiesta dal repository.

Per eseguire semplicemente `ghcr.io/keyserdsoze/llmproxy:*` non devi installare .NET sulla VM.

## 19. Node.js — serve solo per modificare la React Admin

La build container usa Node.js 22 per la UI.

Per sviluppo locale installa Node.js 22+ e verifica:

```bash
node --version
npm --version
```

Anche Node **non serve** sulla VM che esegue l'immagine precompilata.

---

# Parte D — Microsoft Entra ID opzionale

## 20. Quando configurarlo

Per il primo smoke test lascia:

```env
ASPNETCORE_ENVIRONMENT=Development
ENTRA_ENABLED=false
```

Prima di un ambiente `Production` configura Entra ID. Il backend rifiuta deliberatamente l'avvio Production se Entra è disabilitato.

## 21. App Registration

Nel Microsoft Entra admin center:

1. **App registrations** -> **New registration**.
2. Crea una Web application dedicata a LlmProxy.
3. In **Authentication** aggiungi una piattaforma **Web**.
4. Configura il redirect URI:

```text
https://<hostname-llmproxy>/signin-oidc
```

Per test puramente locale sullo stesso PC puoi usare un localhost URI coerente con l'endpoint usato nel browser.

Microsoft Learn:

https://learn.microsoft.com/entra/identity-platform/how-to-add-redirect-uri

5. Crea un client secret/certificate appropriato all'ambiente.
6. Definisci/assegna i ruoli applicativi usati da LlmProxy:

```text
LlmProxy.Admin
LlmProxy.User
LlmProxy.Reader
```

`LlmProxy.Admin` può modificare la configurazione e usare il self-service; `LlmProxy.User` può gestire esclusivamente le proprie API key personali tramite `/admin/me`; `LlmProxy.Reader` può leggere il control plane operativo.

Configura quindi:

```env
ASPNETCORE_ENVIRONMENT=Production
ENTRA_ENABLED=true
ENTRA_TENANT_ID=<tenant-guid>
ENTRA_CLIENT_ID=<application-client-id>
ENTRA_CLIENT_SECRET=<secret>
```

I secret Entra non devono mai essere committati.

Per una prova LAN iniziale senza HTTPS pubblico continua invece in Development/Entra-disabled.

---

# Parte E — Porte e rete

## 22. Porte minime

### VM LlmProxy

```text
8080/tcp    Admin + OpenAI-compatible API, solo per test/LAN se esposto direttamente
```

PostgreSQL non viene pubblicato sulla rete host dal quickstart.

### DGX/vLLM

La VM deve poter raggiungere la service root configurata, ad esempio:

```text
10.0.0.21:8000
```

Se abiliti DCGM:

```text
10.0.0.21:9400
```

Per la futura esposizione Internet è previsto Cloudflare Tunnel, così non è necessario pubblicare direttamente la porta 8080 verso Internet.

---

# Parte F — Troubleshooting

## 23. `unauthorized: authentication required` durante il pull

Controlla:

```bash
docker logout ghcr.io
```

poi ripeti il login con un PAT **classic** dotato di `read:packages` e con accesso al package/repository.

Se l'organizzazione usa SSO, verifica che il token sia autorizzato per SSO.

## 24. `no matching manifest for linux/arm64`

Controlla l'architettura:

```bash
uname -m
```

La pipeline GHCR deve pubblicare un manifest compatibile con l'architettura della VM. La baseline di CI attuale è costruita sul runner Linux standard GitHub e il target di prova consigliato è `linux/amd64`.

## 25. LlmProxy parte ma il nodo è `Unhealthy`

Prova dalla VM, senza passare dal gateway:

```bash
curl -v http://10.0.0.21:8000/health
curl -v http://10.0.0.21:8000/v1/models
```

Controlla quindi:

```bash
docker compose \
  --env-file docker/.env.quickstart \
  -f docker/docker-compose.quickstart.yml \
  logs --tail=200 llmproxy
```

Verifica che `DGX_NODE_BASE_ADDRESS` sia la service root e **non** l'URL completo di `/v1/chat/completions`.

## 26. PostgreSQL non è ready

```bash
docker compose \
  --env-file docker/.env.quickstart \
  -f docker/docker-compose.quickstart.yml \
  ps


docker compose \
  --env-file docker/.env.quickstart \
  -f docker/docker-compose.quickstart.yml \
  logs postgres
```

## 27. Porta 8080 già utilizzata

Cambia:

```env
LLMPROXY_PORT=8081
```

Poi accedi a:

```text
http://<VM>:8081/admin/
```

---

# Checklist rapida

Prima prova Linux:

```text
[ ] Ubuntu VM raggiungibile via SSH
[ ] Docker Engine installato
[ ] docker compose disponibile
[ ] PAT GitHub classic con read:packages
[ ] docker login ghcr.io riuscito
[ ] docker pull ghcr.io/keyserdsoze/llmproxy:main riuscito
[ ] docker/.env.quickstart creato e protetto
[ ] vLLM reale raggiungibile oppure mock Python avviato
[ ] docker compose quickstart up -d
[ ] /healthz = ok
[ ] /readyz = ready
[ ] Admin UI raggiungibile
[ ] /v1/models restituisce agic-code-fast
[ ] Chat Completions funziona
[ ] Streaming SSE funziona
```

Dopo questo smoke test puoi passare a:

1. vLLM reale su DGX Spark;
2. DCGM hardware telemetry;
3. HTTPS/Cloudflare Tunnel;
4. Entra ID;
5. GitHub Copilot BYOK.

---

## Documenti successivi

- `docs/deployment.md` — deployment production-oriented.
- `docs/dgx-vllm.md` — contratto DGX/vLLM e service root.
- `docs/security.md` — trust boundaries, Entra e secret handling.
- `docs/user-access.md` — censimento automatico/manuale utenti, dashboard personale e disabilitazione.
- `docs/github-copilot.md` — integrazione GitHub Copilot.
- `docs/operations.md` — gestione operativa.
- `docs/testing.md` — quality gate e test.
- `docs/project-status.md` — stato corrente e punto esatto di ripartenza dello sviluppo.
