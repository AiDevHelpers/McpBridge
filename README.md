# AiDevHelpers.McpBridge

Standalone MCP server and IDE integrations for Visual Studio and VS Code. The server routes IDE tools to registered IDE instances and also provides terminal session tools.

## Components

- `src/MCP/Server` (`AiDevHelpers.MCP`) — MCP server exe for IDE and terminal tools. Shared tools live in `src/MCP/Core`.
- `src/Installers/Vs2026/Installer` — Visual Studio 2026 extension (VSIX). Shared bridge code lives in `src/Installers/Vs2026/Core`.
- `src/Installers/VsCode/Installer/AiDevTools.VsCode` — VS Code extension (TypeScript). Its protocol and buffer code lives in `src/Installers/VsCode/Core/AiDevTools.VsCode.Core`.
- `src/Helpers/LogHelper` — log parsing and query support used by the bridge.
- `src/Helpers/TerminalControl` — Windows Terminal session management.
- `src/Helpers/WindowsSession` — Windows session helpers.
- `src/Helpers/ConfigHelper` — real paths and redacted reads for config files.
- `src/Helpers/ErrorHelper` — build errors, warnings and notices, with snapshots and suppression.
- `src/Helpers/ExtensionsHelper` — list, describe and add VS Code and Visual Studio extensions; workspace recommendations; the Visual Studio installs with their MSBuild and VSSDK targets.
- `src/Helpers/TestingHelper` — hot-reload test failures, and code coverage.
- `src/Helpers/BookmarkHelper` — bookmark model and service over the IDE bridge.
- `src/Helpers/LintHelper` — lint setup checks (editorconfig, NoWarn, tsconfig strict, ESLint, TSLint, dotnet format, duplicate resource names), the hardcoded-string scanner and the resx key check.
- `src/Helpers/SourceControlHelper` — git status, diff, and two-step approved writes through git.exe; pull request checks and failed CI logs through gh.
- `src/Helpers/OutputHelper` — writes to named output channels in Visual Studio and VS Code.
- `src/Helpers/PackageHelper` — NuGet, pip and npm package setup checks.
- `src/Helpers/GateHelper` — the full gate, from the repo reference file or the detected stack.

## Documentation

- [Features](docs/features.md)
- [IDE bridge tools and setup](docs/ide-bridge.md)
- [Protocol conformance](conformance/protocol.md)
- [Terminal session tool contract](docs/terminal-session-contract.md)
- [Bridge self-test checklist](docs/self_test.md)
- [Remaining work plan](docs/plans/remaining-work-2026-11-19.md)
- [Windows Terminal standard session plan](docs/plans/wt-standard-session.md)

The bridge follows the shared [protocol fixtures](conformance/fixtures.json). See `docs/ide-bridge.md` for extension installation and live IDE setup.

## CI and branches

- Pull requests to `main` and `dev`, and `v*` tags, run `build-and-test` (the solution build, tests and the Visual Studio VSIX) and `vscode-extension` (the VS Code extension tests and package). Pushes to branches don't run CI.
- `main` and `dev` require both checks before a merge. `wip` requires none, blocks deletion, and allows force pushes.
- A `v*` tag also publishes the Bridge, MCP.Core and each helper that is packable to GitHub Packages.
