# Builds the glasses client (if needed) and runs the server with the tray UI.
#
#   .\scripts\run.ps1          # real use, behind Caddy (Production settings)
#   .\scripts\run.ps1 -Dev     # local testing on http://127.0.0.1:5080 (Development settings)
#   .\scripts\run.ps1 -Lan     # like -Dev, but reachable from other devices on your home network
#   .\scripts\run.ps1 -Rebuild # force a fresh client build first
#
# -Lan serves plain HTTP on your LAN IP, for testing only. The router doesn't forward
# port 5080, so it isn't reachable from the internet. Run .\deploy\firewall.ps1 -LanTesting
# once (as admin) so Windows lets the other device in.

param(
    [switch]$Dev,
    [switch]$Lan,
    [switch]$Rebuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$client = Join-Path $root 'client-web'

if ($Rebuild -or -not (Test-Path (Join-Path $client 'dist\index.html'))) {
    Write-Host 'Building the glasses client...'
    Push-Location $client
    try {
        if (-not (Test-Path 'node_modules')) { npm install --no-fund --no-audit }
        npm run build
    }
    finally {
        Pop-Location
    }
}

$serverArgs = @()
if ($Lan) {
    $Dev = $true
    $lanIp = (Get-NetIPConfiguration | Where-Object { $_.IPv4DefaultGateway -and $_.NetAdapter.Status -eq 'Up' } |
        Select-Object -First 1).IPv4Address.IPAddress
    if (-not $lanIp) { throw 'Could not find this PC''s LAN IPv4 address.' }

    # Listen on the LAN address too, accept it as a host name, and allow the page served from it
    # to open the WebSockets. Index 10 appends to the origin list instead of replacing it.
    $serverArgs = @(
        '--urls', "http://127.0.0.1:5080;http://${lanIp}:5080",
        "--AllowedHosts=$lanIp;localhost;127.0.0.1",
        "--Web:AllowedOrigins:10=http://${lanIp}:5080"
    )

    if (-not (Get-NetFirewallRule -DisplayName 'Glasses - LAN testing' -ErrorAction SilentlyContinue)) {
        Write-Warning 'No LAN firewall rule yet: run .\deploy\firewall.ps1 -LanTesting in an admin PowerShell.'
    }
    Write-Host "On the other device open:  http://${lanIp}:5080" -ForegroundColor Green
}

$profile = if ($Dev) { 'dev' } else { 'server' }
if (-not $Dev -and -not (Test-Path (Join-Path $root 'server\appsettings.Local.json'))) {
    Write-Warning 'server\appsettings.Local.json not found: Media:PublicIp is unset, so the glasses will have no media address. See server\appsettings.Local.example.json.'
}

Write-Host "Starting the server ($profile profile)..."
dotnet run --project (Join-Path $root 'server') --launch-profile $profile -- @serverArgs
