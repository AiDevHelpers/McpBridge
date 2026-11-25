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

    # Pick the newest VS 18 install that has the VSSDK targets (the extension development workload); the latest one may lack it.
    $installations = @(& $vswhere -all -prerelease -products '*' -version '[18.0,19.0)' -property installationPath)
    if ($LASTEXITCODE -ne 0 -or $installations.Count -eq 0) {
        throw "No Visual Studio 18 installation was found. Pass -MSBuildPath explicitly."
    }

    $installation = $installations | Where-Object {
        Test-Path -Path (Join-Path $_ 'MSBuild\Microsoft\VisualStudio\v*\VSSDK\Microsoft.VsSDK.targets')
    } | Select-Object -Last 1
    if (-not $installation) {
        throw "No Visual Studio 18 installation has the extension development workload (Microsoft.VsSDK.targets). Install it or pass -MSBuildPath."
    }

    $MSBuildPath = Join-Path $installation "MSBuild\Current\Bin\MSBuild.exe"
}

if (-not (Test-Path -LiteralPath $MSBuildPath)) {
    throw "MSBuild.exe not found at $MSBuildPath."
}

# The VSSDK targets sit in the Visual Studio install that owns this MSBuild, under a version folder (v17.0 on
# VS 2022, v18.0 on VS 18). The project's default path names one edition, so point the build at the one in use.
$vsRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $MSBuildPath)))
$vsSdkTargets = Get-ChildItem -Path (Join-Path $vsRoot 'MSBuild\Microsoft\VisualStudio\v*\VSSDK\Microsoft.VsSDK.targets') -ErrorAction SilentlyContinue |
    Select-Object -First 1
if (-not $vsSdkTargets) {
    throw "Microsoft.VsSDK.targets was not found under $vsRoot. Install the Visual Studio extension development workload."
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
    & $MSBuildPath $Project "/t:Build;CreateVsixContainer" /restore "/p:Configuration=$Configuration" "/p:VsSdkTargetsPath=$($vsSdkTargets.FullName)" /verbosity:minimal
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
