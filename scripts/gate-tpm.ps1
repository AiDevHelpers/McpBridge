# Runs the Category=Tpm tests elevated. They need administrator rights and a TPM, so the other test steps exclude them.
# Run after gate-build.ps1: it reuses the Release build. Asks for UAC when this shell is not elevated.
param([string]$Configuration = 'Release', [switch]$WritePin)


# The script runs with administrator rights, so it refuses to run unless its hash matches the pin committed in scripts/gate-tpm.sha256.
# A changed script shows up as a pin change in git. To accept a reviewed change, run this script once with -WritePin and commit the pin.
$self = Join-Path $PSScriptRoot 'gate-tpm.ps1'
$pinFile = Join-Path $PSScriptRoot 'gate-tpm.sha256'
$actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $self).Hash
if ($WritePin) { Set-Content -LiteralPath $pinFile -Value $actual -Encoding ascii; Write-Output "pinned $actual"; exit 0 }
if (-not (Test-Path -LiteralPath $pinFile)) { Write-Error 'No pin file. Run with -WritePin after review.'; exit 1 }
$pinned = (Get-Content -LiteralPath $pinFile -Raw).Trim()
if ($pinned -ne $actual) { Write-Error "gate-tpm.ps1 hash $actual does not match the pin $pinned. Review the change, then run with -WritePin."; exit 1 }

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\ScriptHelper\tests\AiDevHelpers.ScriptHelper.Core.Tests.csproj'
# .runsettings excludes Category=Tpm and a command-line filter is combined with it, so clear the settings file here.
$testArgs = @('test', $project, '-c', $Configuration, '--no-build', '--filter', 'Category=Tpm', '-p:RunSettingsFilePath=')

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$elevated = ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($elevated) {
    & dotnet @testArgs
    exit $LASTEXITCODE
}

# An elevated process cannot write to this console, so it logs to a file and reports its exit code.
$log = Join-Path ([IO.Path]::GetTempPath()) "gate-tpm-$PID.log"
$quoted = ($testArgs | ForEach-Object { "'$_'" }) -join ','
$command = "& dotnet @($quoted) *> '$log'; exit `$LASTEXITCODE"
$process = Start-Process pwsh -Verb RunAs -Wait -PassThru -ArgumentList '-NoProfile', '-Command', $command
if (Test-Path $log) { Get-Content $log; Remove-Item $log }
exit $process.ExitCode
