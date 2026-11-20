<#
  Opens the Windows Firewall ports used by the Bridge Console's Share tab, on PRIVATE networks only.
  Run once per bench PC from an elevated PowerShell:   .\tools\firewall.ps1
  Remove with:                                          .\tools\firewall.ps1 -Remove
#>
param([switch]$Remove, [int]$Port = 8443)
$ErrorActionPreference = 'Stop'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this from an elevated (Administrator) PowerShell.'
}
$rules = 'HHT Bridge Console - remote view (TCP)', 'HHT Bridge Console - bench discovery (UDP)'
Get-NetFirewallRule -DisplayName $rules -ErrorAction SilentlyContinue | Remove-NetFirewallRule
if ($Remove) { Write-Host 'Rules removed.'; return }
New-NetFirewallRule -DisplayName $rules[0] -Direction Inbound -Protocol TCP -LocalPort $Port -Profile Private -Action Allow | Out-Null
New-NetFirewallRule -DisplayName $rules[1] -Direction Inbound -Protocol UDP -LocalPort 47811 -Profile Private -Action Allow | Out-Null
Write-Host "Allowed TCP $Port and UDP 47811 on Private networks. Make sure the shop network is set to Private, not Public."
