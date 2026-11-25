# End-to-end self test for Script Helper: builds the three executables, sets up a throwaway
# project and ProgramData root, starts the broker hidden, and runs elev against it.
# Run from an elevated PowerShell 7 shell. It never touches the real Script Helper data,
# because ProgramData is pointed at a temp folder for the duration of the run.

#Requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
$failures = [System.Collections.Generic.List[string]]::new()

function Assert-Step([bool]$ok, [string]$name) {
    if ($ok) { Write-Host "PASS  $name" }
    else { Write-Host "FAIL  $name"; $script:failures.Add($name) }
}

$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'FAIL  not elevated: run this from an elevated PowerShell 7 shell'
    exit 1
}

foreach ($project in 'src/ScriptHelper/broker', 'src/ScriptHelper/client') {
    dotnet build (Join-Path $repo $project) -c Debug --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "build failed: $project" }
}

$brokerBin = Join-Path $repo 'src/ScriptHelper/broker/bin/Debug/net10.0-windows'
$clientBin = Join-Path $repo 'src/ScriptHelper/client/bin/Debug/net10.0-windows'
$elevExe = Join-Path $clientBin 'elev.exe'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class Win32Window {
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
}
'@

$work = Join-Path ([IO.Path]::GetTempPath()) ('sh-e2e-' + [guid]::NewGuid())
$programData = Join-Path $work 'ProgramData'
$dataRoot = Join-Path $programData 'AiDevHelpers/script-helper'
$installDir = Join-Path $dataRoot 'bin'
$project = Join-Path $work 'project'
$broker = $null
$savedProgramData = $env:ProgramData

try {
    New-Item -ItemType Directory -Force -Path $programData, (Join-Path $project 'scripts/debug') | Out-Null
    $env:ProgramData = $programData

    $utf8 = [Text.UTF8Encoding]::new($false)
    [IO.File]::WriteAllText((Join-Path $project 'scripts/debug/hello.ps1'),
        "param([string]`$Port)`nWrite-Output 'hello from elevated'`nexit 7`n", $utf8)
    [IO.File]::WriteAllText((Join-Path $project 'scripts/evil.ps1'), "Write-Output 'evil'`n", $utf8)
    [IO.File]::WriteAllText((Join-Path $project '.elevated.json'),
        '{"scripts":[{"path":"scripts/debug/hello.ps1","args":["-Port","*"]}]}', $utf8)

    # Setup: register, then the two-step approval the way an AI caller does it.
    & $elevExe register $project
    Assert-Step ($LASTEXITCODE -eq 0) 'setup: register the project'

    $prepared = (& $elevExe approve $project 'scripts/debug/hello.ps1' --prepare) -join "`n"
    $m = [regex]::Match($prepared, '--confirm (\S+) (\S+)')
    Assert-Step ($m.Success) 'setup: approval preview prints fingerprint and allowlist hash'

    & $elevExe approve $project 'scripts/debug/hello.ps1' --confirm $m.Groups[1].Value $m.Groups[2].Value
    Assert-Step ($LASTEXITCODE -eq 0) 'setup: approve the exact bytes shown'

    # Install the broker into the admin-only folder the client requires.
    New-Item -ItemType Directory -Force -Path $installDir | Out-Null
    Copy-Item -Path (Join-Path $brokerBin '*') -Destination $installDir -Recurse -Force

    # Start the broker hidden. Already elevated here, so no UAC prompt.
    $broker = Start-Process -FilePath (Join-Path $installDir 'scripthelper-broker.exe') -WindowStyle Hidden -PassThru
    $heartbeat = Join-Path $dataRoot 'broker-heartbeat.json'
    $deadline = (Get-Date).AddSeconds(20)
    while (-not (Test-Path $heartbeat) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
    Assert-Step (Test-Path $heartbeat) 'broker: heartbeat appears after hidden start'

    $broker.Refresh()
    $visible = $broker.MainWindowHandle -ne [IntPtr]::Zero -and [Win32Window]::IsWindowVisible($broker.MainWindowHandle)
    Assert-Step (-not $visible) 'broker: no visible window'

    # elev: an approved script runs elevated and its output and exit code come back.
    $out = & $elevExe run $project 'scripts/debug/hello.ps1' -Port 5000 2>&1
    $code = $LASTEXITCODE
    Assert-Step (($out -join "`n") -match 'hello from elevated') 'elev: approved script output comes back'
    Assert-Step ($code -eq 7) "elev: script exit code passes through (got $code)"

    # elev: a script that is not in the manifest is refused with a reason.
    $out = & $elevExe run $project 'scripts/evil.ps1' 2>&1
    $code = $LASTEXITCODE
    Assert-Step ($code -eq 1 -and (($out -join "`n") -match 'refused')) 'elev: unlisted script is refused with a reason'

    # Stop the broker with the stop file and check that it exits and the stop is audited.
    New-Item -ItemType File -Path (Join-Path $dataRoot 'broker.stop') | Out-Null
    $exited = $broker.WaitForExit(20000)
    Assert-Step $exited 'broker: exits on the stop file'

    $audit = Join-Path $dataRoot 'logs/audit.log'
    $auditText = if (Test-Path $audit) { Get-Content $audit -Raw } else { '' }
    Write-Host "audit log ($audit):`n$auditText"
    Assert-Step ($auditText -match 'broker started' -and $auditText -match 'broker stopped') 'broker: start and stop are audited'

    # After the stop, the heartbeat goes stale (5 s). elev must then report silence without queuing.
    Start-Sleep -Seconds 7
    $out = & $elevExe run $project 'scripts/debug/hello.ps1' -Port 5000 2>&1
    $code = $LASTEXITCODE
    Assert-Step ($code -eq 1 -and (($out -join "`n") -match 'not responding')) 'elev: silent broker is reported, nothing hangs'
}
finally {
    if ($broker -and -not $broker.HasExited) { Stop-Process -Id $broker.Id -Force }
    $env:ProgramData = $savedProgramData
    # Keep the temp data when a check failed, so the failure can be inspected.
    if ($failures.Count -eq 0 -and (Test-Path $work)) { Remove-Item -Recurse -Force $work }
    elseif (Test-Path $work) { Write-Host "kept for inspection: $work" }
}

if ($failures.Count -gt 0) {
    Write-Host "`n$($failures.Count) check(s) failed"
    exit 1
}
Write-Host "`nAll end-to-end checks passed"
exit 0
