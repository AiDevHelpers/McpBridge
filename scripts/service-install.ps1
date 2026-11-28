# Installs the AiDevHelpers Host service (LocalSystem) and its files under %ProgramFiles%\AiDevHelpers (unit 3 of docs/plans/2026-11-26-tray-service.md).
# Runs elevated, so it checks its own SHA-256 against scripts/service-install.sha256 before it does anything. A changed script
# refuses until a reviewed change is pinned with -WritePin. Pass -SourceFolder with the published Host and MCP.Http files.
param(
    [string]$SourceFolder,
    [switch]$WritePin
)

$self = Join-Path $PSScriptRoot 'service-install.ps1'
$pinFile = Join-Path $PSScriptRoot 'service-install.sha256'
$actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $self).Hash
if ($WritePin) { Set-Content -LiteralPath $pinFile -Value $actual -Encoding ascii; Write-Output "pinned $actual"; exit 0 }
if (-not (Test-Path -LiteralPath $pinFile)) { Write-Error 'No pin file. Review the script, then run it with -WritePin.'; exit 4 }
$pinned = (Get-Content -LiteralPath $pinFile -Raw).Trim()
if ($pinned -ne $actual) { Write-Error "service-install.ps1 hash $actual does not match the pin $pinned. Review the change, then run with -WritePin."; exit 4 }

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error 'refused: installing the service needs an elevated shell.'
    exit 1
}

if (-not $SourceFolder -or -not (Test-Path -LiteralPath (Join-Path $SourceFolder 'AiDevHelpers.Host.exe'))) {
    Write-Error 'refused: -SourceFolder must hold AiDevHelpers.Host.exe and AiDevHelpers.MCP.Http.exe.'
    exit 2
}

$serviceName = 'AiDevHelpers'
if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    Write-Error "refused: the service $serviceName already exists. Uninstall it first."
    exit 3
}

$target = Join-Path $env:ProgramFiles 'AiDevHelpers'
if (Test-Path -LiteralPath $target) {
    # The folder must be admin-owned: a user-owned folder could be replaced after install.
    $owner = (Get-Acl -LiteralPath $target).Owner
    if ($owner -notmatch 'Administrators|SYSTEM|TrustedInstaller') {
        Write-Error "refused: $target exists and is owned by $owner, not an administrator."
        exit 3
    }
}
else {
    New-Item -ItemType Directory -Path $target | Out-Null
}

# Only Administrators and SYSTEM may write; users may read and run.
$acl = Get-Acl -LiteralPath $target
$acl.SetAccessRuleProtection($true, $false)
foreach ($sid in @('S-1-5-32-544', 'S-1-5-18')) {
    $account = (New-Object System.Security.Principal.SecurityIdentifier $sid)
    $acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new($account, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
}
$acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new((New-Object System.Security.Principal.SecurityIdentifier 'S-1-5-32-545'), 'ReadAndExecute', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
Set-Acl -LiteralPath $target -AclObject $acl -ErrorAction Stop

# A tray or MCP.Http left running from an earlier install holds its files open. Stop them before copying over them.
Get-Process -Name 'AiDevHelpers.Tray', 'AiDevHelpers.MCP.Http' -ErrorAction SilentlyContinue | Stop-Process -Force

Copy-Item -Path (Join-Path $SourceFolder '*') -Destination $target -Recurse -Force -ErrorAction Stop
$hostExe = Join-Path $target 'AiDevHelpers.Host.exe'

& sc.exe create $serviceName binPath= "`"$hostExe`"" start= auto obj= LocalSystem DisplayName= 'AiDevHelpers' | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Error "sc.exe create failed with $LASTEXITCODE."; exit 5 }

# Restart on failure: after 5, 10 and 30 seconds; the failure count resets after a day.
& sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
& sc.exe start $serviceName | Out-Null
Write-Output "installed $serviceName at $target"
exit 0
