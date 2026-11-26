#Requires -Version 7
# Builds, checks and installs the AiDevHelpers VS Code extension (the IDE bridge) from this repo.
# Steps: npm ci, build, lint, test, package, then `code --install-extension --force`. Stops at the first failing step.
# VS Code can stay open; reload its window (or restart it) afterwards.
[CmdletBinding()]
param(
    [switch]$SkipTests,
    [switch]$NoPackageInstall
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent $PSScriptRoot
$Core = Join-Path $RepoRoot "src\Installers\VsCode\Core\AiDevTools.VsCode.Core"
$Extension = Join-Path $RepoRoot "src\Installers\VsCode\Installer\AiDevTools.VsCode"
$Vsix = Join-Path $Extension "aidevhelpers-vscode.vsix"

$code = Get-Command code -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $code) {
    throw "The VS Code command line ('code') is not on PATH. Install VS Code or add its bin folder to PATH."
}
if (-not (Get-Command npm -CommandType Application -ErrorAction SilentlyContinue)) {
    throw "npm is not on PATH. Install Node.js first."
}

Push-Location $Extension
try {
    # The extension's build compiles the Core library, so Core needs its own packages first (same as the full gate).
    Write-Host "==> npm ci (Core)"
    & npm --prefix $Core ci
    if ($LASTEXITCODE -ne 0) { throw "npm ci for Core failed with exit code $LASTEXITCODE." }

    foreach ($step in @(
            @{ Name = "npm ci"; Args = @("ci") },
            @{ Name = "build"; Args = @("run", "build") },
            @{ Name = "lint"; Args = @("run", "lint") })) {
        Write-Host "==> npm $($step.Name)"
        & npm @($step.Args)
        if ($LASTEXITCODE -ne 0) { throw "npm $($step.Name) failed with exit code $LASTEXITCODE." }
    }

    if ($SkipTests) {
        Write-Warning "Skipping npm test (-SkipTests)."
    }
    else {
        Write-Host "==> npm test"
        & npm test
        if ($LASTEXITCODE -ne 0) { throw "npm test failed with exit code $LASTEXITCODE." }
    }

    Write-Host "==> npm run package"
    & npm run package
    if ($LASTEXITCODE -ne 0) { throw "npm run package failed with exit code $LASTEXITCODE." }
}
finally {
    Pop-Location
}

if (-not (Test-Path -LiteralPath $Vsix)) {
    throw "Package not found at $Vsix after packaging."
}

if ($NoPackageInstall) {
    Write-Host "Built $Vsix. Not installed (-NoPackageInstall)."
    return
}

Write-Host "==> code --install-extension $Vsix --force"
& $code.Source --install-extension $Vsix --force
if ($LASTEXITCODE -ne 0) { throw "code --install-extension failed with exit code $LASTEXITCODE." }

Write-Host "Installed the AiDevHelpers IDE Bridge into VS Code. Reload the VS Code window to activate it."
