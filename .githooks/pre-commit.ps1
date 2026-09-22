#!/usr/bin/env pwsh
# pre-commit hook: blocks private/secret content from entering the public repo.
# Install:  copy .githooks\pre-commit .git\hooks\pre-commit ; git config core.hooksPath .githooks
# Bypass is possible; this is a guardrail, not proof. Inspect pushes manually.

$ErrorActionPreference = 'Stop'
$failures = @()

# 1. Forbidden paths (exact, case-insensitive).
$forbiddenPaths = @(
    'appsettings.Development.json',
    'AGENTS.md',
    '.env',
    'qa-',
    'handoff-',
    'design-system-proposal',
    'central.project.toml',
    'index.ps1'
)

# 2. Secret/credential patterns (targeted; avoid generic 'token'/'AI' false positives).
$secretPatterns = @(
    'sk-or-v1-[0-9a-f]{20,}',
    '-----BEGIN [A-Z ]*PRIVATE KEY-----',
    'ZGV2LXNlY3JldC1vbmx5',                    # known dev JWT secret prefix
    'Password\s*=\s*[^;"$]{6,}',               # inline DB passwords in connection strings
    'ghp_[A-Za-z0-9]{20,}',
    'gh_[A-Za-z0-9_]{20,}',
    'xox[baprs]-[A-Za-z0-9-]{10,}',
    '-----BEGIN OPENSSH PRIVATE KEY-----'
)

# 3. Private-workspace path fragments.
$privatePathFragments = @(
    'C:\rcm\_study',
    'C:/rcm/_study',
    'rcm\_study',
    'rcm/_study',
    'ses_[0-9a-f]{6,}'
)

$staged = @()
foreach ($line in (& git diff --cached --name-only --diff-filter=ACMR)) {
    if ($line) { $staged += $line.Trim() }
}
if ($staged.Count -eq 0) { exit 0 }

foreach ($file in $staged) {
    $lower = $file.ToLowerInvariant()

    foreach ($fp in $forbiddenPaths) {
        if ($lower -like "*$($fp.ToLowerInvariant())*") {
            $failures += "FORBIDDEN PATH: $file (matched '$fp')"
        }
    }

    # Read staged blob content (not working tree), text files only.
    $ext = [System.IO.Path]::GetExtension($lower)
    if (@('.png', '.jpg', '.jpeg', '.gif', '.ico', '.pdf', '.docx', '.zip', '.db') -contains $ext) {
        continue
    }
    $content = git show ":$file" 2>$null | Out-String
    if (-not $content) { continue }

    foreach ($sp in $secretPatterns) {
        if ($content -match $sp) {
            $failures += "SECRET PATTERN '$sp' in: $file"
        }
    }
    foreach ($frag in $privatePathFragments) {
        if ($content -like "*$frag*") {
            $failures += "PRIVATE PATH FRAGMENT '$frag' in: $file"
        }
    }
}

if ($failures.Count -gt 0) {
    Write-Host "pre-commit: BLOCKED — private/forbidden content detected:" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    Write-Host "If a rejection is a false positive, fix the file or bypass with: git commit --no-verify (then justify in the commit message)." -ForegroundColor Yellow
    exit 1
}
exit 0