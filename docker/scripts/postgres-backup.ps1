[CmdletBinding()]
param(
    [string]$BackupFile = ("backups/llmproxy-{0}.dump" -f (Get-Date).ToUniversalTime().ToString("yyyyMMddTHHmmssZ"))
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
Set-Location $root

$composeFile = if ($env:COMPOSE_FILE) { $env:COMPOSE_FILE } else { "docker/docker-compose.full.yml" }
$envFile = $env:ENV_FILE
if (-not (Test-Path $composeFile)) { throw "Compose file not found: $composeFile" }
if ($envFile -and -not (Test-Path $envFile)) { throw "Environment file not found: $envFile" }

$composeArgs = @("compose")
if ($envFile) { $composeArgs += @("--env-file", $envFile) }
$composeArgs += @("-f", $composeFile)

function Invoke-Compose {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)
    & docker @composeArgs @Arguments
    if ($LASTEXITCODE -ne 0) { throw "docker compose failed: $($Arguments -join ' ')" }
}

$running = (& docker @composeArgs ps --status running --services) -split "`r?`n"
if ($LASTEXITCODE -ne 0 -or $running -notcontains "postgres") {
    throw "The postgres Compose service must be running before backup."
}

$backupPath = if ([System.IO.Path]::IsPathRooted($BackupFile)) {
    [System.IO.Path]::GetFullPath($BackupFile)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $root $BackupFile))
}
$backupDir = Split-Path -Parent $backupPath
New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
$tempHost = "$backupPath.tmp.$PID"
$tempContainer = "/tmp/llmproxy-backup-$PID.dump"

try {
    Invoke-Compose @("exec", "-T", "-e", "LLM_BACKUP_TMP=$tempContainer", "postgres", "sh", "-eu", "-c", 'pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" --format=custom --compress=6 --no-owner --no-acl -f "$LLM_BACKUP_TMP"')
    Invoke-Compose @("exec", "-T", "-e", "LLM_BACKUP_TMP=$tempContainer", "postgres", "sh", "-eu", "-c", 'pg_restore --list "$LLM_BACKUP_TMP" >/dev/null')
    Invoke-Compose @("cp", "postgres:$tempContainer", $tempHost)
    Invoke-Compose @("exec", "-T", "-e", "LLM_BACKUP_TMP=$tempContainer", "postgres", "sh", "-c", 'rm -f "$LLM_BACKUP_TMP"')

    if (-not (Test-Path $tempHost) -or (Get-Item $tempHost).Length -eq 0) { throw "Backup produced an empty file." }
    Move-Item -Force $tempHost $backupPath

    $checksum = (Get-FileHash -Algorithm SHA256 $backupPath).Hash.ToLowerInvariant()
    "$checksum  $([System.IO.Path]::GetFileName($backupPath))" | Set-Content -Encoding ascii "$backupPath.sha256"
    @(
        "created_at_utc=$((Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ'))"
        "format=postgres-custom"
        "compose_file=$composeFile"
        "note=Authentication__ApiKeyPepper and other deployment secrets are external to this database backup and must be preserved separately in the secret manager."
    ) | Set-Content -Encoding utf8 "$backupPath.meta"

    Write-Host "Backup created: $backupPath"
    Write-Host "Checksum: $backupPath.sha256"
    Write-Host "Metadata: $backupPath.meta"
}
finally {
    Remove-Item -Force -ErrorAction SilentlyContinue $tempHost
    try { & docker @composeArgs exec -T -e "LLM_BACKUP_TMP=$tempContainer" postgres sh -c 'rm -f "$LLM_BACKUP_TMP"' | Out-Null } catch { }
}
