# Connecting clients to the HTTP MCP server

The AiDevHelpers MCP server can run over streamable HTTP as well as over stdio. The HTTP server is the one the tray service runs
(unit 1a of `docs/plans/2026-11-26-tray-service.md`). Its endpoint is:

    http://127.0.0.1:5058/mcp

- The server listens on the loopback address only. Other machines cannot reach it.
- No token is needed. The server checks the `Host` and `Origin` headers instead: a browser page on another site is refused.
- Sessions are stateful and expire after 30 minutes without a request. A client that gets 404 for its session must start a new session.
- The port is fixed at 5058 for now. If it is taken, the server writes the cause to
  `%ProgramData%\AiDevHelpers\mcp-http\status.json` and exits.
- The stdio server still works for every client. Keep an existing stdio setup as it is.

## Per-client setup

Status below is what was checked. Only the HTTP endpoint itself was checked live; the client steps are not yet verified in each client.

| Client | Transport | Status |
|---|---|---|
| Claude Code | HTTP | Steps below, not yet verified |
| VS Code (GitHub Copilot) | HTTP | Steps below, not yet verified |
| Cursor | HTTP | Steps below, not yet verified |
| Visual Studio | Stdio only, for now | HTTP support is not verified |
| Claude Desktop | Stdio only | Use the stdio server, or `mcp-remote` to bridge to HTTP |

### Claude Code

    claude mcp add --transport http aidevhelpers http://127.0.0.1:5058/mcp

### VS Code (`.vscode/mcp.json`)

    {
      "servers": {
        "aidevhelpers": {
          "type": "http",
          "url": "http://127.0.0.1:5058/mcp"
        }
      }
    }

### Cursor (`~/.cursor/mcp.json` or the project's `.cursor/mcp.json`)

    {
      "mcpServers": {
        "aidevhelpers": {
          "url": "http://127.0.0.1:5058/mcp"
        }
      }
    }

### Visual Studio

Keep the stdio server for now. Visual Studio's HTTP MCP support has not been checked, so HTTP stays opt-in until it is.

### Claude Desktop

Claude Desktop only runs stdio servers. Keep the stdio entry, or bridge to HTTP with `mcp-remote`:

    {
      "mcpServers": {
        "aidevhelpers": {
          "command": "npx",
          "args": ["mcp-remote", "http://127.0.0.1:5058/mcp"]
        }
      }
    }

## Troubleshooting

- **Connection refused:** the server is not running, or it is on another port. Check `/healthz` at `http://127.0.0.1:5058/healthz`.
- **Port taken:** read `%ProgramData%\AiDevHelpers\mcp-http\status.json`.
- **404 for a session you started earlier:** the session expired after 30 minutes idle. Re-initialize.
- **403 or 421:** the request carried a non-loopback `Origin` or `Host` header.

## Shared state

Sessions over HTTP and the stdio server are separate processes. They share the server control state, terminal registry and
the IDE manifests, but event buffers and the static state listed in the plan are per process.
