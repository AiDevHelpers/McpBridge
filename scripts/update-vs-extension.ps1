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

# One VsixInstaller run with no /instanceIds: its dialog lists every compatible Visual Studio product with a checkbox
# each, so all VS 18 instances (stable and preview) are offered together. Separate runs per instance hand off to the
# first one and list a single product.
$installer = $instances |
    ForEach-Object { Join-Path $_.installationPath "Common7\IDE\VsixInstaller.exe" } |
    Where-Object { Test-Path -LiteralPath $_ } |
    Select-Object -First 1
if (-not $installer) {
    throw "VsixInstaller.exe was not found in any Visual Studio 18 instance."
}

# The installer refuses a VSIX whose version is already installed (exit 1001), so remove the old copy from each instance
# first. An instance without the extension returns a non-zero code here, which is fine.
$extensionId = "AiDevHelpers.Installers.Vs2026.Installer.A2CE85F0-DEB4-4524-93AE-7854F1FC31FB"
foreach ($instance in $instances) {
    $remover = Join-Path $instance.installationPath "Common7\IDE\VsixInstaller.exe"
    if (Test-Path -LiteralPath $remover) {
        Start-Process -FilePath $remover -ArgumentList @("/quiet", "/instanceIds:$($instance.instanceId)", "/uninstall:$extensionId") -Wait | Out-Null
    }
}

Write-Host "Installing the AiDevHelpers IDE Bridge; tick every Visual Studio instance in the dialog..." -ForegroundColor Cyan
$process = Start-Process -FilePath $installer -ArgumentList @("`"$Vsix`"") -Wait -PassThru
if ($process.ExitCode -ne 0) {
    throw "VsixInstaller exited with code $($process.ExitCode)."
}
