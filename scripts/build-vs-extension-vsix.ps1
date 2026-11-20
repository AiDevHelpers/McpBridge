#Requires -Version 7
[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$MSBuildPath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $RepoRoot "src\Installers\Vs2026\Installer\src\AiDevHelpers.Installers.Vs2026.Installer.csproj"
$Vsix = Join-Path $RepoRoot "src\Installers\Vs2026\Installer\src\AiDevHelpers.Installers.Vs2026.Installer.vsix"

if ([string]::IsNullOrWhiteSpace($MSBuildPath)) {
    $vswhere = "C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path -LiteralPath $vswhere)) {
        throw "vswhere.exe not found at $vswhere. Pass -MSBuildPath explicitly."
    }

    $installation = & $vswhere -latest -prerelease -products '*' -version '[18.0,19.0)' -property installationPath
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($installation)) {
        throw "No Visual Studio 18 installation was found. Pass -MSBuildPath explicitly."
    }

    $MSBuildPath = Join-Path $installation "MSBuild\Current\Bin\MSBuild.exe"
}

if (-not (Test-Path -LiteralPath $MSBuildPath)) {
    throw "MSBuild.exe not found at $MSBuildPath."
}

# The VSSDK does not replace $version$, and CreateVsixContainer regenerates the manifest from source, so the
# version is written into the source manifest for this build only and restored afterwards. The version is the
# git commit count, the height Nerdbank.GitVersioning would give (0.1.<height>).
$sourceManifest = Join-Path (Split-Path -Parent $Project) "source.extension.vsixmanifest"
$height = (& git -C $RepoRoot rev-list --count HEAD).Trim()
$version = "0.1.$height"
$original = [IO.File]::ReadAllText($sourceManifest)
$patched = [regex]::Replace($original, '(<Identity [^>]*?Version=")[^"]*(")', "`${1}$version`${2}", 1)
Write-Host "VSIX version $version" -ForegroundColor Cyan
try {
    [IO.File]::WriteAllText($sourceManifest, $patched)
    & $MSBuildPath $Project "/t:Build;CreateVsixContainer" /restore "/p:Configuration=$Configuration" /verbosity:minimal
    if ($LASTEXITCODE -ne 0) {
        throw "MSBuild failed for $Project with exit code $LASTEXITCODE."
    }
}
finally {
    [IO.File]::WriteAllText($sourceManifest, $original)
}
if (-not (Test-Path -LiteralPath $Vsix)) {
    throw "VSIX package was not created at $Vsix."
}

Write-Host "Built $Vsix" -ForegroundColor Green
