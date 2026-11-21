# IDE bridge (Visual Studio and VS Code)

The MCP server drives open IDE windows through a common set of `Ide*` tools. The same call works in
Visual Studio and VS Code. The reply's `ide` field says which IDE handled it.

## How it works

- Each IDE window runs a bridge on a named pipe, one pipe per window process:
  - Visual Studio: `InstanceRegistry.PipeNameFor(pid)` in the `AiDevHelpers.Bridge` package (source: AiDevHelpers/McpBridge). The VS extension hosts it.
  - VS Code: `\\.\pipe\AiDevHelpers.VSCode.<pid>` (`src/Installers/VsCode/Installer/src/ideBridge.ts`).
- Each window writes a manifest to `%ProgramData%\AiDevHelpers\vs-instances\<pid>.json`:
  `{ pid, pipeName, solutionPath, updatedUtc, ide }`. `ide` is `VisualStudio` or `VsCode`.
- The MCP server reads the manifests (`VsInstanceRegistry`, in the package) and routes each call (`IdeInstanceRouter`, in the package) to the window whose
  `solutionPath` matches. With one instance open, the solution is inferred.
- Routing arguments on every `ide_` tool: `ide` (`VisualStudio` or `VsCode`) and `instancePid` (the process id).
  When the same solution is open in two Visual Studio instances (for example Community and Insiders), a call
  with only `solutionPath` fails with "Several instances have this solution open". Pass `instancePid` to pick one.
  Full rules are in [protocol.md](../conformance/protocol.md#routing-mcp-side).
- Replies that don't arrive within 60 seconds are errors. An instance whose pipe refuses the client (for
  example, one running at a different elevation) is reported as "refused this client".
- Protocol: one JSON line per request, `{"action":"...","args":{...}}`. The reply is one JSON line,
  `{"ok":true,"pid":N,"result":...}` or `{"ok":false,"pid":N,"error":"..."}`. The VS Code bridge adds
  `"unsupported":true` for actions it can't do. The VS bridge never sends that field.
- Watchdog: VS Code's bridge checks every 10 seconds and restarts its pipe and manifest if either is
  missing. The VS bridge uses the same pattern in `BridgePackage`.
- `health` is answered by each bridge directly. It reports the listener state, request count, and pipe name.

## Tools

| Tool | Action | Notes |
|---|---|---|
| `IdeListInstances` | (MCP only) | Lists manifests and their solutions. |
| `IdeBridgeHealth` | `health` | Listener state and request counters. |
| `IdeStartDebugging` / `IdeStopDebugging` | `startDebugging` / `stopDebugging` | VS Code uses the selected or first launch configuration. |
| `IdeSetConfiguration` / `IdeListConfigurations` | `setConfiguration` / `listConfigurations` | VS Code keeps the selected name for `startDebugging`. |
| `IdeSetStartupProject` | `setStartupProject` | VS only. VS Code returns `unsupported`. |
| `IdeRunTests` | `runTests` | VS: `dotnet test` with TRX when `IdeTestRunMode` is `DotnetTest`, otherwise Test Explorer. VS Code: `testing.runAll`. |
| `IdeGetTestResults` | (MCP only) | Reads the newest TRX file in the solution's `TestResults` folder. |
| `IdeBuild`, `IdeClean`, `IdePublish` | `build`, `clean`, `publish` | VS: the IDE build. VS Code: `build` runs the build task; `clean` and `publish` run `dotnet` in the workspace folder. |
| `IdeGetBuildDiagnostics` | `getBuildDiagnostics` | Errors and warnings from the Error List (VS) or diagnostics (VS Code). |
| `IdeCodeCleanup` / `IdeFormatDocument` | `codeCleanup` / `formatDocument` | |
| `IdeGetOutput`, `IdeSearchOutput`, `IdeClearOutput` | `getOutput`, `searchOutput`, `clearOutput` | VS: the named Output pane. VS Code: only the lines this bridge recorded (see Limits). |
| `IdeSetBreakpoint`, `IdeRemoveBreakpoint`, `IdeListBreakpoints` | same names | |
| `IdeStep`, `IdeGetDebugState`, `IdeEvaluate`, `IdeGetLocals` | `step`, `debugState`, `evaluate`, `getLocals` | Locals and evaluate need a paused debugger. |
| `IdeAddWatch`, `IdeRemoveWatch`, `IdeListWatches` | `addWatch`, `removeWatch`, `listWatches` | Watches are kept by the bridge. Values show only while paused. |
| `IdeKillProcess` | `killProcess` | Kills only processes this bridge started. VS starts none, so it refuses every pid. VS Code kills its clean/publish processes. |
| `IdeLaunchInstance` / `IdeKillInstance` | (MCP lifecycle) | Launch or save-and-kill a VS instance. Kill refuses if any document is unsaved. |
| `TerminalSession` | `open`, `tab`, `exec`, `list`, `close` | Runs commands in visible Windows Terminal sessions or headless service-account sessions. See [terminal-session-contract.md](terminal-session-contract.md). |
| `ide_open_document` | `openDocument` | Opens a file and moves the caret to an optional line. |
| `ide_open_solution` | (MCP only) | Launches Visual Studio's devenv with the `.sln`. Does not use the bridge. |
| `bookmark_list`, `bookmark_get`, `bookmark_set`, `bookmark_remove` | `bookmarkList`, `bookmarkGet`, `bookmarkSet`, `bookmarkRemove` | Visual Studio bookmarks. VS Code returns not-supported. See Limits. |

### Helper tools (MCP only, no bridge action)

| Tools | Helper | Notes |
|---|---|---|
| `config_resolve`, `config_read` | ConfigHelper | Real paths for remote debugger, launchSettings, appsettings, user and solution settings. Reads are limited to allowed roots and redact secrets. |
| `error_ingest`, `error_list`, `error_snapshot`, `error_changes` | ErrorHelper | Caller supplies build output. Filters by code and source. Suppression comes from the solution's `.vsconfig` key `aidevhelpersErrors`. |
| `test_watch`, `test_unwatch`, `test_failures`, `test_coverage` | TestingHelper | Watches TestResults while hot reload is active. Coverage uses Coverlet, or the `dotnet-coverage` CLI for Visual Studio. |
| `extension_list`, `extension_info`, `extension_add_plan`, `extension_add_confirm` | ExtensionsHelper | VS Code list and info are local. Visual Studio info queries the Marketplace. Adds are two-step and need approval. |
| `extension_workspace_setup` | ExtensionsHelper | Recommends C# Dev Kit and C# in `.vscode/extensions.json`, and sets `dotnet.defaultSolution` when C# Dev Kit is installed. |

## Limits

- **VS Code output:** VS Code's API can't read other output channels. `getOutput`, `searchOutput`, and
  `clearOutput` cover only the lines this bridge recorded from its own clean and publish runs.
- **VS Code watches:** no API lists them. The bridge evaluates its own list in the active session.
- **VS Code startup project:** no equivalent. Use the launch configuration (`setConfiguration`).
- **VS Test Explorer:** its results aren't exposed to extensions. Use `dotnet test` with TRX mode.
- **VS killProcess:** refused for every pid. Use `IdeKillInstance` to close a VS instance.
- **VS bookmarks:** only bookmarks in open documents are visible. Names are not stored by VS, so `name` comes back null.
- **VS Code bookmarks:** not supported in v1 (`unsupported: true`).
- **VS coverage through the bridge:** not built. Visual Studio coverage uses the `dotnet-coverage` CLI.
- **VS Code hot reload setting:** the bridge action that reads it is not built yet. TestingHelper falls back to `dotnet watch`.
- **Unverified on a real install:** `dotnet.defaultSolution`, the `.vsconfig` key `aidevhelpersErrors`, and the
  Marketplace query shape used by ExtensionsHelper.

## Settings

- `IdeTestRunMode` in the MCP `appsettings.json` (`IdeTools` section): `DotnetTest` or `TestExplorer`
  (default). The VS options page's "IDE test runs use dotnet test with TRX" checkbox writes it.
- The VS options page also edits the MCP advanced settings: verbose logging, context helper and token
  economy switches, context size limit, endpoint strategy, and endpoints. Endpoints are one per line as
  `name|url|primary|models`, and at least one must have `primary` set to `true` when the list isn't empty.

## Build and install

Visual Studio (from the repo root):

```bash
pwsh -File scripts/update-vs-extension.ps1
```

This rebuilds the VSIX and opens VsixInstaller for each VS 2026 instance. Expect a UAC prompt and a
dialog per instance. Commit first: the version comes from git height, so an uncommitted rebuild can
install the same version as before and do nothing.

VS Code bridge (build the Core package first; the Installer depends on it through `file:../Core`):

```bash
pushd src/Installers/VsCode/Core && npm install && npm run build && popd
pushd src/Installers/VsCode/Installer && npm install && npm run build && npx vsce package --out aidevhelpers.vsix && popd
code --install-extension src/Installers/VsCode/Installer/aidevhelpers.vsix --force
```

## Live checks

1. Stop the installed `TokenSaverMCP` service and start the dev server:
   `pwsh -File scripts/run-dev-server.ps1` (needs UAC).
2. VS Code: open a folder with a `.sln` or `.csproj`. The manifest appears in `vs-instances` once the
   extension activates. Send `{"action":"health"}` to its pipe and check `listener_running`.
3. Send `clean`, `searchOutput`, `killProcess` (with an untracked pid, which must be refused), and
   `addWatch` plus `listWatches`.
4. Visual Studio: install the VSIX, reload, and check the manifest, `health`, `runTests`, and
   `killProcess` (refused).
