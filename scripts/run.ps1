# Builds the glasses client (if needed) and runs the server with the tray UI.
#
#   .\scripts\run.ps1          # real use, behind Caddy (Production settings)
#   .\scripts\run.ps1 -Dev     # local testing on http://127.0.0.1:5080 (Development settings)
#   .\scripts\run.ps1 -Rebuild # force a fresh client build first

param(
    [switch]$Dev,
    [switch]$Rebuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$client = Join-Path $root 'client-web'

if ($Rebuild -or -not (Test-Path (Join-Path $client 'dist\index.html'))) {
    Write-Host 'Building the glasses client…'
    Push-Location $client
    try {
        if (-not (Test-Path 'node_modules')) { npm install --no-fund --no-audit }
        npm run build
    }
    finally {
        Pop-Location
    }
}

$profile = if ($Dev) { 'dev' } else { 'server' }
if (-not $Dev -and -not (Test-Path (Join-Path $root 'server\appsettings.Local.json'))) {
    Write-Warning 'server\appsettings.Local.json not found: Media:PublicIp is unset, so the glasses will have no media address. See server\appsettings.Local.example.json.'
}

Write-Host "Starting the server ($profile profile) on http://127.0.0.1:5080 …"
dotnet run --project (Join-Path $root 'server') --launch-profile $profile
