# TerminalControl self test

Tool: `TerminalSession` (actions `open`, `tab`, `exec`, `list`, `close`).

See also docs/terminal-session-contract.md. The real Windows Terminal smoke tests open windows, so they run only with the operator's intent.

## Full self test

Fixture: a headless ServiceAccount session, so no window opens.

- `TerminalSession` (action = open, launchMode = ServiceAccount): PASS when the reply has a `sessionId` and a first `tabId`.
- `TerminalSession` (action = tab, sessionId from open): PASS when a second tab is returned.
- `TerminalSession` (action = exec, sessionId and tabId, command = `echo ok`): PASS when the output contains `ok`.
- `TerminalSession` (action = list): PASS when the session appears.
- `TerminalSession` (action = close, sessionId): PASS when the session is gone from `list`.
- `TerminalSession` (action = unknown): PASS when the reply is an error that names the allowed actions.

Record each as PASS, FAIL or NOT RUN with the exact arguments and reply.
