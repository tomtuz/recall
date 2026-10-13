# scripts/sync-api-docs.ps1 - regenerate the OpenAPI contract and sync the Postman collection.
# Usage (from any directory):  pwsh -File scripts\sync-api-docs.ps1
# Requires: .NET SDK + npm (npx). api-spec.* are the declared source of truth;
# the Postman collection is a generated artifact kept in sync with --sync.

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Interactive console script: colored status output.')]
param()

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$collection = Join-Path $root "postman\Recall.postman_collection.json"

# Work from the repository root; finally restores the caller's location.
Push-Location $root
try {
    Write-Host "1/2 Exporting OpenAPI spec (offline, no database)..." -ForegroundColor Cyan
    dotnet run --project Recall.Api --no-launch-profile -- --export-openapi "$root"
    if ($LASTEXITCODE -ne 0) { throw "OpenAPI export failed (exit $LASTEXITCODE)." }

    Write-Host "2/2 Syncing the Postman collection from api-spec.yaml..." -ForegroundColor Cyan
    $npxArgs = @('--yes', 'openapi-to-postmanv2@6.3.3', '-s', 'api-spec.yaml',
        '--sync', $collection, '-o', $collection, '--sync-options', 'syncExamples=true')
    npx @npxArgs
    if ($LASTEXITCODE -ne 0) { throw "Postman sync failed (exit $LASTEXITCODE)." }

    Write-Host "api-spec.json / api-spec.yaml / postman collection are in sync." -ForegroundColor Green
}
finally {
    Pop-Location
}
