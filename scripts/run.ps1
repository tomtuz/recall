#!/usr/bin/env pwsh
# scripts/run.ps1 - start the Recall API (uses the setup from scripts/init.ps1).
# Optional: -NoBuild to skip the build; -Watch for dotnet watch (hot reload). Uses any PostgreSQL already reachable at the
# connection string's host:port; falls back to the Docker container from init.ps1.
# Runs from any directory and leaves the caller's location unchanged.

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Interactive console script: colored status output.')]
param([switch]$NoBuild, [switch]$Watch)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

# 1. Ensure user secrets exist (runs first-time setup if init.ps1 was never run).
$secretCount = @(dotnet user-secrets list --project $ApiDir 2>$null |
    Where-Object { $_ -match '^(Jwt:Secret|ConnectionStrings:Default)\s*=' }).Count
if ($secretCount -lt 2) {
    Write-Host "[run] User secrets missing - running first-time setup..." -ForegroundColor Yellow
    & (Join-Path $PSScriptRoot 'init.ps1')
}

# 2. Ensure PostgreSQL is reachable: an already running server (any local install)
#    is used as is; otherwise start the Docker container created by init.ps1.
$db = Get-DbTarget
$dbAddr = "$($db.Host):$($db.Port)"
$pgOk = Test-DbReachable $db.Host $db.Port
if ($pgOk) {
    Write-Host "[run] PostgreSQL is reachable at $dbAddr." -ForegroundColor Green
}
elseif (Get-Command docker -ErrorAction SilentlyContinue) {
    docker info *> $null
    if ($LASTEXITCODE -eq 0) {
        $status = docker inspect -f '{{.State.Status}}' recall-pg 2>$null
        if ($status -ne 'running') {
            if ($status -eq 'exited') { docker start recall-pg *> $null }
            else {
                Write-Host "[run] PostgreSQL container missing - run ./index.ps1 setup first (creating basic one now)..." -ForegroundColor Yellow
                docker run -d --name recall-pg `
                    -e POSTGRES_USER=recall -e POSTGRES_PASSWORD=recall -e POSTGRES_DB=recall `
                    -p 15432:5432 postgres:16-alpine *> $null
            }
        }
        for ($i = 0; $i -lt 30; $i++) {
            docker exec recall-pg pg_isready -U recall -d recall *> $null
            if ($LASTEXITCODE -eq 0) { $pgOk = $true; break }
            Start-Sleep -Seconds 1
        }
    }
}
if (-not $pgOk) {
    # The API applies migrations at startup, so it cannot run without a database.
    $why = if (Get-Command docker -ErrorAction SilentlyContinue) { 'the Docker engine or container did not come up' } else { 'Docker is not installed to start the demo container' }
    Write-Host "[run] No PostgreSQL answers at $dbAddr ($why)." -ForegroundColor Red
    Write-Host "      Start PostgreSQL there (or Docker Desktop), then re-run. Setup: ./index.ps1 setup." -ForegroundColor Yellow
    exit 1
}

# 3. The API port must be free; otherwise dotnet run fails with "address already in use".
$owner = Get-PortOwner $ApiPort | Select-Object -First 1
if ($owner) {
    $what = if (Test-OwnApiProcess $owner) { 'an already running Recall API' } else { "'$($owner.ProcessName)'" }
    Write-Host "[run] Port $ApiPort is held by $what (PID $($owner.Id))." -ForegroundColor Red
    Write-Host "      Stop it (Stop-Process -Id $($owner.Id)) or restart with .\index.ps1, then re-run." -ForegroundColor Yellow
    exit 1
}

# 4. Run the API in this console (Ctrl+C to stop). With --project, dotnet run uses
#    the project's launch profile and Recall.Api as content root from any location.
Write-Host "=== Recall API -> http://localhost:$ApiPort/swagger ===" -ForegroundColor Cyan
$runArgs = @('run', '--project', $ApiDir, '--urls', "http://localhost:$ApiPort")
if ($NoBuild) { $runArgs += '--no-build' }
if ($Watch) { $runArgs = @('watch', '--project', $ApiDir, 'run', '--urls', "http://localhost:$ApiPort") }
dotnet @runArgs
Write-Host "API exited with code $LASTEXITCODE" -ForegroundColor Yellow
