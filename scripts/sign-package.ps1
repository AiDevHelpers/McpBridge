<#
.SYNOPSIS
Signs a package's manifest.json with the release private key (unit 10b).

.DESCRIPTION
Writes manifest.json.sig (raw 64-byte Ed25519 signature) next to manifest.json. Uses OpenSSL 3 with -rawin, because Ed25519
signs the message itself and has no pre-hash. The signature is checked for length; the release workflow verifies it again
with the release public key before the zip is published.

.PARAMETER PackageDir  Folder that already contains manifest.json.
.PARAMETER KeyFile     PEM file holding the Ed25519 private key (the release workflow writes it from the secret to a temp file).
#>
param(
    [Parameter(Mandatory)][string]$PackageDir,
    [Parameter(Mandatory)][string]$KeyFile
)

$ErrorActionPreference = 'Stop'

$manifest = Join-Path $PackageDir 'manifest.json'
$sig = Join-Path $PackageDir 'manifest.json.sig'
if (-not (Test-Path $manifest)) { throw "manifest.json not found in $PackageDir" }
if (-not (Test-Path $KeyFile)) { throw "key file not found: $KeyFile" }

$openssl = (Get-Command openssl -ErrorAction SilentlyContinue)?.Source
if (-not $openssl) {
    $bundled = Join-Path $env:ProgramFiles 'Git\usr\bin\openssl.exe'
    if (Test-Path $bundled) { $openssl = $bundled } else { throw 'openssl not found' }
}

& $openssl pkeyutl -sign -rawin -inkey $KeyFile -in $manifest -out $sig
if ($LASTEXITCODE -ne 0) { throw "openssl signing failed (exit $LASTEXITCODE)" }

$length = (Get-Item $sig).Length
if ($length -ne 64) { throw "signature is $length bytes; an Ed25519 signature is 64" }

Write-Host "Signed $manifest"

