# Starts Caddy in front of the Glasscast phone relay, from the relay's folder (copied there by
# scripts\publish-relay.ps1 with caddy.exe and Caddyfile.relay). The host name comes from
# relay.Local.json (Web:PublicHost), so it's set in one place.
#
#   .\start-caddy.ps1                    # HTTPS on 443
#   .\start-caddy.ps1 -HttpsPort 8443    # when something else (IIS) holds 443 on this machine
#
# Public TCP 443 must reach this port: Let's Encrypt checks it (TLS-ALPN) for the certificate.

param(
    [int]$HttpsPort = 443
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$local = Join-Path $PSScriptRoot 'relay.Local.json'
if (-not (Test-Path $local)) { throw 'relay.Local.json not found: add { "Web": { "PublicHost": "relay.example.com" } } first.' }
$hostName = (Get-Content $local -Raw | ConvertFrom-Json).Web.PublicHost
if (-not $hostName) { throw 'relay.Local.json has no Web.PublicHost.' }

$env:RELAY_HOST = $hostName
$env:HTTPS_PORT = "$HttpsPort"
Write-Host "Caddy for https://$hostName (port $HttpsPort) -> 127.0.0.1:5090"
& (Join-Path $PSScriptRoot 'caddy.exe') run --config (Join-Path $PSScriptRoot 'Caddyfile.relay')
