# Builds AiDevHelpers.slnx with Visual Studio's MSBuild, as CI does. dotnet build cannot build the VSIX project.
# npm steps are skipped here: the full gate runs them as their own steps.
param([string]$Configuration = 'Release')

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$msbuild = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'Visual Studio MSBuild.exe was not found.' }

$root = Split-Path -Parent $PSScriptRoot
& $msbuild (Join-Path $root 'AiDevHelpers.slnx') /restore /t:Build "/p:Configuration=$Configuration" /p:SkipNpm=true /m /v:minimal
exit $LASTEXITCODE
