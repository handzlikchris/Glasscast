# Adds the two inbound Windows Firewall rules the app needs.
# Run once from an elevated PowerShell:  .\deploy\firewall.ps1
# Remove them again with:                .\deploy\firewall.ps1 -Remove

param(
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'

$rules = @(
    @{ Name = 'Glasses - Caddy HTTPS';  Protocol = 'TCP'; Port = 443 },
    @{ Name = 'Glasses - WebRTC media'; Protocol = 'UDP'; Port = 50000 }
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

    if ($existing) {
        Write-Host "Already present: $($rule.Name)"
        continue
    }

    New-NetFirewallRule -DisplayName $rule.Name -Direction Inbound -Action Allow `
        -Protocol $rule.Protocol -LocalPort $rule.Port | Out-Null
    Write-Host "Added: $($rule.Name) ($($rule.Protocol) $($rule.Port))"
}
