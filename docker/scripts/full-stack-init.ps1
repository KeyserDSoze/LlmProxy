param(
    [switch]$Start
)

$ErrorActionPreference = "Stop"
$Root = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$EnvFile = Join-Path $Root "docker/.env.full"
$ExampleFile = Join-Path $Root "docker/.env.full.example"
$ComposeFile = Join-Path $Root "docker/docker-compose.full.yml"

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw "Docker Desktop/Engine is required."
}

docker compose version | Out-Null

if (-not (Test-Path $EnvFile)) {
    Copy-Item $ExampleFile $EnvFile
}

function New-HexSecret([int]$Bytes) {
    $buffer = New-Object byte[] $Bytes
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($buffer) } finally { $rng.Dispose() }
    return ([BitConverter]::ToString($buffer) -replace '-', '').ToLowerInvariant()
}

$content = Get-Content $EnvFile -Raw
$content = $content.Replace("CHANGE_ME_POSTGRES_PASSWORD", (New-HexSecret 24))
$content = $content.Replace("CHANGE_ME_REDIS_PASSWORD", (New-HexSecret 24))
$content = $content.Replace("CHANGE_ME_LLM_PROXY_API_KEY", ("llmp_" + (New-HexSecret 24)))
$content = $content.Replace("CHANGE_ME_LLM_PROXY_API_KEY_PEPPER", (New-HexSecret 32))
$content = $content.Replace("CHANGE_ME_UPSTREAM_CREDENTIAL_KEY", (New-HexSecret 32))
$content = $content.Replace("CHANGE_ME_GRAFANA_ADMIN_PASSWORD", (New-HexSecret 20))
Set-Content -Path $EnvFile -Value $content -NoNewline

Write-Host "Full-stack environment prepared: docker/.env.full"
Write-Host "Internal PostgreSQL, Redis and OpenTelemetry service URLs are injected automatically by Compose."
Write-Host "Before startup, edit operator-specific values such as DGX_NODE_BASE_ADDRESS and PROVIDER_MODEL_NAME."
Write-Host "If GHCR is private, authenticate first with: docker login ghcr.io"

if ($Start) {
    docker compose --env-file $EnvFile -f $ComposeFile pull
    docker compose --env-file $EnvFile -f $ComposeFile up -d
    Write-Host "LlmProxy: http://localhost:8080/admin/ (or LLMPROXY_PORT)"
    Write-Host "Grafana:  http://localhost:3000 (or GRAFANA_PORT)"
}
