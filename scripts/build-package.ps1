# Publishes the three tray-side programs (the service Host, the HTTP MCP, the tray) into artifacts/package for the installer.
# Usage: scripts/build-package.ps1 -Version 0.1.0-dev
param([string]$Version = '0.1.0-dev')

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$out = Join-Path $repo 'artifacts\package'
if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force }
New-Item -ItemType Directory -Path $out | Out-Null

$projects = @(
    'src\Tray\Host\src\AiDevHelpers.Host.csproj',
    'src\Tray\MCP.Http\src\AiDevHelpers.MCP.Http.csproj',
    'src\Tray\Tray\src\AiDevHelpers.Tray.csproj'
)
foreach ($p in $projects) {
    dotnet publish (Join-Path $repo $p) -c Release -o $out
    if ($LASTEXITCODE -ne 0) { throw "publish failed: $p" }
}

Set-Content -LiteralPath (Join-Path $out 'version.txt') -Value $Version -Encoding ascii
foreach ($exe in 'AiDevHelpers.Host.exe', 'AiDevHelpers.MCP.Http.exe', 'AiDevHelpers.Tray.exe') {
    if (-not (Test-Path -LiteralPath (Join-Path $out $exe))) { throw "missing $exe in the package" }
}
Write-Output "package $Version in $out"
