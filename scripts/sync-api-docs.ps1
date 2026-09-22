# scripts/sync-api-docs.ps1 - regenerate the OpenAPI contract and sync the Postman collection.
# Usage (from repo root):  pwsh -File scripts\sync-api-docs.ps1
# Requires: .NET SDK + npm (npx). api-spec.* are the declared source of truth;
# the Postman collection is a generated artifact kept in sync with --sync.

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $root

Write-Host "1/2 Exporting OpenAPI spec (offline, no database)..." -ForegroundColor Cyan
dotnet run --project Recall.Api --no-launch-profile -- --export-openapi "$root"
if ($LASTEXITCODE -ne 0) { throw "OpenAPI export failed (exit $LASTEXITCODE)." }

Write-Host "2/2 Syncing the Postman collection from api-spec.yaml..." -ForegroundColor Cyan
npx --yes openapi-to-postmanv2@6.3.3 -s api-spec.yaml `
  --sync (Join-Path $root "postman\Recall.postman_collection.json") `
  -o (Join-Path $root "postman\Recall.postman_collection.json") `
  --sync-options syncExamples=true
if ($LASTEXITCODE -ne 0) { throw "Postman sync failed (exit $LASTEXITCODE)." }

Write-Host "api-spec.json / api-spec.yaml / postman collection are in sync." -ForegroundColor Green