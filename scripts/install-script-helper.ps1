#Requires -Version 7
#Requires -RunAsAdministrator
<#
Installs Script Helper on this machine: builds the elev client and the broker, creates the admin-only folders and sets up the installer
script through `elev tools` (which asks you to approve it), stops a running broker, and copies the broker into the admin-only install
folder. Run it from an elevated PowerShell 7 shell. The broker is started later, by the first `elev run`, with one UAC prompt.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$dataRoot = Join-Path ($env:ProgramData ?? 'C:\ProgramData') 'AiDevHelpers\script-helper'
$installDir = Join-Path $dataRoot 'bin'

foreach ($name in 'broker', 'client') {
    $project = (Get-ChildItem (Join-Path $repo "src\ScriptHelper\$name") -Filter *.csproj).FullName
    dotnet build $project -c $Configuration --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "build failed: $project" }
}

$brokerOut = Join-Path $repo "src\ScriptHelper\broker\bin\$Configuration\net10.0-windows"
$elev = Join-Path $repo "src\ScriptHelper\client\bin\$Configuration\net10.0-windows\elev.exe"

# Any setup verb creates the admin-only folders; `tools` also puts the installer script there and asks you to approve it.
& $elev tools
if ($LASTEXITCODE -ne 0) { throw "elev tools did not finish (exit $LASTEXITCODE)" }

# A running broker holds its files: ask it to stop, then wait for it to go.
$stop = Join-Path $dataRoot 'broker.stop'
if (Get-Process -Name 'scripthelper-broker' -ErrorAction SilentlyContinue) {
    New-Item -ItemType File -Force $stop | Out-Null
    $deadline = (Get-Date).AddSeconds(45)
    while ((Get-Process -Name 'scripthelper-broker' -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 1
    }
    if (Get-Process -Name 'scripthelper-broker' -ErrorAction SilentlyContinue) {
        throw 'The broker is still running; close it and run this script again.'
    }
}

New-Item -ItemType Directory -Force $installDir | Out-Null
Copy-Item -Path (Join-Path $brokerOut '*') -Destination $installDir -Recurse -Force
Write-Host "Installed the broker to $installDir"
Write-Host 'Done. The first `elev run` starts the broker with one UAC prompt.'
