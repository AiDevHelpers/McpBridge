# Installs the AiDevHelpers Windows service (LocalSystem) from a folder with the published Host, MCP.Http and Tray files.
# It runs elevated, so it refuses to run unless its hash matches the pin committed in scripts/service-install.sha256.
# A changed script shows up as a pin change in git. To accept a reviewed change, run it once with -WritePin and commit the pin.
# -DryRun prints what it would do (lines that start with "PLAN:") and changes nothing.
param(
    [Parameter(Mandatory)][string]$SourceDirectory,
    [string]$InstallDirectory = (Join-Path $env:ProgramFiles 'AiDevHelpers'),
    [string]$ServiceName = 'AiDevHelpers',
    [switch]$Reinstall,
    [switch]$NoStart,
    [switch]$DryRun,
    [switch]$WritePin
)

$ErrorActionPreference = 'Stop'

$self = Join-Path $PSScriptRoot 'service-install.ps1'
$pinFile = Join-Path $PSScriptRoot 'service-install.sha256'
$actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $self).Hash
if ($WritePin) { Set-Content -LiteralPath $pinFile -Value $actual -Encoding ascii; Write-Output "pinned $actual"; exit 0 }
if (-not (Test-Path -LiteralPath $pinFile)) { Write-Error 'No pin file. Run with -WritePin after review.'; exit 1 }
$pinned = (Get-Content -LiteralPath $pinFile -Raw).Trim()
if ($pinned -ne $actual) { Write-Error "service-install.ps1 hash $actual does not match the pin $pinned. Review the change, then run with -WritePin."; exit 1 }

function Fail([string]$message) { Write-Error $message; exit 1 }

# The installed files run as SYSTEM, so the folder must not belong to an ordinary user who could swap them.
function Test-TrustedOwner([string]$path) {
    $owner = (Get-Acl -LiteralPath $path).Owner
    return @('BUILTIN\Administrators', 'NT AUTHORITY\SYSTEM', 'NT SERVICE\TrustedInstaller') -contains $owner
}

$hostExe = 'AiDevHelpers.Host.exe'
$httpExe = 'AiDevHelpers.MCP.Http.exe'
foreach ($required in @($hostExe, $httpExe)) {
    if (-not (Test-Path -LiteralPath (Join-Path $SourceDirectory $required))) { Fail "$required is missing from $SourceDirectory." }
}

if (Test-Path -LiteralPath $InstallDirectory) {
    if (-not (Test-TrustedOwner $InstallDirectory)) {
        Fail "$InstallDirectory is owned by $((Get-Acl -LiteralPath $InstallDirectory).Owner), not by Administrators or SYSTEM. Remove it or pick another folder."
    }
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing -and -not $Reinstall) { Fail "Service $ServiceName is already installed. Use -Reinstall to replace it." }

$exe = Join-Path $InstallDirectory $hostExe
$recovery = 'restart/5000/restart/5000/restart/30000'

if ($DryRun) {
    if ($existing) {
        Write-Output "PLAN: stop service $ServiceName"
        Write-Output "PLAN: delete service $ServiceName"
    }
    Write-Output "PLAN: set install directory access: SYSTEM and Administrators full control, Users read: $InstallDirectory"
    Write-Output "PLAN: copy $SourceDirectory -> $InstallDirectory"
    Write-Output "PLAN: write install-manifest.json (SHA-256 of every installed file)"
    Write-Output "PLAN: create service $ServiceName binPath=`"$exe`" start=auto obj=LocalSystem (account LocalSystem)"
    Write-Output "PLAN: failure actions reset=86400 actions=$recovery, restart on non-crash failures"
    if (-not $NoStart) { Write-Output "PLAN: start service $ServiceName" }
    exit 0
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Fail 'This script needs administrator rights.'
}

if ($existing) {
    if ($existing.Status -ne 'Stopped') { Stop-Service -Name $ServiceName -Force; $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30)) }
    & sc.exe delete $ServiceName | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail "sc delete failed with $LASTEXITCODE." }
}

# Create the folder with its access set before any file goes in, so nothing is ever writable by a user.
New-Item -ItemType Directory -Force -Path $InstallDirectory | Out-Null
$acl = New-Object System.Security.AccessControl.DirectorySecurity
$acl.SetAccessRuleProtection($true, $false)
$inherit = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
$none = [System.Security.AccessControl.PropagationFlags]::None
$allow = [System.Security.AccessControl.AccessControlType]::Allow
foreach ($rule in @(
        @('NT AUTHORITY\SYSTEM', 'FullControl'),
        @('BUILTIN\Administrators', 'FullControl'),
        @('BUILTIN\Users', 'ReadAndExecute'))) {
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($rule[0], $rule[1], $inherit, $none, $allow)))
}
Set-Acl -LiteralPath $InstallDirectory -AclObject $acl

Copy-Item -Path (Join-Path $SourceDirectory '*') -Destination $InstallDirectory -Recurse -Force

$manifest = Get-ChildItem -LiteralPath $InstallDirectory -Recurse -File |
    Where-Object { $_.Name -ne 'install-manifest.json' } |
    ForEach-Object { [ordered]@{ path = $_.FullName.Substring($InstallDirectory.TrimEnd('\').Length + 1); sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash } }
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $InstallDirectory 'install-manifest.json') -Encoding utf8

& sc.exe create $ServiceName binPath= "`"$exe`"" start= auto obj= LocalSystem DisplayName= 'AiDevHelpers' | Out-Null
if ($LASTEXITCODE -ne 0) { Fail "sc create failed with $LASTEXITCODE." }
& sc.exe description $ServiceName 'Runs the AiDevHelpers MCP server in the logged-on user session.' | Out-Null
& sc.exe failure $ServiceName reset= 86400 actions= $recovery | Out-Null
if ($LASTEXITCODE -ne 0) { Fail "sc failure failed with $LASTEXITCODE." }
& sc.exe failureflag $ServiceName 1 | Out-Null

if (-not $NoStart) {
    Start-Service -Name $ServiceName
}

Write-Output ('RESULT ' + (@{ ok = $true; service = $ServiceName; installDirectory = $InstallDirectory; started = (-not $NoStart.IsPresent) } | ConvertTo-Json -Compress))
exit 0
