# MCP over HTTP

`AiDevHelpers.MCP.Http` serves the same tools as the stdio `AiDevHelpers.MCP` exe over streamable HTTP, so one process can serve every IDE and chat client. The stdio exe is unchanged; existing client configs keep working.

Status: the server and its checks are done (unit 1a of `docs/plans/2026-11-26-tray-service.md`). Starting it automatically in your session comes with the Windows service (unit 2). Until then run the exe yourself.

## Run it

```powershell
AiDevHelpers.MCP.Http.exe --port 5757
```

Options: `--port <n>` (default 5757, `0` picks a free port), `--token <file>`, `--endpoint <file>`. By default the files live in `%LOCALAPPDATA%\AiDevHelpers\mcp-http\`.

- `token`: the bearer token, created on first start. Only your user and SYSTEM can read it.
- `endpoint.json`: `pid`, `port`, `version`, `startedUtc`, or `error` when startup failed. A busy port writes `"error": "port-in-use"` and the exe exits with code 2.

## Install as a Windows service

The Host service starts the HTTP MCP in your logon session, restarts it after a crash, and restarts it when `/healthz` stops answering. Install it from a folder that holds the published `AiDevHelpers.Host.exe`, `AiDevHelpers.MCP.Http.exe` and their files, in an elevated shell:

```powershell
pwsh scripts/service-install.ps1 -SourceDirectory <published folder>
pwsh scripts/service-uninstall.ps1
```

- Both scripts check their own SHA-256 against `scripts/service-install.sha256` and `scripts/service-uninstall.sha256` before doing anything. After a reviewed change run the script once with `-WritePin` and commit the pin.
- `-DryRun` prints the steps (lines that start with `PLAN:`) and changes nothing. `-Reinstall` replaces an installed service; `-NoStart` installs without starting.
- The service installs under `%ProgramFiles%\AiDevHelpers`. The folder is created with SYSTEM and Administrators full control and Users read-only before any file is copied, and an existing folder owned by an ordinary user is refused. An `install-manifest.json` records the SHA-256 of every installed file.
- The service runs as LocalSystem, starts automatically, and restarts itself after a failure (5 s, 5 s, then 30 s).
- The token and endpoint record live in `%ProgramData%\AiDevHelpers\mcp-http`, open only to your user, SYSTEM and Administrators.
- The uninstall script removes the service, the install folder (only when it holds `install-manifest.json`) and the token folder (unless `-KeepData`). The tray's run-at-login entry is removed by the tray.

If you are logged on over RDP, the server runs in that session; with a local console user it runs in the console session. One server serves one user.

## The tray's view of the server

The tray (`AiDevHelpers.Tray`) reads the same endpoint record and token, through the shared `AiDevHelpers.Mcp.Client` library, and reports the server's state:

- **Connected:** the record is valid, the process listening on the port is the one that wrote the record, and `/healthz` answers 200 with the same pid.
- **Reconnecting:** a connected server failed its health check fewer than three times in a row.
- **Disconnected:** with a reason: `no-endpoint`, `endpoint-invalid`, a startup error such as `port-in-use`, `no-token`, `not-listening`, `listener-mismatch` (another process owns the port, so the token is never sent), or `unhealthy`.

The tray never sends the token to a process that does not own the port. The state is observable: a new subscriber gets the current state at once, and polling runs only while someone subscribes.

Services are read the same way: the tray reports the `AiDevHelpers` service as running, stopped, pending, not installed, or unknown. Starting and stopping go through the pinned install scripts with a UAC prompt.

## What the server checks

Every request must pass, in order:

1. `Host` is `127.0.0.1:<port>` or `localhost:<port>` (403 otherwise; blocks DNS rebinding).
2. No `Origin` header (403 otherwise; browsers always send one, MCP clients do not).
3. `Authorization: Bearer <token>` (401 otherwise).

The server binds to `127.0.0.1` only. `GET /healthz` returns `status`, `version`, `pid` and `port` (same checks apply).

The token is not a boundary against code that already runs as you. Tray confirmation of dangerous tools is the real control for those.

## Dangerous tools need your confirmation

A client steered by hostile text (a log line, a build message) could call tools that run commands. Over HTTP these tools ask the tray before each call:

`terminal_session`, `script_run`, `run_elevated_script`, `ide_kill_instance`, `ide_kill_process`, `ide_attach_process`, `ide_evaluate`, `ide_start_debugging`, `tool_install`, `gate_run`, `live_test_start`, `dotnet_diag`, `test_hosts_stop`, `git_confirm`, `extension_add_confirm`, `server_control_set`, `ide_launch_instance`, `ide_reset_instance`, `ide_publish`.

- The list is `ToolRiskCatalog`. A tool that is not listed as safe, including one added later, counts as dangerous, and a test fails until it is classified. Builds and test runs stay safe: they run code from a repository you already opened.
- Each request carries a fresh nonce and a hash of the call's arguments. The answer must return both, so an approval cannot be reused for other arguments.
- It fails closed: no tray running, an error, a wrong answer, or no answer within 30 seconds refuses the call.
- The tray is the pipe server (`AiDevHelpers.Tray.Approval.<your SID>`). The server process must be `AiDevHelpers.Tray.exe` in the same folder as the HTTP exe; another process serving that pipe name gets nothing. The tray itself arrives in the tray unit, so until then these calls are refused over HTTP. The stdio exe is unchanged.

## Sessions

Sessions are stateful (the library defaults to stateless, which is switched off). A restart ends every session; clients get 404 for the old session id and must initialize again. Idle sessions expire after 30 minutes.

## Client setup (unverified checklist)

Each entry is untested against the real client. Mark it done after you confirm a tool list and one tool call.

| Client | Config | Verified |
|---|---|---|
| Claude Code | `claude mcp add --transport http aidevhelpers http://127.0.0.1:5757/mcp --header "Authorization: Bearer <token>"` | no |
| VS Code | `.vscode/mcp.json`: `{"servers":{"aidevhelpers":{"type":"http","url":"http://127.0.0.1:5757/mcp","headers":{"Authorization":"Bearer ${input:aidevhelpers-token}"}}},"inputs":[{"id":"aidevhelpers-token","type":"promptString","password":true}]}` | no |
| Cursor | `mcp.json`: `{"mcpServers":{"aidevhelpers":{"url":"http://127.0.0.1:5757/mcp","headers":{"Authorization":"Bearer ${env:AIDEVHELPERS_TOKEN}"}}}}` | no |
| Visual Studio | `.mcp.json` with the same shape as VS Code | no |
| Claude Desktop | stdio only: keep using the stdio exe, or bridge with `mcp-remote` | no |

Prefer an environment variable or an input prompt over pasting the token into a config file that is committed.

## Shared state

Both servers keep tool state in static fields, so over HTTP every client shares it. Plan-then-confirm tokens are random, single use and expire after 15 minutes; only the planning client receives its token, so another client cannot confirm a plan it never saw.

| State | Scope today |
|---|---|
| Event subscriptions (`ide_subscribe_events`), keyed by IDE pid | shared |
| Error snapshots, localization and test-host plans, test state | shared |
| Managed processes, live test runs | shared |
| Terminal sessions | shared |
| Server control (`server_control_set`) | the state is shared; pause, close and repo notices go to every connected client |
