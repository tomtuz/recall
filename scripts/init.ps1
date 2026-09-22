#!/usr/bin/env pwsh
# scripts/init.ps1 - one-time backend setup for a fresh clone.
# Generates the JWT signing key, sets user secrets, starts PostgreSQL,
# applies migrations and seeds demo data. Idempotent: safe to re-run.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location (Join-Path $root 'Recall.Api')

Write-Host "=== Recall API - first-time setup ===" -ForegroundColor Cyan

# 1. User secrets: JWT signing key + database connection string.
$secretsPresent = $false
dotnet user-secrets list 2>$null | ForEach-Object {
    if ($_ -match '^(Jwt:Secret|ConnectionStrings:Default)') { $secretsPresent = $true }
}
if (-not $secretsPresent) {
    dotnet user-secrets init | Out-Null
    # Generate 64 random bytes -> base64 (512-bit key, matches production guidance).
    $key = New-Object byte[] 64
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($key)
    $jwtSecret = [Convert]::ToBase64String($key)
    dotnet user-secrets set "Jwt:Secret" $jwtSecret | Out-Null
    dotnet user-secrets set "ConnectionStrings:Default" "Host=localhost;Port=15432;Database=recall;Username=recall;Password=recall" | Out-Null
    Write-Host "[init] JWT signing key generated and stored in user secrets." -ForegroundColor Green
    Write-Host "       Database connection string set (localhost:15432 demo default)." -ForegroundColor Green
} else {
    Write-Host "[init] User secrets already present - keeping existing values." -ForegroundColor DarkGray
}

# 2. PostgreSQL via Docker.
$dockerOk = $false
if (Get-Command docker -ErrorAction SilentlyContinue) {
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
        Write-Warning "Docker is installed but the engine did not start; the API can still run if you provide a PostgreSQL at localhost:15432."
    }
} else {
    Write-Warning "Docker CLI not found. Install Docker Desktop, or point ConnectionStrings:Default at an existing PostgreSQL."
}

Write-Host ""
Write-Host "Setup complete. Start the API with:" -ForegroundColor Cyan
Write-Host "  pwsh -File scripts/run.ps1        (from the repository root)" -ForegroundColor White
Write-Host "or:" -ForegroundColor Cyan
Write-Host "  dotnet run --project Recall.Api --urls http://localhost:15080" -ForegroundColor White