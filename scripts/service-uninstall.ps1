# Removes the AiDevHelpers Windows service, its install folder and, unless -KeepData, the HTTP MCP token and endpoint folder.
# It runs elevated, so it refuses to run unless its hash matches the pin committed in scripts/service-uninstall.sha256.
# To accept a reviewed change, run it once with -WritePin and commit the pin.
# -DryRun prints what it would do (lines that start with "PLAN:") and changes nothing.
# The install folder is only removed when it holds install-manifest.json, which the install script writes, so a wrong
# path cannot delete an unrelated folder. The tray's run-at-login entry belongs to the user and is removed by the tray.
param(
    [string]$InstallDirectory = (Join-Path $env:ProgramFiles 'AiDevHelpers'),
    [string]$DataDirectory = (Join-Path $env:ProgramData 'AiDevHelpers\mcp-http'),
    [string]$ServiceName = 'AiDevHelpers',
    [switch]$KeepData,
    [switch]$DryRun,
    [switch]$WritePin
)

$ErrorActionPreference = 'Stop'

$self = Join-Path $PSScriptRoot 'service-uninstall.ps1'
$pinFile = Join-Path $PSScriptRoot 'service-uninstall.sha256'
$actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $self).Hash
if ($WritePin) { Set-Content -LiteralPath $pinFile -Value $actual -Encoding ascii; Write-Output "pinned $actual"; exit 0 }
if (-not (Test-Path -LiteralPath $pinFile)) { Write-Error 'No pin file. Run with -WritePin after review.'; exit 1 }
$pinned = (Get-Content -LiteralPath $pinFile -Raw).Trim()
if ($pinned -ne $actual) { Write-Error "service-uninstall.ps1 hash $actual does not match the pin $pinned. Review the change, then run with -WritePin."; exit 1 }

function Fail([string]$message) { Write-Error $message; exit 1 }

$marker = Join-Path $InstallDirectory 'install-manifest.json'
$removeInstall = Test-Path -LiteralPath $InstallDirectory
if ($removeInstall -and -not (Test-Path -LiteralPath $marker)) {
    Fail "$InstallDirectory has no install-manifest.json, so it is not an AiDevHelpers install and will not be removed."
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
$removeData = (-not $KeepData) -and (Test-Path -LiteralPath $DataDirectory)

if ($DryRun) {
    if ($existing) {
        Write-Output "PLAN: stop service $ServiceName"
        Write-Output "PLAN: delete service $ServiceName"
    }
    if ($removeInstall) { Write-Output "PLAN: remove directory $InstallDirectory" }
    if ($removeData) { Write-Output "PLAN: remove directory $DataDirectory" }
    exit 0
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Fail 'This script needs administrator rights.'
}

if ($existing) {
    if ($existing.Status -ne 'Stopped') { Stop-Service -Name $ServiceName -Force; $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30)) }
    & sc.exe delete $ServiceName | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail "sc delete failed with $LASTEXITCODE." }
}

# The HTTP MCP runs in a user session and may still hold files in the install folder; stop it first.
Get-Process -Name 'AiDevHelpers.MCP.Http' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($InstallDirectory, [StringComparison]::OrdinalIgnoreCase) } |
    Stop-Process -Force

if ($removeInstall) { Remove-Item -LiteralPath $InstallDirectory -Recurse -Force }
if ($removeData) { Remove-Item -LiteralPath $DataDirectory -Recurse -Force }

Write-Output ('RESULT ' + (@{ ok = $true; service = $ServiceName; removedInstall = $removeInstall; removedData = $removeData } | ConvertTo-Json -Compress))
exit 0
