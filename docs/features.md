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

## Files and model server (no IDE needed)

- `file_edit`: apply, insert, delete, rename and rollback text edits inside a workspace root, with a byte-exact backup before every write, an atomic write and the file's BOM kept. See [Host-MCP/file-edit-contract.md](Host-MCP/file-edit-contract.md).
- `model_server_status`, `model_server_capacity`: read the SimpleModelServer router over HTTP (loopback only). See [Host-MCP/model-server-contract.md](Host-MCP/model-server-contract.md).
- `container`: logs, start, stop, restart and exec on a Docker or Podman container, with JSON results and a 20,000-character output cap. See [Host-MCP/container-contract.md](Host-MCP/container-contract.md).
- `wsl`: WSL distro status and health, `run` a bash command line in a distro, and `debug_setup` for a debug launch config (writes `.vscode/launch.json` only when absent). See [Host-MCP/wsl-contract.md](Host-MCP/wsl-contract.md).

## Terminal sessions

- `terminal_session`: visible Windows Terminal tabs or headless service-account sessions, with elevated sessions. See [terminal-session-contract.md](terminal-session-contract.md).
- `script_run`: runs `.ps1`, `.cmd`, `.bat`, `.sh`, `.py` and `.js` with the matching interpreter, with timeouts, no-output stops and repeat-failure advice.
- `tool_install`: two-step winget install of missing interpreters, with user approval.

## Elevated scripts

- Script Helper broker (`elev`): runs scripts from a repo's approved `.elevated.json` allowlist, with fingerprint and approval checks, through `run_elevated_script`.
- Repo setup scan for projects and scripts that need administrator rights.
- Chat-first approvals: `elev setup tools` is refused before UAC unless the yes was given in chat; `repo-setup`, terminals and rules refuse console questions and say to ask in chat.
- Workloads: `workload_list`, `workload_check`, and `workload_install_plan`/`workload_install_confirm`, installed through the elevated helper from a pinned table (`scripts/workload-install.sha256`).

## Source control and CI

- `git_status` and `git_diff` run at once; every write goes through `git_plan` then `git_confirm` with a single-use token.
- Repo rules block commits and pushes on protected branches and bad branch names.
- `pr_checks` and `ci_failure` read pull request checks and failed CI logs through `gh`.

## Build, test and quality

- Gate: `gate_plan` and `gate_run` run the repo's full gate.
- Tests: `test_affected`, `test_watch`, `test_failures`, `test_coverage`, `live_test_start`, and `test_hosts_list`/`test_hosts_stop` for stray test hosts.
- Lint: `lint_check`.
- Localization: `string_scan`, `resx_check`, `localization_sync_check`, `localization_sync`, `localization_extract`, `localization_culture_check`, `localization_pseudo`, `localization_translate`.
- Packages: `package_check`, `package_restore`, `package_outdated`.
- Errors: `error_ingest`, `error_list`, `error_snapshot`, `error_changes` with suppression.
- Diagnostics: `dotnet_diag` wraps dotnet-dump, stack, counters, gcdump, trace and symbol.
- Build environment: `build_env` lists Visual Studio installs with MSBuild and VSSDK targets.
- Projects: `project_types_list`, `project_audit`, `project_fix_plan`/`confirm`, `project_add_plan`/`confirm`, `project_add_test_plan`/`confirm`. Standards in `.aidevhelpers/project-standards.json`. With no pinned Test SDK or runner, the test-project plan asks the user in chat for the version.

## Configuration and extensions

- `config_resolve` and `config_read`: real paths for launch, app and solution settings, with secrets redacted.
- Extensions: list, describe and add VS Code and Visual Studio extensions (adds are two-step); workspace setup for C# Dev Kit.
- Layered settings: global, repo `.aidevhelpers/settings.json`, per-user `user.json`.

## Server operations

- `server_control_set` and `server_control_status` pause, close and resume every server for an update.
- `self_test_list` and `self_test_read` expose each helper's self-test. See [self_test.md](self_test.md).
- `AiDevHelpers.MCP.Http` serves the same tools over streamable HTTP on `127.0.0.1` for several clients at once: bearer token, Host and Origin checks, stateful sessions, `/healthz`. See [mcp-http.md](mcp-http.md).
- The tray's IDE Instances tab lists running Visual Studio and VS Code instances with their repo, branch, path, start time and user. It is read-only.
- The tray reports the HTTP MCP's connection state (Connected, Reconnecting, Disconnected, with a reason) and the `AiDevHelpers` service state. It only sends the token to the process that owns the MCP port. See [mcp-http.md](mcp-http.md#the-trays-view-of-the-server).

## Packaging and CI

- Bridge, MCP.Core and the packable helpers publish to GitHub Packages on a `v*` tag.
- Pull requests to `main` and `dev` run `build-and-test` and `vscode-extension`.
