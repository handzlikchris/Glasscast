# Publishes the phone relay and deploys it to an IIS site with Web Deploy, in one go:
#
#   .\scripts\deploy-relay.ps1 -Server relay.example.com -User deployer
#
# It builds the page and the relay (scripts\publish-relay.ps1), then syncs the folder to the IIS
# site through Web Deploy (https://<Server>:8172): the site goes offline for the copy
# (app_offline.htm, so the relay's files aren't locked), only changed files move, and nothing on
# the server is deleted (relay.Local.json, logs and the data folder stay). Then it checks
# https://<Server>/health and /features.
#
# Needs Web Deploy on this PC (msdeploy.exe) and, on the server, the Web Management Service plus
# Web Deploy, with TCP 8172 open to this PC only. Setup: architecture\deployment-and-networking.md,
# "Deploying updates with Web Deploy".
#
# The password: -Password, the GLASSCAST_DEPLOY_PASSWORD environment variable, or a prompt.

param(
    [Parameter(Mandatory = $true)][string]$Server,
    [Parameter(Mandatory = $true)][string]$User,
    [string]$Site = 'GlasscastRelay',
    [int]$Port = 8172,
    [securestring]$Password,
    # Deploy the last publish as it is, without building again.
    [switch]$NoBuild,
    # Show what would change, change nothing.
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$source = Join-Path $root 'publish\relay-win-x64'

$msdeploy = @(
    "$env:ProgramFiles\IIS\Microsoft Web Deploy V3\msdeploy.exe",
    "${env:ProgramFiles(x86)}\IIS\Microsoft Web Deploy V3\msdeploy.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $msdeploy) { throw 'msdeploy.exe not found: install Web Deploy on this PC (https://www.iis.net/downloads/microsoft/web-deploy).' }

if (-not $NoBuild) {
    & (Join-Path $PSScriptRoot 'publish-relay.ps1') -Runtime win-x64 -Output $source
}
if (-not (Test-Path (Join-Path $source 'web.config'))) { throw "Nothing to deploy in $source (no web.config)." }

if (-not $Password) {
    if ($env:GLASSCAST_DEPLOY_PASSWORD) {
        $Password = ConvertTo-SecureString $env:GLASSCAST_DEPLOY_PASSWORD -AsPlainText -Force
    }
    else {
        $Password = Read-Host "Password for $User on $Server" -AsSecureString
    }
}
$plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($Password))

$endpoint = "https://${Server}:$Port/msdeploy.axd?site=$Site"
$arguments = @(
    '-verb:sync',
    "-source:contentPath=`"$source`"",
    "-dest:contentPath=`"$Site`",computerName=`"$endpoint`",userName=`"$User`",password=`"$plain`",authType=`"Basic`"",
    # The Web Management Service uses a self-signed certificate unless you give it another one.
    '-allowUntrusted',
    # Take the site offline while copying, so the running relay doesn't hold its files.
    '-enableRule:AppOffline',
    # Never delete on the server: relay.Local.json, logs and old page assets stay.
    '-enableRule:DoNotDeleteRule',
    '-retryAttempts:3'
)
if ($WhatIf) { $arguments += '-whatif' }

Write-Host "Deploying $source to $Site on $Server..."
& $msdeploy @arguments
$plain = $null
if ($LASTEXITCODE -ne 0) { throw "Web Deploy failed (exit $LASTEXITCODE)." }
if ($WhatIf) { return }

# The relay starts on the first request after the copy.
$health = Invoke-WebRequest "https://$Server/health" -UseBasicParsing -TimeoutSec 60
$features = Invoke-WebRequest "https://$Server/features" -UseBasicParsing -TimeoutSec 30
Write-Host "health: $($health.Content)   features: $($features.Content)"
if ($health.Content -ne 'ok') { throw 'The relay did not answer ok after the deploy.' }
Write-Host 'Deployed.' -ForegroundColor Green
