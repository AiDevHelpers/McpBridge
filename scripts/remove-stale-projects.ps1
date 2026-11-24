#Requires -Version 7
<#
Removes Script Helper registry entries, approvals and user allows for the project roots named on the command line, but only when that folder
no longer exists. Run elevated (through `elev run`). Anything else in the registry is left alone.
#>
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Roots)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $Roots) { throw 'Name at least one project root.' }

$data = Join-Path ($env:ProgramData ?? 'C:\ProgramData') 'AiDevHelpers\script-helper'
$registry = Join-Path $data 'projects.json'
$doc = Get-Content -Raw -LiteralPath $registry | ConvertFrom-Json

$kept = @()
foreach ($project in $doc.Projects) {
    $named = $Roots | Where-Object { $_.TrimEnd('\') -ieq $project.Root.TrimEnd('\') }
    if (-not $named -or (Test-Path -LiteralPath $project.Root)) {
        $kept += $project
        continue
    }

    if ($project.Id -notmatch '^[A-Za-z0-9_-]+$') { throw "Unexpected project id '$($project.Id)'." }
    foreach ($path in @(
            (Join-Path $data "approvals\$($project.Id).json"),
            (Join-Path $data "allowall\$($project.Id).json"),
            (Join-Path $data "userallow\$($project.Id).json"),
            (Join-Path $data "versions\$($project.Id).ver"),
            (Join-Path $data "versions\$($project.Id)-ua.ver"))) {
        Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    }

    Write-Output "removed $($project.Root) ($($project.Id))"
}

$temp = Join-Path $data ([IO.Path]::GetRandomFileName())
@{ Projects = $kept } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $temp -Encoding utf8
Move-Item -LiteralPath $temp -Destination $registry -Force
Write-Output "registry now lists $($kept.Count) project(s)"
