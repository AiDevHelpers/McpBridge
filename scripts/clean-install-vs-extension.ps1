#Requires -Version 7
[CmdletBinding()]
param(
    [switch]$NoBuild
)

# Clean install of the AiDevHelpers IDE Bridge into every Visual Studio 18 instance:
#   1. refuse if Visual Studio is running (its files are locked),
#   2. remove the extension from each instance,
#   3. check for stale copies left in the per-user extension caches,
#   4. install the VSIX with one VsixInstaller run (one dialog, tick every instance).
# Exp hives (devenv /RootSuffix Exp) are not covered: VsixInstaller reaches them only through a separate run.

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent $PSScriptRoot
$Vsix = Join-Path $RepoRoot "src\Installers\Vs2026\Installer\src\AiDevHelpers.Installers.Vs2026.Installer.vsix"
$BuildScript = Join-Path $PSScriptRoot "build-vs-extension-vsix.ps1"
$ExtensionId = "AiDevHelpers.Installers.Vs2026.Installer.A2CE85F0-DEB4-4524-93AE-7854F1FC31FB"
$VsixDll = "AiDevHelpers.Installers.Vs2026.Installer.dll"
$vswhere = "C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe"
$cacheRoot = Join-Path $env:LOCALAPPDATA "Microsoft\VisualStudio"

# 1. Nothing that locks the install may be running. Checked first, before any build or uninstall.
#    devenv: Visual Studio (any hive) holds the extension files.
#    VSIXInstaller: a leftover installer or uninstall would race this run.
#    AiDevHelpers.MCP: the MCP servers load the bridge output and lock the build.
$locking = @(
    Get-Process devenv, VSIXInstaller, 'AiDevHelpers.MCP' -ErrorAction SilentlyContinue |
        ForEach-Object { "$($_.ProcessName) (PID $($_.Id))" }
)
if ($locking.Count -gt 0) {
    throw "Still running: $($locking -join ', '). Close them, then rerun. Nothing was changed."
}

# Build the VSIX unless told not to. The Community MSBuild is used: the default pick (latest) may lack the VSSDK.
if (-not $NoBuild) {
    $community = "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"
    & $BuildScript -MSBuildPath $community
    if ($LASTEXITCODE -ne 0) { throw "VSIX build failed." }
}
if (-not (Test-Path -LiteralPath $Vsix)) {
    throw "VSIX not found at $Vsix. Build it first or omit -NoBuild."
}

$instances = @(& $vswhere -all -prerelease -format json | ConvertFrom-Json | Where-Object {
    [version]($_.installationVersion -replace '-.*$') -ge [version]"18.0"
})
if ($instances.Count -eq 0) {
    throw "No Visual Studio 18 instances found."
}

# 2. Remove the extension from each instance. An instance without it returns non-zero; that is fine.
foreach ($instance in $instances) {
    $remover = Join-Path $instance.installationPath "Common7\IDE\VsixInstaller.exe"
    if (Test-Path -LiteralPath $remover) {
        Write-Host "Removing from $($instance.displayName) $($instance.installationVersion)..."
        Start-Process -FilePath $remover -ArgumentList @("/quiet", "/instanceIds:$($instance.instanceId)", "/uninstall:$ExtensionId") -Wait | Out-Null
    }
}

# 3. Stale copies: any installed DLL left in the per-user caches after removal.
$stale = @(Get-ChildItem -Path $cacheRoot -Directory -Filter "18.0_*" -ErrorAction SilentlyContinue |
    ForEach-Object { Get-ChildItem -Path (Join-Path $_.FullName "Extensions") -Recurse -Filter $VsixDll -ErrorAction SilentlyContinue } |
    Where-Object { $_.FullName -notmatch '\\Exp\\|Exp\\Extensions' })
if ($stale.Count -gt 0) {
    Write-Warning "Stale copies remain after removal:"
    $stale | ForEach-Object { Write-Warning "  $($_.FullName)" }
    throw "Stale copies still present. Remove them, then rerun. Nothing was installed."
}

# 4. Install: one VsixInstaller run with no /instanceIds, so the dialog offers every instance.
$installer = $instances |
    ForEach-Object { Join-Path $_.installationPath "Common7\IDE\VsixInstaller.exe" } |
    Where-Object { Test-Path -LiteralPath $_ } |
    Select-Object -First 1
if (-not $installer) {
    throw "VsixInstaller.exe was not found in any Visual Studio 18 instance."
}

Write-Host "Installing; tick every Visual Studio instance in the dialog..." -ForegroundColor Cyan
$process = Start-Process -FilePath $installer -ArgumentList @("`"$Vsix`"") -Wait -PassThru
if ($process.ExitCode -ne 0) {
    throw "VsixInstaller exited with code $($process.ExitCode)."
}
Write-Host "Installed. Check the hive timestamps to confirm each instance." -ForegroundColor Green
