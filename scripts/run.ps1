#!/usr/bin/env pwsh
# scripts/run.ps1 - start the Recall API (uses the setup from scripts/init.ps1).
# Optional: -PgOnly to only ensure PostgreSQL is running, -NoBuild to skip the build.

param([switch]$NoBuild)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

# 1. Ensure user secrets exist (generates them if init.ps1 was never run).
$apiDir = Join-Path $root 'Recall.Api'
Set-Location $apiDir
$secretCount = (dotnet user-secrets list 2>$null | Measure-Object).Count
if ($secretCount -lt 2 -and $secretCount -ne 2) {
    if ($secretCount -lt 2) {
        Write-Host "[run] User secrets missing - running first-time setup..." -ForegroundColor Yellow
        & (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'init.ps1')
    }
}

# 2. Ensure PostgreSQL is running (container created by init.ps1).
$pgOk = $false
if (Get-Command docker -ErrorAction SilentlyContinue) {
    docker info *> $null
    if ($LASTEXITCODE -eq 0) {
        $status = docker inspect -f '{{.State.Status}}' recall-pg 2>$null
        if ($status -ne 'running') {
            if ($status -eq 'exited') { docker start recall-pg *> $null }
            else {
                Write-Host "[run] PostgreSQL container missing - run scripts/init.ps1 first (creating basic one now)..." -ForegroundColor Yellow
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
    Write-Warning "PostgreSQL not reachable on localhost:15432 - the API will start but database operations will fail until a database is reachable."
}

# 3. Run the API in this console (Ctrl+C to stop).
Write-Host "=== Recall API -> http://localhost:15080/swagger ===" -ForegroundColor Cyan
if ($NoBuild) {
    dotnet run --no-build --urls "http://localhost:15080"
} else {
    dotnet run --urls "http://localhost:15080"
}
Write-Host "API exited with code $LASTEXITCODE" -ForegroundColor Yellow