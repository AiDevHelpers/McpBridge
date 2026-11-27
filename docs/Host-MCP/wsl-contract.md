# wsl contract

Ported from TokenSaver `TokenSaver.WslControl` (`WslProvider`, `SessionWsl`, `WslDebugConfigService`) and `WslControlTools`.
Differences by design: no `Community.Wsl.Sdk` (distros come from `wsl.exe -l -v`); no session-0 runner (the aidevtools server runs in the user session);
JSON results; `CommandRunner` for processes. Dropped: apt helpers, build helper and `wsl do` (see the follow-up list in the plan).

## Tool
`wsl(action, distro?, command?, timeoutSeconds?, runtime?, programPath?, workspaceRoot?, write?)`. `action` is trimmed and case-insensitive.
Actions: `status`, `health`, `run`, `debug_setup`. Unknown action: `ok=false`, error lists the valid actions.

## Distro names
- Validated: `^[A-Za-z0-9][A-Za-z0-9._-]*$` (no leading dash, so `--import` cannot be read as an option). Else error "Invalid distro name ...".
- Omitted: the default distro (the one `wsl -l -v` marks with `*`). None registered: error "No WSL distribution is registered. Install one, e.g. `wsl --install -d Ubuntu`."

## Listing (`wsl.exe -l -v`)
- Output is UTF-16 LE text, may start with a BOM, has a header row, then rows `[*] NAME STATE VERSION`. The parser strips NUL characters and BOM, skips the header, and accepts rows matching `^\s*(\*)?\s*(\S+)\s+(\S+)\s+(\d+)\s*$`.
- Run with environment `WSL_UTF8=1` where supported; the parser handles both UTF-16 and UTF-8 text.
- Timeout 20 s. Launch failure or non-zero exit: `ok=false` with the stderr text.
- `wsl.exe` not present: `available=false`, not an exception.

## Status (`status`)
Result: `{ok, available, distroCount, distros: [{name, isDefault, state, version}], defaultDistro}`. `state` is `Running` or `Stopped` as printed.
Not kept from TokenSaver (not in `wsl -l -v`): base path and default uid.

## Health (`health`, optional `distro`)
One bash probe, run through `/bin/bash -c`, printing KEY=VALUE lines: `KERNEL` (`uname -sr`), `USER` (`id -un`), `OS` (`PRETTY_NAME` from /etc/os-release), `APT` (1 if apt-get exists), `SUDO` (1 if `sudo -n true` succeeds).
Timeout 120 s (a stopped distro can take a while to start).
Healthy = `KERNEL` non-empty. Result: `{ok, distro, healthy, os, kernel, user, hasApt, hasSudo, detail}`. A failed probe: `healthy=false` with `detail` = stderr or exit code.

## Run (`run`)
- `command` required, not empty. Runs `/bin/bash -c <command>` in the distro (`wsl.exe -d <distro> -- /bin/bash -c <command>`, arguments as a list). Pipes, redirection and env work.
- `timeoutSeconds` 1-600, default 120. Out of range: error "timeoutSeconds must be between 1 and 600.".
- Output decoded as UTF-8 (inside the distro). Trailing CR/LF trimmed. Each stream capped at 20,000 characters with a `capped` flag.
- Result: `{ok, distro, exitCode, stdout, stderr, timedOut, capped, executionError}`. On timeout: `timedOut=true`, `exitCode=-1`. A launch error: `executionError` set, `exitCode=-1`.
- Tool description states that this runs arbitrary commands in the distro.

## Debug setup (`debug_setup`)
- `runtime`: `dotnet`, `python` or `node` (else error). `programPath` required, as seen inside WSL.
- Backend probe through `run` with 30 s timeout:
  - dotnet: `test -x "$HOME/.vsdbg/vsdbg"` -> ready. Install hint: the vsdbg install script into `~/.vsdbg`.
  - python: `python3 -c 'import debugpy'` -> ready. Hint: `python3 -m pip install --user debugpy`.
  - node: `command -v node` -> ready. Hint: install Node.js.
- Launch configuration (VS Code JSON):
  - dotnet: `{name:"WSL: .NET Launch", type:"coreclr", request:"launch", program, cwd:"${workspaceFolder}", stopAtEntry:false, console:"internalConsole", pipeTransport:{pipeCwd:"${workspaceFolder}", pipeProgram:"wsl.exe", pipeArgs:["-d", distro, "--"], debuggerPath:"~/.vsdbg/vsdbg"}}`.
  - python: `{name:"WSL: Python Launch", type:"debugpy", request:"launch", program, console:"integratedTerminal", python:"wsl.exe", pythonArgs:["-d", distro, "--", "python3"]}`.
  - node: `{name:"WSL: Node Launch", type:"node", request:"launch", program, cwd:"${workspaceFolder}", useWSL:true}`.
- `write=true` with `workspaceRoot`: writes `<workspaceRoot>\.vscode\launch.json` containing `{version:"0.2.0", configurations:[config]}`, only when the file does not exist
  (created with `FileMode.CreateNew`, so no overwrite and no race). Refused when `.vscode` is a link or junction. Refused when `workspaceRoot` is not an existing folder.
  An existing file is never changed; the result returns the fragment to merge by hand.
- Result: `{ok, distro, runtime, backendReady, backendDetail, installHint, launchConfigName, launchConfig, configWritten}`.
- `programPath` is only ever serialized into JSON, never put on a command line.

## Errors
Refusals and failures are `{ok:false, error}`. Missing `wsl.exe` is `available=false` in `status`, and an error in the other actions.

## Not ported
- `wsl do` (natural-language planning, needs a generate call), apt install/update/upgrade (15 min), build (10 min, `wslpath -a`).
- Session-0 execution (`ConsoleUserProcessRunner`).
