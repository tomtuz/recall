#!/usr/bin/env pwsh
# index.ps1 - Recall quick launcher (run from any directory: .\index.ps1 or pwsh -File index.ps1).
#
#   .\index.ps1          start (or restart) the API in a new Windows Terminal tab
#   .\index.ps1 stop     stop this checkout's running API
#   .\index.ps1 status   Docker / PostgreSQL / API port report
#   .\index.ps1 test     Playwright API tests against the running API (starts one if needed)
#
# The API tab runs scripts/run.ps1, which sets up user secrets and PostgreSQL on
# first use. A stale API from this checkout is stopped first; any other process
# on the port aborts the launch instead of being touched.

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Interactive launcher: colored console output is the UI.')]
param([Parameter(Position = 0)][ValidateSet('start', 'stop', 'status', 'test')][string]$Action = 'start')

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'scripts\common.ps1')

function Wait-Port([int]$Port, [bool]$Up, [int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        if ([bool](Get-PortOwner $Port) -eq $Up) { return $true }
        Start-Sleep -Milliseconds 500
    }
    $false
}

function Stop-Api {
    $owners = @(Get-PortOwner $ApiPort)
    if (-not $owners) { Write-Host "API not running (port $ApiPort free)." -ForegroundColor DarkGray; return }
    foreach ($p in $owners) {
        if (-not (Test-OwnApiProcess $p)) {
            throw "Port $ApiPort is held by unrelated process '$($p.ProcessName)' (PID $($p.Id)) - not touching it."
        }
    }
    foreach ($p in $owners) {
        Stop-Process -Id $p.Id -Force
        Write-Host "Stopped the running API (PID $($p.Id))." -ForegroundColor Yellow
    }
    if (-not (Wait-Port $ApiPort $false 10)) { throw "Port $ApiPort did not free up." }
}

function Start-Api {
    Stop-Api
    $run = Join-Path $PSScriptRoot 'scripts\run.ps1'
    # pwsh -File keeps wt.exe from splitting the command on semicolons.
    if (Get-Command wt.exe -ErrorAction SilentlyContinue) {
        & wt.exe -w 0 new-tab --title 'Recall API' pwsh -NoProfile -NoExit -File $run
    } else {
        Start-Process pwsh -ArgumentList '-NoProfile', '-NoExit', '-File', $run
    }
    Write-Host "Starting the API in a new terminal tab..." -ForegroundColor Cyan
    if (Wait-Port $ApiPort $true 120) {
        Write-Host "API up -> http://localhost:$ApiPort/swagger" -ForegroundColor Green
    } else {
        Write-Warning "Port $ApiPort did not open within 120 s - check the 'Recall API' tab."
    }
}

function Show-Status {
    $db = Get-DbTarget
    $engine = if (Get-Command docker -ErrorAction SilentlyContinue) {
        docker info *> $null; if ($LASTEXITCODE -eq 0) { 'running' } else { 'stopped' }
    } else { 'not installed' }
    $container = if ($engine -eq 'running') { docker inspect -f '{{.State.Status}}' recall-pg 2>$null } else { $null }
    $api = Get-PortOwner $ApiPort | Select-Object -First 1
    Write-Host ("Docker engine      : {0}" -f $engine)
    Write-Host ("recall-pg          : {0}" -f $(if ($container) { $container } else { 'n/a' }))
    Write-Host ("PostgreSQL         : {0} at {1}:{2}" -f $(if (Test-DbReachable $db.Host $db.Port) { 'reachable' } else { 'not reachable' }), $db.Host, $db.Port)
    Write-Host ("API :{0}         : {1}" -f $ApiPort, $(if ($api) { "running (PID $($api.Id))" } else { 'stopped' }))
}

function Invoke-Tests {
    $testDir = Join-Path $PSScriptRoot 'tests\api'
    Push-Location $testDir
    try {
        if (-not (Test-Path 'node_modules')) { npm ci; if ($LASTEXITCODE -ne 0) { throw 'npm ci failed.' } }
        # Reuse a running API; otherwise Playwright starts and stops its own.
        $env:RECALL_REUSE_API = if (Get-PortOwner $ApiPort) { '1' } else { $null }
        npm test
        $code = $LASTEXITCODE
        Write-Host "HTML report: npm run show-report (in tests/api)" -ForegroundColor DarkGray
        exit $code
    } finally {
        Remove-Item Env:RECALL_REUSE_API -ErrorAction SilentlyContinue
        Pop-Location
    }
}

switch ($Action) {
    'start' { Start-Api }
    'stop' { Stop-Api }
    'status' { Show-Status }
    'test' { Invoke-Tests }
}
