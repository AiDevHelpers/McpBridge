# Plan: WSL and container tools in aidevtools

Branch: `feat/modelserver-file-edit` (user choice 2026-11-27, same branch). Worktree: `F:\aidevhelpers\wip-worktree`.
Source of behavior: TokenSaver repo `F:\aidevhelpers\TokenSaver` (read only; its code stays untouched, no removal, no forwarders).
Status: draft, Sonnet review folded in, awaiting approval to start.

## Source files (TokenSaver, read only)
- WSL: `TokenSaver.Core/TokenSaver.WslControl/` (`Core/WslProvider.cs`, `SessionWsl.cs`, `WslDebugConfigService.cs`; contracts, models), tools
  `TokenSaver.Host/TokenSaver.MCP/Tools/WslControlTools.cs`, tests `TokenSaver.Core/TokenSaver.WslControl.Tests` (port these, do not reinvent),
  `TokenSaver.Host/TokenSaver.MCP.Tests/Tools/WslControlToolsTests.cs`.
- Containers: `TokenSaver.Host/TokenSaver.MCP/Tools/DevLoopTools.cs` (`DockerPreflightAsync`, lifecycle verbs, exec, logs), tests `DevLoopToolsTests.cs`.

## Decisions
- Shape (user): `wsl` tool, `action` = status, health, run, debug_setup; `container` tool, `action` = logs, start, stop, restart, exec. JSON results.
  Status, health and container status are structured fields (distros array, default flag, state; container state, health, exitCode, finishedAt), not text headers.
- Runtime (user): Docker and Podman. Detect `docker --version`, else `podman --version`; `runtime` argument overrides; the result always reports which runtime ran
  (the `docker` command can be a Podman shim). Docker daemon probe `info --format {{.ServerVersion}}`; Podman uses `{{.Version.Version}}`. Daemon-down message
  differs: Docker Desktop vs `podman machine start`. Inspect format handles a nil or missing healthcheck (`<nil>`) on Podman.
- Process launching: aidevtools `CommandRunner` (argument lists, timeout, output cap). Set `MaxOutputChars` explicitly (default 4M is too large); returned output
  is capped at 20,000 characters with a `capped` flag, as TokenSaver does for `container exec`.
- Server runs in the user session (stdio launch, no `UseWindowsService`), so TokenSaver's session-0 runner (`ConsoleUserProcessRunner`, `SessionWslCommandExecutor`) is
  NOT ported. `AiDevHelpers.Helpers.WindowsSession` has no pipe-capturing runner anyway. A service-mode port would be its own plan.
- WSL without `Community.Wsl.Sdk`: distro list and default come from `wsl.exe -l -v`. Set `WSL_UTF8=1` in `CommandRequest.Environment` and keep a NUL/BOM-strip
  fallback for older `wsl.exe`; no change to shared `CommandRunner`. Commands run inside a distro (`wsl -d X -- ...`) are UTF-8. `WslDistroInfo` loses `BasePath`
  and `DefaultUid` (not available from `wsl -l -v`); state (Running/Stopped) is kept.
- Dropped (reachable in TokenSaver only through `wsl do`): apt update/upgrade/install helpers (15 min timeout, package regex, `BashQuote`) and `BuildAsync`
  (10 min, `wslpath -a`). Listed as dropped in the contract. `wsl do`, `model_route`, `diff_summarize`, `clean_and_chat` are follow-ups.
- `wsl run`: `/bin/bash -c`; `timeoutSeconds` parameter (1-600, default 120; cold start of a stopped distro can exceed 120, tested); error exit -1 with an
  `executionError` field; `timedOut` field; trailing CR/LF trimmed.
- `container logs`: no model distillation. `tailLines` (reject below 1), last N lines plus an `errorLines` filter that reads stdout and stderr (docker writes the
  container's stderr to its own stderr). Works on a stopped container.
- `container exec`: `timeoutSeconds` 1-600 default 60; arguments as a list `exec <name> /bin/sh -c <command>`; timeout kills the client only, documented.
- Security: container name `^[A-Za-z0-9][A-Za-z0-9_.-]*$` plus `--` before the name in logs/start/stop/restart; distro name validated the same way (reject a leading
  dash, for example `--import`); descriptions of `wsl run` and `container exec` say they run arbitrary commands. `debug_setup write=true` writes only
  `<workspaceRoot>/.vscode/launch.json`, uses `FileMode.CreateNew` (no overwrite, no race), refuses a junction `.vscode`, and uses its own small root check
  (no reference from `WslHelper` to `FileEditHelper`).
- Process registry: `ManagedProcessRegistry` lives in MCP.Core, unreachable from helpers; the tool layer uses `CommandRequest.OnStarted` if registration is wanted
  (not in this plan unless U1 shows TokenSaver's tools depend on it for cancellation).
- Wiring per helper, done in the helper's first unit: csproj with `PackageId` and embedded `SelfTest.md`, `Strings.cs` + `Resources/Strings.resx` with every user-facing
  string, test csproj, `.slnx` entries, `ProjectReference`, `ci.yml` pack step; per tool: `WithTools<>`, `SelfTestTools.HelperNames`, `SelfTestCoverageTests`,
  `mcp-tools.md`, `docs/ide-bridge.md`, README. `string_scan` and `HardCodedStringTests` must stay clean.

## Units
- [ ] U1. Contracts. `docs/Host-MCP/wsl-contract.md` and `container-contract.md`: every action, parameter, default, error text, timeout, the health probe (one bash
  script emitting KEY=VALUE lines; healthy = non-empty kernel), debug-setup runtimes (dotnet=vsdbg, python=debugpy, node=inspector) and launch.json shape, preflight order
  (name, `--version`, daemon, inspect), plus the dropped list. Done: each TokenSaver behavior documented or listed as dropped; checked against source.
- [x] U2. (done: Haiku subagent built it; red = CS0246 `IRuntimeCommands`, then 40/40 green; committed 760d582) `ContainerHelper` scaffold and runtime layer. `ICommandExecutor` seam (fake in tests), runtime detection (docker, podman fallback, neither, override, shim
  reported), name validation, daemon check per runtime. Failing tests first. Wiring (csproj, resx, SelfTest.md, slnx, ci.yml).
- [ ] U3. `ContainerHelper` actions. Failing tests first, ported from `DevLoopToolsTests`: status (inspect, nil health), start/stop/restart then re-inspect, exec
  (exit code, stdout, stderr, timeout, list args, cap), logs (tail validation, errorLines from both streams, stopped container), Podman-specific cases.
- [x] U4. (done, committed 760d582; tool tests refusal-only; MCP.Core fails only on WslHelper wiring and the pre-existing RoslynHelper test) `container` tool in MCP.Core: tool tests (JSON shapes, refusals, cancellation with timeout), self-test entries, docs wiring.
- [ ] U5. `WslHelper` scaffold and distro layer. `IWslCommandExecutor` seam (fake), `wsl -l -v` parse (UTF-16 with NULs, `WSL_UTF8`, default marker, state),
  availability, default distro, distro-name validation. Failing tests first, ported from `TokenSaver.WslControl.Tests`. Wiring as in U2.
- [ ] U6. `WslHelper` health and run. Health probe script and parse; `run` (bash -c quoting of `$` and quotes, timeout and cancellation, cold-start case, non-ASCII
  and CRLF output, cap, exit -1 on launch error). Failing tests first.
- [ ] U7. `WslHelper` debug_setup. Backend probe per runtime, install hint, launch.json content, `write` creates only when absent (CreateNew), only under the workspace
  root, refuses a junction `.vscode`. Tests per runtime, existing file kept, path escape refused.
- [x] U5-U8 (done, committed 18b2768: WslHelper 51/51 tests; wsl tool with refusal-only tests; the .vscode junction refusal has no test, it needs a junction; U7 debug_setup is inside WslHelper) `wsl` tool in MCP.Core: tool tests, self-test entries, docs wiring.
- [ ] U9. Docs and gates: `docs/features.md`, `docs/self_test.md`, dogfood log; lite gate with aidevtools tools; full gate before any PR to dev/main. Live checks: a real
  `wsl run` and `wsl status`; a `container` status call if Docker or Podman is installed (record NOT RUN otherwise).
  - Live checks run 2026-11-27 (scratch console harness, outside the repo, referencing both helper projects):
    - `wsl status`: OK. One distro, `kali-linux`, default, Stopped, WSL 2.
    - `wsl run "echo live-ok; uname -r"`: OK. Exit 0, stdout `live-ok` and kernel `6.18.58.1-microsoft-standard-WSL2`, cold start 5.3 s.
    - `container detect`: runtime `docker` (28.4.0). Podman not installed, so no Podman live check (NOT RUN).
    - `container status` on a missing name: returned the Docker Desktop daemon-down error (no elevated pipe access, `//./pipe/docker_engine` missing). The error path works; a running-container status check is NOT RUN (daemon down).
  - Docs: `docs/features.md` updated with `container` and `wsl`. `docs/self_test.md` does not exist in this repo; self-test coverage lives in each helper's `SelfTest.md` and is checked by `SelfTestCoverageTests`.
  - Lite gate: `lint_check` 0 findings; `string_scan` only the WslService probe lines (allowlisted); `test_affected` 4 projects; `resx_check` reports unused keys that are read through variables (false positives). `dotnet test`: WslHelper 51/51, ContainerHelper 40/40, MCP.Core 388/389, the one failure is the pre-existing `HardCodedStringTests` RoslynHelper/ActionDispatcher.cs:18 case.
  - Dogfood log: entry appended to `~/.claude/docs/dogfood.md`.
  - Still open: full gate (required before any PR to dev or main; no PR opened).

## Follow-ups (not in this plan)
- `wsl do`, `model_route`, `diff_summarize`, `clean_and_chat` after a generate/chat call exists on `ModelServerHelper`.
- Retire the duplicate TokenSaver debug, test and diagnostics tools.
- Service-mode (session 0) WSL execution, only if aidevtools ever runs as a service.

## Rules
- Each unit: failing test, implement, build, test, commit, push branch. No PR unless the user asks.
- Code edits go through TokenSaver `edit_file`; new files need Write (edit_file cannot create files, logged in the dogfood gaps).
