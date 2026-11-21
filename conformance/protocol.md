# IDE bridge protocol (conformance)

Every IDE bridge follows these rules. The C# bridge (`AiDevHelpers.Bridge`, hosted by the Visual Studio
extension) and the VS Code bridge (`src/Installers/VsCode/Installer/AiDevTools.VsCode/src/ideBridge.ts`) both must pass the
fixtures in `fixtures.json`.

## Pipe

- Name: `AiDevHelpers.VS.<pid>` for Visual Studio, `AiDevHelpers.VSCode.<pid>` for VS Code. `<pid>` is the
  process that hosts the bridge.
- Local named pipe on `\.\pipe\<name>`. One request per connection.
- A pipe owned by a process at a different elevation refuses clients at the other level. The MCP reports
  that as "pipe refused this client", not as a hang.

## Request and reply

- One JSON line per request: `{"action":"...","args":{...}}`.
- One JSON line per reply:
  - success: `{"ok":true,"pid":N,"result":...}`
  - failure: `{"ok":false,"pid":N,"error":"..."}`
- `unsupported` is optional. Only bridges that can refuse an action (VS Code) may add `"unsupported":true`.
  Readers must accept replies with or without it.
- A malformed request line gets a failure reply, never a dropped connection.
- Every failure gets a failure reply, including unexpected exceptions. A connection closed without a reply
  reads as an empty error to the caller.

## health

- `{"action":"health"}` is answered by the bridge itself, not by the action handler.
- The `result` object has `listener_running` (bool), `pipe_name`, `started_utc`, `last_request_utc`,
  `requests_handled`, and `solution_path` (string or null).

## Manifest

- Path: `%ProgramData%\AiDevHelpers\vs-instances\<pid>.json`.
- Fields: `pid`, `pipeName`, `solutionPath` (string or null), `updatedUtc`, `ide`, `logRoots`.
- `ide` is `VisualStudio` or `VsCode`. `logRoots` is an array (empty for Visual Studio).
- Written by the bridge on start and rewritten by the watchdog. Removed on dispose.

## Watchdog

- Checks every 10 seconds. Restarts the pipe if the listener stopped, and rewrites the manifest if it is missing.

## Routing (MCP side)

Every `ide_` tool takes these optional arguments, and the router applies them in this order:

1. `ide` (`VisualStudio` or `VsCode`) keeps only instances of that IDE.
2. `instancePid` keeps only the instance with that process id. No match is an error.
3. `solutionPath` keeps instances whose solution matches. No match is an error. More than one match is an
   error unless `instancePid` picked one.
   When more than one instance still matches, no `ide` was given, and `.claude/aidevhelpers.json` next to the
   solution holds `preferredIde`, the preferred IDE's instance is used if exactly one of the matches is that IDE.
   Otherwise it stays an error. The preference only breaks this tie; `ide` and `instancePid` always win.
4. With no `solutionPath`: one instance is used; several is an error that asks for `solutionPath` or `instancePid`.
   The stored preference is not consulted, because there is no solution to find the repo config from.

A reply that does not arrive within 60 seconds is an error, so a stalled bridge cannot hang the caller.

## Actions added in v1 of the helpers

| Action | Args | Result | Notes |
|---|---|---|---|
| `openDocument` | `file`, optional `line` (1-based) | `{file, line, opened}` | Missing file is an error. VS Code reveals the line. |
| `openSolution` | `solutionPath` | `{solution_path, opened}` (VS) | VS Code opens the solution's folder. The MCP tool `ide_open_solution` launches devenv instead of using this action. |
| `bookmarkList` | none | array of `{id, file, line, name}` | VS only. Reads native markers from open documents. |
| `bookmarkGet` | `id` (`file|line`) | one bookmark, or null | VS only. |
| `bookmarkSet` | `file`, `line`, optional `name` | the bookmark | VS only. `name` is not stored by VS and comes back null. |
| `bookmarkRemove` | `id` | true or false | VS only. |
| `outputWrite` | `channel`, `text` | `{channel, written}` | Appends `text` and a newline to a named channel, creating it on first use. VS: an Output window pane found by a GUID derived from the name. VS Code: an output channel. Missing `channel` or `text` is an error. Channels can't be read back. |

VS Code replies to the four bookmark actions with `"unsupported":true` (D8).

| Action | Args | Result | Notes |
|---|---|---|---|
| `hotReloadState` | none | `{ide, enabled, setting}` (VS Code) | Reads `csharp.experimental.debug.hotReload` through the extension host settings API. `enabled` is null when unset. |
| `testHotReloadState` | none | `{ide, enabled, setting, settingsPath}` (VS) | Reads `test.hotReload` from the instance's `settings.json`. The instance folder comes from its `state.json`, falling back to the folder name. |
| `continuousTestingStart` | none | `{ide, continuous, command}` (VS Code) | Runs `testing.startContinuousRun`. |
| `continuousTestingStop` | none | `{ide, continuous, command}` (VS Code) | Runs `testing.stopContinuousRun`. |
| `continuousTestingState` | none | `{ide, tracked}` (VS Code) | The bridge's own record of its last start or stop. VS Code has no API to read continuous mode. |

A call that can't reach an instance within 60 seconds gets an error reply. An instance that accepts the connection but never replies is reported as "No reply".

## Fixtures

- `fixtures.json` runs each entry against the bridge's generic handler. An entry checks the reply shape
  (`ok`, `reply_keys`), not the action's result, so action results are tested in each bridge's own tests.
