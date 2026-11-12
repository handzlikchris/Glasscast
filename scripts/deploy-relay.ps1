# Publishes the phone relay and deploys it to an IIS site with Web Deploy, in one go:
#
#   .\scripts\deploy-relay.ps1 -Server relay.example.com -User deployer
#
# Or save the server and login once (you type the password; it's kept encrypted for your Windows
# account with DPAPI, in %LOCALAPPDATA%\GlassesRemote\deploy-relay.clixml), and from then on:
#
#   .\scripts\deploy-relay.ps1 -Save -Server relay.example.com -User deployer   # once
#   .\scripts\deploy-relay.ps1                                                   # every deploy
#
# It builds the page and the relay (scripts\publish-relay.ps1), then syncs the folder to the IIS
# site through Web Deploy (https://<Server>:8172): it stops the site's app pool for the copy (the
# running relay holds its files), copies only changed files, deletes nothing on the server
# (relay.Local.json, logs and the data folder stay), gives the app pool write access to data\
# (the registered phones), and starts the pool again, whatever happened.
# Then it checks https://<Server>/health and /features.
#
# Needs Web Deploy on this PC (msdeploy.exe) and, on the server, the Web Management Service plus
# Web Deploy, with TCP 8172 open to this PC only. Setup: architecture\deployment-and-networking.md,
# "Deploying updates with Web Deploy".
#
# The password: -Password, the saved login, the GLASSCAST_DEPLOY_PASSWORD environment variable, or a
# prompt. It is never printed.

param(
    [string]$Server,
    [string]$User,
    [string]$Site = 'GlasscastRelay',
    [int]$Port = 8172,
    [securestring]$Password,
    # Deploy the last publish as it is, without building again.
    [switch]$NoBuild,
    # Show what would change, change nothing.
    [switch]$WhatIf,
    # Save -Server, -User and a password typed now for later deploys, then stop.
    [switch]$Save
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$source = Join-Path $root 'publish\relay-win-x64'
$savedLogin = Join-Path $env:LOCALAPPDATA 'GlassesRemote\deploy-relay.clixml'

if ($Save) {
    if (-not $Server -or -not $User) { throw 'With -Save, give -Server and -User too.' }
    $typed = if ($Password) { $Password } else { Read-Host "Password for $User on $Server" -AsSecureString }
    New-Item -ItemType Directory -Force (Split-Path $savedLogin) | Out-Null
    # Export-Clixml encrypts the SecureString with DPAPI: only this Windows account on this PC can read it.
    [pscustomobject]@{ Server = $Server; Credential = New-Object PSCredential($User, $typed) } | Export-Clixml $savedLogin
    Write-Host "Saved the login for $User on $Server in $savedLogin (encrypted for this Windows account)." -ForegroundColor Green
    return
}

if (-not $Server -or -not $User) {
    if (-not (Test-Path $savedLogin)) {
        throw 'Give -Server and -User, or save them once: .\scripts\deploy-relay.ps1 -Save -Server <host> -User <user>'
    }
    $saved = Import-Clixml $savedLogin
    if (-not $Server) { $Server = $saved.Server }
    if (-not $User) {
        $User = $saved.Credential.UserName
        if (-not $Password) { $Password = $saved.Credential.Password }
    }
}

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
$remote = "computerName=`"$endpoint`",userName=`"$User`",password=`"$plain`",authType=`"Basic`""
# The Web Management Service uses a self-signed certificate unless you give it another one.
$common = @('-allowUntrusted')

# Stops or starts the site's app pool on the server (Web Deploy's recycleApp provider).
function Set-RemotePool([string]$mode) {
    & $msdeploy '-verb:sync' '-source:recycleApp' "-dest:recycleApp=`"$Site`",recycleMode=`"$mode`",$remote" @common
    if ($LASTEXITCODE -ne 0) { throw "Couldn't $mode on the server (exit $LASTEXITCODE)." }
}

$copy = @(
    '-verb:sync',
    "-source:contentPath=`"$source`"",
    "-dest:contentPath=`"$Site`",$remote",
    # Never delete on the server: relay.Local.json, logs and old page assets stay.
    '-enableRule:DoNotDeleteRule',
    # Nor overwrite the server's registered phones with a local test run's (the folder itself goes).
    '-skip:objectName=filePath,absolutePath=\\data\\',
    # A file the stopping relay still holds is tried again a few times.
    '-retryAttempts:10',
    '-retryInterval:2000'
) + $common

if ($WhatIf) {
    & $msdeploy @copy '-whatif'
    $plain = $null
    return
}

# The relay keeps its program files open while it runs, and its long-lived connections (the
# phones' companions) can keep it from letting go quickly when only told to go offline, so stop
# its app pool for the copy, and always start it again.
Write-Host "Stopping $Site on $Server..."
Set-RemotePool 'StopAppPool'
try {
    Write-Host "Copying $source..."
    & $msdeploy @copy
    $copied = $LASTEXITCODE

    # The relay keeps the registered phones in data\ next to it: the app pool needs Modify there.
    # setAcl without a user grants it to the site's app pool identity. Harmless when already set.
    if ($copied -eq 0) {
        & $msdeploy '-verb:sync' '-source:setAcl' "-dest:setAcl=`"$Site/data`",setAclAccess=Modify,$remote" @common
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "Couldn't give the app pool write access to $Site\data (exit $LASTEXITCODE): registered phones won't survive a restart. On the server: icacls <site folder>\data /grant `"IIS AppPool\${Site}:(OI)(CI)M`""
        }
    }

    # An app_offline.htm left on the server (Web Deploy's AppOffline rule, from an interrupted
    # deploy) would keep the site showing "Site Under Construction": remove it if it's there.
    # Usually there's none, and Web Deploy reports that on stderr (FileOrFolderNotFound): not an error here.
    $ErrorActionPreference = 'Continue'
    $cleanup = & $msdeploy '-verb:delete' "-dest:contentPath=`"$Site/app_offline.htm`",$remote" @common 2>&1
    $ErrorActionPreference = 'Stop'
    $cleanup | Where-Object { "$_" -match 'Deleting' } | ForEach-Object { Write-Host "$_" }
}
finally {
    Write-Host "Starting $Site..."
    Set-RemotePool 'StartAppPool'
    $plain = $null
}
if ($copied -ne 0) { throw "Web Deploy failed to copy (exit $copied); the old version is running again." }

# The relay starts on the first request after the copy.
$health = Invoke-WebRequest "https://$Server/health" -UseBasicParsing -TimeoutSec 60
$features = Invoke-WebRequest "https://$Server/features" -UseBasicParsing -TimeoutSec 30
Write-Host "health: $($health.Content)   features: $($features.Content)"
if ($health.Content -ne 'ok') { throw 'The relay did not answer ok after the deploy.' }
Write-Host 'Deployed.' -ForegroundColor Green
