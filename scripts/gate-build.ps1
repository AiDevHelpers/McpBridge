# Builds AiDevHelpers.slnx with Visual Studio's MSBuild, as CI does. dotnet build cannot build the VSIX project.
# npm steps are skipped here: the full gate runs them as their own steps.
# The running AiDevHelpers.MCP servers from this repo lock their output files, so they are stopped by pid first
# (only servers whose executable is under this repo). The aidevtools connection must then be reconnected with /mcp.
param([string]$Configuration = 'Release')

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$msbuild = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'Visual Studio MSBuild.exe was not found.' }

$root = Split-Path -Parent $PSScriptRoot

$stopped = @()
foreach ($server in Get-Process -Name 'AiDevHelpers.MCP' -ErrorAction SilentlyContinue) {
    $path = $server.Path
    if ($path -and $path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
        Stop-Process -Id $server.Id -Force
        $stopped += $server.Id
    }
}
if ($stopped.Count -gt 0) { Write-Host "Stopped repo MCP servers by pid: $($stopped -join ', ')" }

& $msbuild (Join-Path $root 'AiDevHelpers.slnx') /restore /t:Build "/p:Configuration=$Configuration" /p:SkipNpm=true /m /v:minimal
$code = $LASTEXITCODE
if ($stopped.Count -gt 0) { Write-Host 'Reconnect the aidevtools MCP server with /mcp.' }
exit $code
