#!/usr/bin/env pwsh
# scripts/init.ps1 - one-time backend setup for a fresh clone.
# Generates the JWT signing key, sets user secrets and starts PostgreSQL; the API
# applies migrations and seeds demo data on its first start. Idempotent: safe to
# re-run. Runs from any directory and leaves the caller's location unchanged.

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Interactive console script: colored status output.')]
param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

Write-Host "=== Recall API - first-time setup ===" -ForegroundColor Cyan

# 1. User secrets: JWT signing key + database connection string.
$secretsPresent = [bool](dotnet user-secrets list --project $ApiDir 2>$null |
    Where-Object { $_ -match '^(Jwt:Secret|ConnectionStrings:Default)' })
if (-not $secretsPresent) {
    dotnet user-secrets init --project $ApiDir | Out-Null
    # Generate 64 random bytes -> base64 (512-bit key, matches production guidance).
    $key = New-Object byte[] 64
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($key)  # also works in Windows PowerShell 5.1
    $jwtSecret = [Convert]::ToBase64String($key)
    dotnet user-secrets set "Jwt:Secret" $jwtSecret --project $ApiDir | Out-Null
    dotnet user-secrets set "ConnectionStrings:Default" "Host=localhost;Port=15432;Database=recall;Username=recall;Password=recall" --project $ApiDir | Out-Null
    Write-Host "[init] JWT signing key generated and stored in user secrets." -ForegroundColor Green
    Write-Host "       Database connection string set (localhost:15432 demo default)." -ForegroundColor Green
} else {
    Write-Host "[init] User secrets already present - keeping existing values." -ForegroundColor DarkGray
}

# 2. PostgreSQL: use the server the connection string points at if it is already
#    reachable (any local install); otherwise start the Docker demo container.
$db = Get-DbTarget
$dbAddr = "$($db.Host):$($db.Port)"
$dbReady = Test-DbReachable $db.Host $db.Port
if ($dbReady) {
    Write-Host "[init] PostgreSQL is reachable at $dbAddr - using it (Docker not needed)." -ForegroundColor Green
}
elseif (Get-Command docker -ErrorAction SilentlyContinue) {
    docker info *> $null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[init] Docker engine not running - starting Docker Desktop..." -ForegroundColor Yellow
        $dd = Join-Path $env:ProgramFiles "Docker\Docker\Docker Desktop.exe"
        if (Test-Path $dd) {
            Start-Process $dd
            for ($i = 0; $i -lt 60; $i++) { docker info *> $null; if ($LASTEXITCODE -eq 0) { break }; Start-Sleep -Seconds 2 }
        }
    }
    if ($LASTEXITCODE -eq 0) {
        $status = docker inspect -f '{{.State.Status}}' recall-pg 2>$null
        if (-not $status) {
            docker run -d --name recall-pg `
                -e POSTGRES_USER=recall -e POSTGRES_PASSWORD=recall -e POSTGRES_DB=recall `
                -p 15432:5432 postgres:16-alpine *> $null
            Write-Host "[init] PostgreSQL container 'recall-pg' created (port 15432)." -ForegroundColor Green
        } elseif ($status -ne 'running') {
            docker start recall-pg *> $null
            Write-Host "[init] PostgreSQL container 'recall-pg' started." -ForegroundColor Green
        } else {
            Write-Host "[init] PostgreSQL container 'recall-pg' already running." -ForegroundColor DarkGray
        }
        for ($i = 0; $i -lt 30; $i++) {
            docker exec recall-pg pg_isready -U recall -d recall *> $null
            if ($LASTEXITCODE -eq 0) { $dbReady = $true; break }
            Start-Sleep -Seconds 1
        }
        if (-not $dbReady) { throw "PostgreSQL did not accept connections within 30s." }
    } else {
        Write-Warning "Docker is installed but its engine did not start, and nothing answers at $dbAddr."
        Write-Host "       Start Docker Desktop and re-run this script, or start your own PostgreSQL at $dbAddr." -ForegroundColor Yellow
    }
} else {
    Write-Warning "No PostgreSQL answers at $dbAddr, and Docker is not installed to start the demo container."
    Write-Host "       Either start a PostgreSQL server at $dbAddr (user/password/database as in" -ForegroundColor Yellow
    Write-Host "       ConnectionStrings:Default; demo default recall/recall/recall), or install Docker" -ForegroundColor Yellow
    Write-Host "       Desktop and re-run this script. To use another server, change the connection string:" -ForegroundColor Yellow
    Write-Host "       dotnet user-secrets set ""ConnectionStrings:Default"" ""Host=...;Port=...;..."" --project Recall.Api" -ForegroundColor Yellow
}

Write-Host ""
if ($dbReady) {
    Write-Host "Setup complete. Start the API with:" -ForegroundColor Cyan
} else {
    Write-Host "Setup incomplete: no database at $dbAddr. The API applies migrations at startup," -ForegroundColor Yellow
    Write-Host "so it cannot start until PostgreSQL runs there (see above). Once it does, start it with:" -ForegroundColor Yellow
}
Write-Host "  pwsh -File scripts/run.ps1" -ForegroundColor White
Write-Host "or:" -ForegroundColor Cyan
Write-Host "  dotnet run --project Recall.Api --urls http://localhost:15080" -ForegroundColor White
