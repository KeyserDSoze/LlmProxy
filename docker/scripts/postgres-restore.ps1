[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$BackupFile,
    [switch]$ConfirmDestructive
)

$ErrorActionPreference = "Stop"
if (-not $ConfirmDestructive) {
    throw "Restore is destructive. Re-run with -ConfirmDestructive. All gateway writers must be stopped."
}

$root = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
Set-Location $root
$composeFile = if ($env:COMPOSE_FILE) { $env:COMPOSE_FILE } else { "docker/docker-compose.full.yml" }
$envFile = $env:ENV_FILE
$redisPrefix = if ($env:REDIS_KEY_PREFIX) { $env:REDIS_KEY_PREFIX } else { "llmproxy" }
$startGateway = if ($env:RESTORE_START_GATEWAY) { $env:RESTORE_START_GATEWAY -eq "true" } else { $true }

$backupPath = if ([System.IO.Path]::IsPathRooted($BackupFile)) {
    [System.IO.Path]::GetFullPath($BackupFile)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $root $BackupFile))
}
if (-not (Test-Path $backupPath)) { throw "Backup file not found: $backupPath" }
if (-not (Test-Path $composeFile)) { throw "Compose file not found: $composeFile" }
if ($envFile -and -not (Test-Path $envFile)) { throw "Environment file not found: $envFile" }

$checksumFile = "$backupPath.sha256"
if (Test-Path $checksumFile) {
    $expected = ((Get-Content $checksumFile -TotalCount 1) -split '\s+')[0].ToLowerInvariant()
    $actual = (Get-FileHash -Algorithm SHA256 $backupPath).Hash.ToLowerInvariant()
    if (-not $expected -or $expected -ne $actual) { throw "Backup checksum verification failed." }
}

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
    throw "The postgres Compose service must be running before restore."
}

$tempContainer = "/tmp/llmproxy-restore-$PID.dump"
try {
    Invoke-Compose @("cp", $backupPath, "postgres:$tempContainer")
    Invoke-Compose @("exec", "-T", "-e", "LLM_RESTORE_TMP=$tempContainer", "postgres", "sh", "-eu", "-c", 'pg_restore --list "$LLM_RESTORE_TMP" >/dev/null')

    $services = (& docker @composeArgs config --services) -split "`r?`n"
    if ($services -contains "llmproxy") {
        & docker @composeArgs stop llmproxy | Out-Null
    }

    Invoke-Compose @("exec", "-T", "postgres", "sh", "-eu", "-c", 'dropdb --force --if-exists -U "$POSTGRES_USER" "$POSTGRES_DB" && createdb -U "$POSTGRES_USER" -O "$POSTGRES_USER" "$POSTGRES_DB"')
    Invoke-Compose @("exec", "-T", "-e", "LLM_RESTORE_TMP=$tempContainer", "postgres", "sh", "-eu", "-c", 'pg_restore -U "$POSTGRES_USER" -d "$POSTGRES_DB" --no-owner --no-acl --exit-on-error "$LLM_RESTORE_TMP"')
    Invoke-Compose @("exec", "-T", "-e", "LLM_RESTORE_TMP=$tempContainer", "postgres", "sh", "-c", 'rm -f "$LLM_RESTORE_TMP"')

    $runningAfter = (& docker @composeArgs ps --status running --services) -split "`r?`n"
    if (($services -contains "redis") -and ($runningAfter -contains "redis")) {
        Invoke-Compose @("exec", "-T", "-e", "LLM_RESTORE_PREFIX=$redisPrefix", "redis", "sh", "-eu", "-c", 'redis-cli -a "$REDIS_PASSWORD" --scan --pattern "${LLM_RESTORE_PREFIX}:*" 2>/dev/null | while IFS= read -r key; do [ -n "$key" ] || continue; redis-cli -a "$REDIS_PASSWORD" UNLINK "$key" >/dev/null 2>&1; done')
    }

    if ($startGateway -and ($services -contains "llmproxy")) {
        Invoke-Compose @("up", "-d", "llmproxy")
    }

    Write-Host "Restore completed from: $backupPath"
    Write-Host "Important: Authentication__ApiKeyPepper and all external deployment secrets must match the backed-up environment."
}
finally {
    try { & docker @composeArgs exec -T -e "LLM_RESTORE_TMP=$tempContainer" postgres sh -c 'rm -f "$LLM_RESTORE_TMP"' | Out-Null } catch { }
}
