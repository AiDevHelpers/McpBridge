# Shared vs per-session state in the MCP server

One MCP server process can now serve several chat clients over HTTP (see `docs/mcp-http-clients.md`). Each client has its own
MCP session. The state below is shared by every session in the process, unless the table says otherwise.

| State | Where | Scope | Notes |
|---|---|---|---|
| Plan-then-confirm tokens | `PlanOwnership` (`ServerControlRuntime.cs`) | Per session | A token planned in one session is refused when another session confirms it. Learned from any tool result with a `token` field. |
| Control state (running, paused, closing) | `ServerControl` state file | Shared by all processes | Pause and closing are sent to every session (`ControlSession.BroadcastAsync`). |
| Session servers | `ControlSession` | Per session, keyed by session id | Remembered from each tool call; a session whose send fails is forgotten. |
| Event subscriptions | `EventSubscriptions` (by pid) | Shared | A subscription is for an IDE instance's events, not a chat. |
| Error tools | `ErrorTools.Services` | Shared | Error snapshots are per process. |
| Localization stores | `LocalizationTools` | Shared | Read by every session. |
| Test host plans | `TestHostTools.Plans` | Shared | Plans are confirmed through `PlanOwnership` like other tokens. |
| Testing state and live runs | `TestingState`, `LiveRuns` | Shared | One live test run per process; a second session sees it. |
| Managed processes | `ManagedProcessRegistry` | Shared | Processes started by any session. |
| Terminal sessions | `TerminalSessionManager`, `SessionRegistry` | Shared | A terminal opened in one session is visible to the others. |

## Rules for new state

- Per-chat state (a plan, a confirmation, a subscription that belongs to one chat) goes in a per-session store keyed by session id.
- Shared state is for things that describe the machine or an IDE instance, not a chat.
- Logging and notifications go to every session through `ControlSession.BroadcastAsync`.

## Limits

- Stdio runs one session per process, so the per-session rules do nothing there.
- Event buffers are per process. An HTTP client and a stdio client see different events.
- Sessions do not survive a restart. Clients re-initialize.
