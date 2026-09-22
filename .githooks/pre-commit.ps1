#!/usr/bin/env pwsh
# pre-commit hook: blocks private/secret content from entering the public repo.
# Bypass is possible; this is a guardrail, not proof. Inspect pushes manually.
#
# False-positive policy:
# - Markdown fenced code blocks are skipped entirely (documentation examples).
# - The documented local Docker demo credential (User=recall + Password=recall
#   on localhost) is allowlisted; passwords paired with non-localhost hosts
#   still fail.
# Real secrets (private keys, provider tokens, the dev JWT prefix, non-local
# DB passwords, private workspace paths) always block.

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
    'ghp_[A-Za-z0-9]{20,}',
    'gh_[A-Za-z0-9_]{20,}',
    'xox[baprs]-[A-Za-z0-9-]{10,}',
    '-----BEGIN OPENSSH PRIVATE KEY-----'
)

# 3. Inline password pattern: flags any non-localhost DB password. The
#    documented local demo credential (User=recall, Password=recall) is
#    allowed when the same line/URI clearly points at localhost.
$passwordPattern = 'Password\s*=\s*[^;"''\s]{3,}'
$localhostEvidence = 'localhost|127\.0\.0\.1|::1'

# 4. Private-workspace path fragments.
$privatePathFragments = @(
    'C:\rcm\_study',
    'C:/rcm/_study',
    'rcm\_study',
    'rcm/_study',
    'ses_[0-9a-f]{6,}'
)

# Markdown fenced-code-block lines are documentation examples, not secrets.
function Test-InFencedBlock {
    param([string]$Text, [int]$Position)
    $before = $Text.Substring(0, $Position)
    $fenceCount = ([regex]::Matches($before, '(?m)^\s*```')).Count
    return ($fenceCount % 2 -eq 1)
}

$staged = @()
foreach ($line in (& git diff --cached --name-only --diff-filter=ACMR)) {
    if ($line) { $staged += $line.Trim() }
}
if ($staged.Count -eq 0) { exit 0 }

foreach ($file in $staged) {
    $lower = $file.ToLowerInvariant()

    # The scanner's own source legitimately contains the patterns themselves;
    # skip .githooks files from content scanning (path rules still apply above).
    if ($lower -like '*.githooks*') { continue }

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
    $content = (git show ":$file" 2>$null) -join "`n"
    if (-not $content) { continue }

    $isMd = $ext -eq '.md'

    foreach ($sp in $secretPatterns) {
        foreach ($m in [regex]::Matches($content, $sp)) {
            if ($isMd -and (Test-InFencedBlock $content $m.Index)) { continue }
            $failures += "SECRET PATTERN '$sp' in: $file"
            break
        }
    }

    foreach ($pm in [regex]::Matches($content, $passwordPattern, 'IgnoreCase')) {
        if ($isMd -and (Test-InFencedBlock $content $pm.Index)) { continue }
        # Local demo credential allowance: the surrounding context must show
        # this is a localhost connection (same line or same fenced block).
        $lineStart = $content.LastIndexOf("`n", [Math]::Max(0, $pm.Index - 1)) + 1
        $lineEnd = $content.IndexOf("`n", $pm.Index)
        if ($lineEnd -lt 0) { $lineEnd = $content.Length }
        $line = $content.Substring($lineStart, $lineEnd - $lineStart)
        $context = if ($isMd) { $content.Substring(0, [Math]::Min($content.Length, $pm.Index + 400)) } else { $line }
        if ($context -match $localhostEvidence -and $pm.Value -match '(?i)recall') {
            continue  # documented local Docker demo default
        }
        $failures += "INLINE PASSWORD in: $file ('$($pm.Value.Substring(0, [Math]::Min(20, $pm.Value.Length)))...')"
    }

    foreach ($frag in $privatePathFragments) {
        if ($content -like "*$frag*") {
            $failures += "PRIVATE PATH FRAGMENT '$frag' in: $file"
        }
    }
}

if ($failures.Count -gt 0) {
    Write-Host "pre-commit: BLOCKED - private/forbidden content detected:" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    Write-Host "If this is a false positive, adjust the hook allowlist (.githooks/pre-commit.ps1) - do not habituate --no-verify." -ForegroundColor Yellow
    exit 1
}
exit 0