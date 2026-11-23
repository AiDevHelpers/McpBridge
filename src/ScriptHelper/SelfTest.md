# ScriptHelper self test

Tool: `RunElevatedScript`. The foreground-terminal steps use `TerminalSession` (see the TerminalControl self-test).

## Full self test

Run with the operator present: the elevated terminal raises a UAC prompt that only the operator can accept. Close every session you open.

Window check: `src/ScriptHelper/tests/e2e/Test-ForegroundTerminal.ps1`. Run `-Mode Snapshot` before each open and `-Mode Check -Kind Standard|Elevated` after it, with a scratch `-StateFile`.

- Foreground terminal, not elevated: Snapshot, then `TerminalSession` (action = open, launchMode = InteractiveUser, elevated = false), then Check with `-Kind Standard`. PASS when the check prints PASS for a new window that is visible, has a size, is on the user's desktop session and is not elevated.
- Foreground terminal, elevated: Snapshot, then `TerminalSession` (action = open, launchMode = InteractiveUser, elevated = true), the operator accepts UAC, then Check with `-Kind Elevated`. PASS when the check prints PASS for a new window that is visible, has a size, is on the user's desktop session and is elevated. NOT RUN when the operator does not accept UAC.
- `TerminalSession` (action = close, sessionId) for each opened session: PASS when its window is gone.
- `RunElevatedScript` (projectRoot = a folder without `.elevated.json`, script = `x.ps1`): PASS when the reply is a refusal or error text and no script ran.
- `RunElevatedScript` (projectRoot = empty): PASS when the reply says projectRoot and script are needed.
- End-to-end broker test: `pwsh src/ScriptHelper/tests/e2e/Run-EndToEnd.ps1` from an elevated shell. PASS when it ends with "All end-to-end checks passed".
- Unit tests: `dotnet test src/ScriptHelper/tests/AiDevHelpers.ScriptHelper.Core.Tests.csproj`. PASS when every test passes (Tpm tests excluded unless elevated).

Record each as PASS, FAIL or NOT RUN with the exact arguments and reply.
