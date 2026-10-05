# Publishes the headless phone relay (relay-server/) with the glasses page, ready to copy to a
# server and run behind Caddy. It serves the page and relays the first connection between
# glasses and phones, nothing else: no capture, input, window or media code is in it, so it
# can't control the machine it runs on.
#
#   .\scripts\publish-relay.ps1                       # Windows x64, self-contained
#   .\scripts\publish-relay.ps1 -Runtime linux-x64    # or linux-arm64
#   .\scripts\publish-relay.ps1 -Output D:\glasscast-relay
#
# Self-contained: the server needs no .NET installed. On the server, next to the program, put
# your host name in relay.Local.json:   { "Web": { "PublicHost": "relay.example.com" } }
# then run start-relay.ps1 (Windows) or start-relay.sh (Linux), and Caddy with
# deploy\Caddyfile.relay. See architecture\deployment-and-networking.md, "Hosted phone relay".

param(
    [string]$Runtime = 'win-x64',
    [string]$Output = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$client = Join-Path $root 'client-web'
if (-not $Output) { $Output = Join-Path $root "publish\relay-$Runtime" }

Write-Host 'Building the glasses page...'
Push-Location $client
try {
    if (-not (Test-Path 'node_modules')) { npm install --no-fund --no-audit }
    # Its own folder: dist is what a running PC server serves to the glasses.
    npm run build -- --outDir dist-relay --emptyOutDir
    if ($LASTEXITCODE -ne 0) { throw 'The glasses page did not build' }
}
finally {
    Pop-Location
}

Write-Host "Publishing the relay for $Runtime to $Output..."
dotnet publish (Join-Path $root 'relay-server') -c Release -r $Runtime --self-contained -o $Output
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

$wwwroot = Join-Path $Output 'wwwroot'
if (Test-Path $wwwroot) { Remove-Item -Recurse -Force $wwwroot }
Copy-Item -Recurse (Join-Path $client 'dist-relay') $wwwroot
Copy-Item (Join-Path $root 'deploy\relay\start-relay.ps1') $Output
Copy-Item (Join-Path $root 'deploy\relay\start-relay.sh') $Output

if (-not (Test-Path (Join-Path $Output 'relay.Local.json'))) {
    Write-Warning "No relay.Local.json in $Output yet: add one with Web:PublicHost before starting it on the server."
}
Write-Host "Done: $Output" -ForegroundColor Green
