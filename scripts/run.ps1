#!/usr/bin/env pwsh
# scripts/run.ps1 - start the Recall API (uses the setup from scripts/init.ps1).
# Optional: -NoBuild to skip the build. Uses any PostgreSQL already reachable at the
# connection string's host:port; falls back to the Docker container from init.ps1.

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

# 2. Ensure PostgreSQL is reachable: an already running server (any local install)
#    is used as is; otherwise start the Docker container created by init.ps1.

# Host/port from ConnectionStrings:Default (user secrets, else environment); demo default localhost:15432.
function Get-DbTarget {
  $cs = dotnet user-secrets list 2>$null | ForEach-Object { if ($_ -match '^ConnectionStrings:Default\s*=\s*(.+)$') { $Matches[1] } } | Select-Object -First 1
  if (-not $cs) { $cs = $env:ConnectionStrings__Default }
  $target = [pscustomobject]@{ Host = 'localhost'; Port = 15432 }
  if ($cs) {
    if ($cs -match '(?i)(?:^|;)\s*(?:Host|Server)\s*=\s*([^;]+)') { $target.Host = $Matches[1].Trim() }
    $target.Port = if ($cs -match '(?i)(?:^|;)\s*Port\s*=\s*(\d+)') { [int]$Matches[1] } else { 5432 }
  }
  $target
}

# TCP probe with a short timeout (works in Windows PowerShell 5.1 and pwsh 7).
function Test-DbReachable([string]$DbHost, [int]$Port) {
  $client = New-Object System.Net.Sockets.TcpClient
  try { return $client.ConnectAsync($DbHost, $Port).Wait(1500) -and $client.Connected }
  catch { return $false }
  finally { $client.Dispose() }
}

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
    $why = if (Get-Command docker -ErrorAction SilentlyContinue) { 'the Docker container did not come up' } else { 'Docker is not installed to start the demo container' }
    Write-Warning "No PostgreSQL answers at $dbAddr ($why)."
    Write-Host "       The API starts anyway, but every database request fails until a PostgreSQL" -ForegroundColor Yellow
    Write-Host "       server runs at $dbAddr - start it, then restart the API. Details: scripts/init.ps1." -ForegroundColor Yellow
}

# 3. Run the API in this console (Ctrl+C to stop).
Write-Host "=== Recall API -> http://localhost:15080/swagger ===" -ForegroundColor Cyan
if ($NoBuild) {
    dotnet run --no-build --urls "http://localhost:15080"
} else {
    dotnet run --urls "http://localhost:15080"
}
Write-Host "API exited with code $LASTEXITCODE" -ForegroundColor Yellow