# Spins up a throwaway Umbraco 17 site (SQLite) that references the plugin, for end-to-end checks.
# Usage (repo root, PowerShell):  .\test\setup-local-test.ps1
$ErrorActionPreference = 'Stop'
$repo    = Split-Path -Parent $PSScriptRoot
$siteDir = Join-Path $repo 'test\TestSite'

dotnet new install Umbraco.Templates::17.0.0 --force
if (Test-Path $siteDir) { Remove-Item -Recurse -Force $siteDir }
dotnet new umbraco --force -n TestSite -o $siteDir --friendly-name "Administrator" --email "admin@example.com" `
    --password "P@ssw0rd1234" --development-database-type SQLite
dotnet add $siteDir reference (Join-Path $repo 'src\Lait.Umbraco.Awesome.SEO\Lait.Umbraco.Awesome.SEO.csproj')
Write-Host "Configure LaitSeo:Sites in test\TestSite\appsettings.json, then: dotnet run --project test\TestSite"
