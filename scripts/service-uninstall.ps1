# Removes the AiDevHelpers Host service and its files under %ProgramFiles%\AiDevHelpers (unit 3). Runs elevated and checks its own SHA-256
# against scripts/service-uninstall.sha256 first, like service-install.ps1. Leaves %ProgramData% alone: logs and approvals stay.
param([switch]$WritePin)

$self = Join-Path $PSScriptRoot 'service-uninstall.ps1'
$pinFile = Join-Path $PSScriptRoot 'service-uninstall.sha256'
$actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $self).Hash
if ($WritePin) { Set-Content -LiteralPath $pinFile -Value $actual -Encoding ascii; Write-Output "pinned $actual"; exit 0 }
if (-not (Test-Path -LiteralPath $pinFile)) { Write-Error 'No pin file. Review the script, then run it with -WritePin.'; exit 4 }
$pinned = (Get-Content -LiteralPath $pinFile -Raw).Trim()
if ($pinned -ne $actual) { Write-Error "service-uninstall.ps1 hash $actual does not match the pin $pinned. Review the change, then run with -WritePin."; exit 4 }

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error 'refused: removing the service needs an elevated shell.'
    exit 1
}

$serviceName = 'AiDevHelpers'
if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    & sc.exe stop $serviceName | Out-Null
    Start-Sleep -Seconds 3
    & sc.exe delete $serviceName | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Error "sc.exe delete failed with $LASTEXITCODE."; exit 5 }
}

$target = Join-Path $env:ProgramFiles 'AiDevHelpers'
if (Test-Path -LiteralPath $target) {
    Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction Stop
}

Write-Output "uninstalled $serviceName; $target removed."
exit 0
