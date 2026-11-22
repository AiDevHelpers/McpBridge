# Updates the aidevtools MCP server while chats are using it: pause, close, update, resume.
#   1. paused:  every server refuses tool calls and tells its chat.
#   2. closing: every server exits, which releases its files.
#   3. update:  runs -UpdateCommand in the repo root (default: rebuild the server).
#   4. running: always written at the end, even when the update fails, so no chat stays paused.
# A chat reconnects to a fresh server by itself (in Claude Code: /mcp reconnect).
param(
    [string]$Message = 'updating aidevtools',
    [string]$UpdateCommand = 'dotnet build src/MCP/Server/src/AiDevHelpers.MCP.csproj -c Release',
    [int]$PauseSeconds = 10,
    [int]$CloseTimeoutSeconds = 60
)

$statePath = Join-Path ($env:ProgramData ?? 'C:\ProgramData') 'AiDevHelpers\control\state.json'

function Set-ControlState([string]$State, [string]$Text) {
    New-Item -ItemType Directory -Force -Path (Split-Path $statePath) | Out-Null
    $json = [ordered]@{ state = $State; message = $Text; updatedUtc = (Get-Date).ToUniversalTime().ToString('o') } | ConvertTo-Json
    $temp = "$statePath.$([guid]::NewGuid().ToString('N')).tmp"
    Set-Content -Path $temp -Value $json -Encoding utf8
    Move-Item -Path $temp -Destination $statePath -Force
    Write-Host "aidevtools control state: $State"
}

$root = Split-Path -Parent $PSScriptRoot
try {
    Set-ControlState 'paused' $Message
    Start-Sleep -Seconds $PauseSeconds

    Set-ControlState 'closing' $Message
    $deadline = (Get-Date).AddSeconds($CloseTimeoutSeconds)
    while ((Get-Process -Name 'AiDevHelpers.MCP' -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 1
    }
    $left = Get-Process -Name 'AiDevHelpers.MCP' -ErrorAction SilentlyContinue
    if ($left) {
        throw "Still running after $CloseTimeoutSeconds seconds: $($left.Id -join ', '). Close those chats or servers and run again."
    }

    Push-Location $root
    try {
        Write-Host "Updating: $UpdateCommand"
        Invoke-Expression $UpdateCommand
        if ($LASTEXITCODE -ne 0) { throw "The update command failed with exit code $LASTEXITCODE." }
    }
    finally {
        Pop-Location
    }
}
finally {
    Set-ControlState 'running' $null
}
