#Requires -Version 7
# Harmless check for the visible elevated window: prints a few lines, who it ran as, and exits with the code given by -ExitCode.
param([int]$ExitCode = 0)

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$elevated = ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Write-Output "visible window check: $($identity.Name), elevated: $elevated"
Write-Output "console window title: $($Host.UI.RawUI.WindowTitle)"
Start-Sleep -Seconds 3
exit $ExitCode
