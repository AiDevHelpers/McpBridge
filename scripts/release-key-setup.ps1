<#
.SYNOPSIS
One-time setup of the release signing key (unit 10b).

.DESCRIPTION
Generates an Ed25519 key, stores the private key as the RELEASE_SIGNING_KEY secret of a protected GitHub
environment (you are the required reviewer), prints the raw 32-byte public key to commit, and deletes the
local private key. The private key never goes into the repo.

Requires: gh (logged in with access to the repo) and openssl (Git for Windows or OpenSSL).
Run once. It refuses to overwrite an existing local key.
#>
[CmdletBinding()]
param(
    [string]$Repo = 'AiDevHelpers/McpBridge',
    [string]$Environment = 'release',
    [string]$KeyFolder = (Join-Path $env:USERPROFILE '.aidevhelpers-keys'),
    [switch]$KeepLocalKey
)

$ErrorActionPreference = 'Stop'

function Find-OpenSsl {
    $cmd = Get-Command openssl -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $bundled = Join-Path $env:ProgramFiles 'Git\usr\bin\openssl.exe'
    if (Test-Path $bundled) { return $bundled }
    throw 'openssl not found. Install Git for Windows or OpenSSL.'
}

$openssl = Find-OpenSsl

gh auth status *> $null
if ($LASTEXITCODE -ne 0) { throw 'gh is not logged in. Run: gh auth login' }

New-Item -ItemType Directory -Force -Path $KeyFolder | Out-Null
$pem = Join-Path $KeyFolder 'release-signing.pem'
$pubDer = Join-Path $KeyFolder 'release-signing.pub.der'

if (Test-Path $pem) {
    throw "A key already exists at $pem. Move it aside first, or the existing key is used by the secret."
}

& $openssl genpkey -algorithm ed25519 -out $pem
if ($LASTEXITCODE -ne 0) { throw 'Key generation failed.' }

& $openssl pkey -in $pem -pubout -outform DER -out $pubDer
if ($LASTEXITCODE -ne 0) { throw 'Public key export failed.' }

# An Ed25519 SubjectPublicKeyInfo DER ends with the raw 32-byte key.
$der = [IO.File]::ReadAllBytes($pubDer)
$publicKey = [Convert]::ToBase64String($der[($der.Length - 32)..($der.Length - 1)])
Remove-Item $pubDer
# The private key can be deleted below, and the secret cannot be read back, so keep the public key on disk too.
$pubFile = Join-Path $KeyFolder 'release-signing.pub.txt'
Set-Content -Path $pubFile -Value $publicKey -Encoding ascii

$uid = gh api user --jq .id
gh api -X PUT "repos/$Repo/environments/$Environment" -F "reviewers[][type]=User" -F "reviewers[][id]=$uid" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Could not create environment '$Environment'." }

Get-Content -Raw $pem | gh secret set RELEASE_SIGNING_KEY --env $Environment --repo $Repo
if ($LASTEXITCODE -ne 0) { throw 'Could not store RELEASE_SIGNING_KEY.' }

if (-not $KeepLocalKey) {
    Remove-Item $pem
    Write-Host "Local private key removed. The secret is the only copy."
} else {
    Write-Host "Local private key kept at $pem. Delete it once you are sure the secret is set."
}

Write-Host ''
Write-Host 'Public key (commit this, it is not secret):'
Write-Host $publicKey
Write-Host "Also saved to $pubFile"
