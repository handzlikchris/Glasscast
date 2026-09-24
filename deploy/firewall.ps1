# Adds the inbound Windows Firewall rules the app needs.
# Run from an elevated PowerShell:
#   .\deploy\firewall.ps1               # TCP 8443 (Caddy) and UDP 50000 (WebRTC media)
#   .\deploy\firewall.ps1 -LanTesting   # also TCP 5080 from your local subnet only (scripts\run.ps1 -Lan)
#   .\deploy\firewall.ps1 -Remove       # remove all of them again
#
# The router forwards external TCP 443 to this PC's 8443 (IIS keeps 443 locally), so Caddy's
# rule is for 8443. Re-running the script fixes a rule whose port has changed.

param(
    [switch]$LanTesting,
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'

$rules = @(
    @{ Name = 'Glasses - Caddy HTTPS';  Protocol = 'TCP'; Port = 8443;  Remote = 'Any' },
    @{ Name = 'Glasses - WebRTC media'; Protocol = 'UDP'; Port = 50000; Remote = 'Any' },
    # Plain-HTTP test server: home network only, never the internet.
    @{ Name = 'Glasses - LAN testing';  Protocol = 'TCP'; Port = 5080;  Remote = 'LocalSubnet'; LanOnly = $true }
)

foreach ($rule in $rules) {
    $existing = Get-NetFirewallRule -DisplayName $rule.Name -ErrorAction SilentlyContinue

    if ($Remove) {
        if ($existing) {
            Remove-NetFirewallRule -DisplayName $rule.Name
            Write-Host "Removed: $($rule.Name)"
        }
        continue
    }

    if ($rule.LanOnly -and -not $LanTesting) {
        continue
    }

    if ($existing) {
        $port = ($existing | Get-NetFirewallPortFilter).LocalPort
        if ("$port" -ne "$($rule.Port)") {
            $existing | Get-NetFirewallPortFilter | Set-NetFirewallPortFilter -LocalPort $rule.Port
            Write-Host "Updated: $($rule.Name) (port $port -> $($rule.Port))"
        }
        else {
            Write-Host "Already present: $($rule.Name)"
        }
        continue
    }

    New-NetFirewallRule -DisplayName $rule.Name -Direction Inbound -Action Allow `
        -Protocol $rule.Protocol -LocalPort $rule.Port -RemoteAddress $rule.Remote | Out-Null
    Write-Host "Added: $($rule.Name) ($($rule.Protocol) $($rule.Port), from $($rule.Remote))"
}
