# Starts the Glasscast phone relay from its own folder (where relay.json, relay.Local.json and
# wwwroot are). Copied next to the program by scripts\publish-relay.ps1.
#
#   .\start-relay.ps1
#
# It listens on 127.0.0.1:5090 only; Caddy (deploy\Caddyfile.relay) is the public side.

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
if (-not (Test-Path 'relay.Local.json') -and -not $env:Web__PublicHost) {
    Write-Warning 'No relay.Local.json and no Web__PublicHost: the relay will refuse your host name.'
}
& (Join-Path $PSScriptRoot 'GlassesRemote.RelayServer.exe') @args
