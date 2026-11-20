# AiDevHelpers.McpBridge

Standalone MCP server and IDE integrations for Visual Studio and VS Code. The server routes IDE tools to registered IDE instances and also provides terminal session tools.

## Components

- `src/MCP/Server` (`AiDevHelpers.MCP`) — MCP server exe for IDE and terminal tools. Shared tools live in `src/MCP/Core`.
- `src/Installers/Vs2026/Installer` — Visual Studio 2026 extension (VSIX). Shared bridge code lives in `src/Installers/Vs2026/Core`.
- `src/Installers/VsCode/Installer` — VS Code extension. Its protocol and buffer code lives in `src/Installers/VsCode/Core`.
- `src/Helpers/LogHelper` — log parsing and query support used by the bridge.
- `src/Helpers/TerminalControl` — Windows Terminal session management.
- `src/Helpers/WindowsSession` — Windows session helpers.

## Documentation

- [IDE bridge tools and setup](docs/ide-bridge.md)
- [Protocol conformance](conformance/protocol.md)
- [Terminal session tool contract](docs/terminal-session-contract.md)
- [Bridge self-test checklist](docs/self_test.md)
- [Remaining work plan](docs/plans/remaining-work-2026-11-19.md)
- [Windows Terminal standard session plan](docs/plans/wt-standard-session.md)

The bridge follows the shared [protocol fixtures](conformance/fixtures.json). See `docs/ide-bridge.md` for extension installation and live IDE setup.
