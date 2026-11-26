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
| `goToDefinition`, `findAllReferences` | `file`, `line` (1-based), optional `column` (1-based) | `{file, line, column, command, caret_line, caret_column, active_file}` | Places the caret, then runs the IDE command: VS `Edit.GoToDefinition` / `Edit.FindAllReferences`; VS Code `editor.action.revealDefinition` / `references-view.findReferences`. Results show in the IDE, not in the reply. |
| `formatSolution` | none | `{formatted, files}` | Formats every `.cs` file under the workspace (VS: the solution folder), saving each one. Skips bin, obj, .git, node_modules and TestResults. |
| `openSolution` | `solutionPath` | `{solution_path, opened}` (VS) | VS Code opens the solution's folder. The MCP tool `ide_open_solution` launches devenv instead of using this action. |
| `bookmarkList` | none | array of `{id, file, line, name}` | VS only. Reads native markers from open documents. |
| `bookmarkGet` | `id` (`file|line`) | one bookmark, or null | VS only. |
| `bookmarkSet` | `file`, `line`, optional `name` | the bookmark | VS only. `name` is not stored by VS and comes back null. |
| `bookmarkRemove` | `id` | true or false | VS only. |
| `outputWrite` | `channel`, `text` | `{channel, written}` | Appends `text` and a newline to a named channel, creating it on first use. VS: an Output window pane found by a GUID derived from the name. VS Code: an output channel. Missing `channel` or `text` is an error. Channels can't be read back. |
| `gitStatus`, `gitStage`, `gitCommit`, `gitPull`, `gitPush`, `gitFetch`, `gitCreateBranch`, `gitDeleteBranch`, `gitSwitch`, `gitStashPush`, `gitStashPop`, `gitStashApply` | per action (`paths`, `message`, `branch`, optional `repoPath`) | `gitStatus`: `{branch, ahead, behind, changes}`; others `{...ok}` | VS Code only, through its built-in git extension. The MCP side approves every write first. `repoPath` picks the repository whose root matches; without it the first open repository is used. |

VS Code replies to the four bookmark actions with `"unsupported":true` (D8).

| Action | Args | Result | Notes |
|---|---|---|---|
| `hotReloadState` | none | `{ide, enabled, setting}` (VS Code only) | Reads `csharp.experimental.debug.hotReload` through the extension host settings API. `enabled` is null when unset. |
| `testHotReloadState` | none | `{ide, enabled, setting, settingsPath}` (Visual Studio only) | Reads `test.hotReload` from the instance's `settings.json`. The instance folder comes from its `state.json`, falling back to the folder name. |
| `continuousTestingStart` | none | `{ide, continuous, command}` (VS Code only) | Runs `testing.startContinuousRun`. |
| `continuousTestingStop` | none | `{ide, continuous, command}` (VS Code only) | Runs `testing.stopContinuousRun`. |
| `continuousTestingState` | none | `{ide, tracked}` (VS Code only) | The bridge's own record of its last start or stop. VS Code has no API to read continuous mode. |

A call that can't reach an instance within 60 seconds gets an error reply. An instance that accepts the connection but never replies is reported as "No reply".

## Fixtures

- `fixtures.json` runs each entry against the bridge's generic handler. An entry checks the reply shape
  (`ok`, `reply_keys`), not the action's result, so action results are tested in each bridge's own tests.

## Events pipe

Visual Studio only. Separate from the request pipe, which is unchanged (one request, one reply).

- Name: `AiDevHelpers.VS.<pid>.events`. Up to 8 concurrent subscribers.
- Subscribe: the client connects and writes one line `{"action":"subscribe","args":{"events":["buildDone", ...]}}`.
  Omit `events` (or pass an empty list) for all events.
- Reply, one line: `{"ok":true,"pid":N,"subscriptionId":"..."}`. A refused subscribe is `{"ok":false,"pid":N,"error":"..."}`.
- Then the bridge writes event lines `{"event":"<name>","seq":N,"data":{...}}`. `seq` starts at 1 for each subscription.
- `{"event":"heartbeat","seq":N}` every 15 seconds of silence. A client may treat 45 seconds without any line as disconnected.
- `{"event":"dropped","seq":N,"data":{"count":K}}` precedes the next event when K events were dropped. A subscriber
  holds 500 events; the oldest are dropped first.
- `{"event":"closed","reason":"..."}` then the connection ends. Reasons: `bridgeStopped` (bridge disposed),
  `slowSubscriber` (a write took more than 30 seconds), `unsubscribed`.
- Unsubscribe: the client closes its connection. No separate request.
- Event names: `solutionOpened`, `solutionClosing`, `buildBegin`, `buildDone`, `enterBreakMode`, `enterRunMode`,
  `contextChanged`, `documentSaved`, `documentOpened`, `documentClosing`.
- A VS Code bridge does not offer this pipe; `ide_subscribe_events` reports it as an error for VS Code.

## Actions added in phases 1 to 6

Bridge actions behind the tools in `docs/ide-bridge.md`. Visual Studio implements all of them. The VS Code bridge implements all except `listTasks`, `addTask` and `clearTasks`, which it refuses as unsupported. Each write action takes `confirm` (default false) and replies `planned` or `applied`.

VS Code differences:
- `undoGroup` reverses the last edit group this bridge applied. VS Code has one unnamed undo stack, so the group is not chosen by name.
- `listExceptions` and `setExceptionBreak` cover only the debug adapter's exception filters. Their replies carry `partial: true`.
- `semanticDefinition`, `semanticReferences` and `renameSymbol` use VS Code's language providers, not Roslyn.
- `writeActivityLog` appends to an "AiDevHelpers" output channel, not ActivityLog.xml.
- `setStatus` text clears after a VS Code timeout. Visual Studio keeps it until replaced.

- Debugger: `listProcesses`, `listDebuggedProcesses`, `listThreads`, `listStackFrames`, `listExceptions`, `setExceptionBreak`, `setBreakpointCondition`, `enableBreakpoint`, `disableBreakpoint`, `attachProcess`, `detachAll`.
- Editor and documents: `findInFiles`, `replaceInFiles`, `listDocuments`, `saveDocument`, `closeDocument`, `undoGroup`, `listCodeElements`.
- Solution and projects: `projectProperties`, `addProjectItem`, `removeProjectItem`, `closeSolution`.
- Tasks and shell services: `listTasks`, `addTask`, `clearTasks`, `setStatus`, `showInfoBar`, `writeActivityLog`, `getSetting`.
- Roslyn: `semanticReferences`, `semanticDefinition`, `renameSymbol`.
- Events: subscription over `AiDevHelpers.VS.<pid>.events`, not a request action.

`setBreakpointCondition` takes `hitCountType` with the values `always`, `equal`, `multiple` or `greaterOrEqual`.
