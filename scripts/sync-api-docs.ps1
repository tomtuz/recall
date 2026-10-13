# scripts/sync-api-docs.ps1 - regenerate the OpenAPI contract (api-spec.json + api-spec.yaml).
# Usage (from any directory):  pwsh -File scripts\sync-api-docs.ps1 [-Postman]
# Requires: .NET SDK; -Postman also needs npm (npx). api-spec.* are the source of truth.
# The Postman collection is curated by hand (ordered demo flow + test scripts). -Postman
# re-syncs it with openapi-to-postmanv2, which adds generated folders and rewrites every
# request: review that diff before keeping it.

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Interactive console script: colored status output.')]
param([switch]$Postman)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$collection = Join-Path $root "postman\Recall.postman_collection.json"

# Work from the repository root; finally restores the caller's location.
Push-Location $root
try {
    Write-Host "Exporting OpenAPI spec (offline, no database)..." -ForegroundColor Cyan
    dotnet run --project Recall.Api --no-launch-profile -- --export-openapi "$root"
    if ($LASTEXITCODE -ne 0) { throw "OpenAPI export failed (exit $LASTEXITCODE)." }

    if ($Postman) {
        Write-Host "Syncing the Postman collection from api-spec.yaml..." -ForegroundColor Cyan
        $npxArgs = @('--yes', 'openapi-to-postmanv2@6.3.3', '-s', 'api-spec.yaml',
            '--sync', $collection, '-o', $collection, '--sync-options', 'syncExamples=true')
        npx @npxArgs
        if ($LASTEXITCODE -ne 0) { throw "Postman sync failed (exit $LASTEXITCODE)." }
        Write-Host "Review 'git diff postman/' before committing the synced collection." -ForegroundColor Yellow
    }

    Write-Host "api-spec.json / api-spec.yaml regenerated." -ForegroundColor Green
}
finally {
    Pop-Location
}
