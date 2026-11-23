#Requires -Version 7
[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$NoBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent $PSScriptRoot
$Vsix = Join-Path $RepoRoot "src\Installers\Vs2026\Installer\src\AiDevHelpers.Installers.Vs2026.Installer.vsix"
$BuildScript = Join-Path $PSScriptRoot "build-vs-extension-vsix.ps1"
$vswhere = "C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe"

if (-not $NoBuild) {
    & $BuildScript -Configuration $Configuration
}
if (-not (Test-Path -LiteralPath $Vsix)) {
    throw "VSIX package not found at $Vsix. Build it first or omit -NoBuild."
}
if (-not (Test-Path -LiteralPath $vswhere)) {
    throw "vswhere.exe not found at $vswhere."
}

$instances = @(& $vswhere -all -prerelease -format json | ConvertFrom-Json | Where-Object {
    [version]($_.installationVersion -replace '-.*$') -ge [version]"18.0"
})
if ($instances.Count -eq 0) {
    Write-Warning "No Visual Studio 18 instances were found; the VSIX was built but not installed."
    return
}

# Every Visual Studio 18 instance gets the extension, stable and preview alike, all installers running at once.
$running = foreach ($instance in $instances) {
    $installer = Join-Path $instance.installationPath "Common7\IDE\VsixInstaller.exe"
    if (-not (Test-Path -LiteralPath $installer)) {
        Write-Warning "VsixInstaller.exe not found for $($instance.displayName); skipping that instance."
        continue
    }

    Write-Host "Installing the AiDevHelpers IDE Bridge into $($instance.displayName) $($instance.catalog.productDisplayVersion)..." -ForegroundColor Cyan
    Start-Process -FilePath $installer -ArgumentList @("/instanceIds:$($instance.instanceId)", "`"$Vsix`"") -PassThru
}
$running | Wait-Process
