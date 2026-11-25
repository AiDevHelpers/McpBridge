#Requires -Version 7
# Harmless check that a script ran through the Script Helper broker with administrator rights. Prints who it ran as and exits 0.
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$elevated = ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Write-Output "ran as $($identity.Name); elevated: $elevated"
exit 0
