# scripts/common.ps1 - helpers shared by init.ps1 and run.ps1 (dot-sourced).
# Every dotnet call passes --project, so the caller's location never changes.

$RepoRoot = Split-Path -Parent $PSScriptRoot
$ApiDir = Join-Path $RepoRoot 'Recall.Api'
$ApiPort = 15080

# Processes listening on a local TCP port (none when free; Windows only).
function Get-PortOwner([int]$Port) {
    if (-not (Get-Command Get-NetTCPConnection -ErrorAction SilentlyContinue)) { return }
    Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty OwningProcess -Unique |
        ForEach-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue }
}

# True when the process runs this checkout's Recall.Api (dotnet run or the built exe).
function Test-OwnApiProcess($Process) {
    $cmdline = (Get-CimInstance Win32_Process -Filter "ProcessId=$($Process.Id)" -ErrorAction SilentlyContinue).CommandLine
    if (-not $cmdline) { return $false }
    $cmdline.Replace('/', '\').IndexOf("$ApiDir\", [StringComparison]::OrdinalIgnoreCase) -ge 0
}

# Host/port from ConnectionStrings:Default (user secrets, else environment); demo default localhost:15432.
function Get-DbTarget {
    $cs = dotnet user-secrets list --project $ApiDir 2>$null |
        ForEach-Object { if ($_ -match '^ConnectionStrings:Default\s*=\s*(.+)$') { $Matches[1] } } |
        Select-Object -First 1
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
