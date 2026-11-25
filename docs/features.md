# Features

What McpBridge does, by area. Tool names, arguments and limits are in [ide-bridge.md](ide-bridge.md).

## IDE control (Visual Studio and VS Code)

- One set of `ide_*` tools works in both IDEs; the reply's `ide` field says which handled it.
- Routing to the right IDE window by solution, `ide` or `instancePid`, with a stored `preferredIde` to break ties.
- List, launch, save-and-kill and reset IDE instances; health checks and a watchdog on each bridge.
- Launch is elevated only for solutions that need administrator rights.
- Build, clean, publish, tests, build diagnostics, format and code cleanup.
- Debugging: start and stop, breakpoints, stepping, locals, evaluate, watches.
- Navigation: open documents, go to definition, find references.
- Output panes and named output channels; bookmarks (Visual Studio).

## Terminal sessions

- `terminal_session`: visible Windows Terminal tabs or headless service-account sessions, with elevated sessions. See [terminal-session-contract.md](terminal-session-contract.md).
- `script_run`: runs `.ps1`, `.cmd`, `.bat`, `.sh`, `.py` and `.js` with the matching interpreter, with timeouts, no-output stops and repeat-failure advice.
- `tool_install`: two-step winget install of missing interpreters, with user approval.

## Elevated scripts

- Script Helper broker (`elev`): runs scripts from a repo's approved `.elevated.json` allowlist, with fingerprint and approval checks, through `run_elevated_script`.
- Repo setup scan for projects and scripts that need administrator rights.

## Source control and CI

- `git_status` and `git_diff` run at once; every write goes through `git_plan` then `git_confirm` with a single-use token.
- Repo rules block commits and pushes on protected branches and bad branch names.
- `pr_checks` and `ci_failure` read pull request checks and failed CI logs through `gh`.

## Build, test and quality

- Gate: `gate_plan` and `gate_run` run the repo's full gate.
- Tests: `test_affected`, `test_watch`, `test_failures`, `test_coverage`, `live_test_start`, and `test_hosts_list`/`test_hosts_stop` for stray test hosts.
- Lint: `lint_check`, `string_scan`, `resx_check`.
- Packages: `package_check`, `package_restore`, `package_outdated`.
- Errors: `error_ingest`, `error_list`, `error_snapshot`, `error_changes` with suppression.
- Diagnostics: `dotnet_diag` wraps dotnet-dump, stack, counters, gcdump, trace and symbol.
- Build environment: `build_env` lists Visual Studio installs with MSBuild and VSSDK targets.

## Configuration and extensions

- `config_resolve` and `config_read`: real paths for launch, app and solution settings, with secrets redacted.
- Extensions: list, describe and add VS Code and Visual Studio extensions (adds are two-step); workspace setup for C# Dev Kit.
- Layered settings: global, repo `.aidevtools/settings.json`, per-user `user.json`.

## Server operations

- `server_control_set` and `server_control_status` pause, close and resume every server for an update.
- `self_test_list` and `self_test_read` expose each helper's self-test. See [self_test.md](self_test.md).

## Packaging and CI

- Bridge, MCP.Core and the packable helpers publish to GitHub Packages on a `v*` tag.
- Pull requests to `main` and `dev` run `build-and-test` and `vscode-extension`.
