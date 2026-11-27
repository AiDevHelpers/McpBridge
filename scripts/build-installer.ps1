# Builds the package, adds the pinned service scripts to it, and compiles the Inno Setup installer into artifacts/installer.
# Usage: scripts/build-installer.ps1 -Version 0.1.0-dev
param([string]$Version = '0.1.0-dev')

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'build-package.ps1') -Version $Version
if ($LASTEXITCODE -ne 0) { throw 'package build failed' }

# The service scripts travel with the package. Each keeps its own committed pin, so the copies must match the pinned files.
$pkg = Join-Path $repo 'artifacts\package'
foreach ($name in 'service-install.ps1', 'service-install.sha256', 'service-uninstall.ps1', 'service-uninstall.sha256') {
    Copy-Item -LiteralPath (Join-Path $repo "scripts\$name") -Destination $pkg -Force
}

$iscc = 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
if (-not (Test-Path -LiteralPath $iscc)) { throw "Inno Setup not found at $iscc" }
& $iscc "/DAppVersion=$Version" (Join-Path $repo 'installer\AiDevHelpers.iss')
if ($LASTEXITCODE -ne 0) { throw 'installer build failed' }
Write-Output (Join-Path $repo "artifacts\installer\AiDevHelpers-Setup-$Version.exe")
