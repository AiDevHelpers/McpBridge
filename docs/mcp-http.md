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

## What the server checks

Every request must pass, in order:

1. `Host` is `127.0.0.1:<port>` or `localhost:<port>` (403 otherwise; blocks DNS rebinding).
2. No `Origin` header (403 otherwise; browsers always send one, MCP clients do not).
3. `Authorization: Bearer <token>` (401 otherwise).

The server binds to `127.0.0.1` only. `GET /healthz` returns `status`, `version`, `pid` and `port` (same checks apply).

The token is not a boundary against code that already runs as you. Tray confirmation of dangerous tools (unit 1c) is the real control for those.

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

Both servers keep tool state in static fields, so over HTTP every client shares it. Plan-then-confirm tokens will be bound to the session that made them in unit 1b. Until then another client could confirm a plan it did not make.

| State | Scope today |
|---|---|
| Event subscriptions (`ide_subscribe_events`), keyed by IDE pid | shared |
| Error snapshots, localization and test-host plans, test state | shared |
| Managed processes, live test runs | shared |
| Terminal sessions | shared |
| Server control (`server_control_set`) | the last caller's chat only (fixed in 1b) |
