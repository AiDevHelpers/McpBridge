# Visual Studio API coverage: implementation plan, 2026-11-25

Status: draft for review. Full specification. Covers every gap from the Visual Studio API review, with per-tool contracts,
files, API members, tests and done criteria. Replaces the earlier gap-list version of this file.

## 1. Goal and scope

Add the Visual Studio (EnvDTE, EnvDTE80/90/90a/100 and VS SDK) areas the bridge does not use yet, exposed as `ide_*` MCP tools
through the existing bridge protocol, with the same confirm rules for writes and the same refusal shape everywhere.

In scope: debugger processes, exceptions, threads, stack frames, breakpoint detail, find/replace in files, documents, undo
groups, code model, project items, task list, status bar, info bars, activity log, settings read, push events, Roslyn semantic
tools.

Out of scope: changing existing `aidevhelpers` behaviour, the aidevtools MCP server, new packaging.

## 2. Decisions (from the user)

- Roslyn tools are in this plan (phase 6).
- Every write action takes `confirm`. Without it the tool returns `{"ok":true,"result":{"planned":true,"would":{...}}}` and changes
  nothing. This includes breakpoint enable/disable, status, info bar, activity log, save, close and task writes.
- Events are pushed from the bridge to the MCP server in this plan (phase 5), delivered only to the client that subscribed.
- One plan, grouped into phases.
- The VS Code bridge gets the same tools where VS Code has an equivalent API. Otherwise it replies with `unsupported: true`.

## 3. Cross-cutting rules

### 3.1 Dispatch and threading

- Each new VS action is one `case` in `IdeActions.InvokeOnUiThread` (`src/Installers/Vs2026/Core/src/IdeActions.cs`).
  `Invoke` already runs on the UI thread and sets `SuppressUI` (`IdeActions.cs:33-41`).
- Actions that need a break-mode state check it first and return `{"error": ...}` through the existing exception path
  (`RequireBreakMode`, `IdeActions.cs:687`).

### 3.2 Argument helpers

- Add `internal static bool? OptionalBool(JsonElement args, string name)` beside `OptionalString` (`IdeActions.cs:746`).
  Already added in phase 1 unit 1.
- `confirm` is read with `OptionalBool(args, "confirm") == true`. A missing or non-boolean value means plan-only.

### 3.3 Confirm contract

- Plan-only reply: `{"ok":true,"pid":N,"result":{"planned":true,"would":{<what would change>}}}`. Nothing changes.
- Applied reply: `{"ok":true,"pid":N,"result":{"planned":false,"applied":{<what changed>}}}`.
- Enforcement is on the bridge side. The MCP tool forwards `confirm` unchanged, so a direct pipe call cannot skip it.
- Every write action has a fixture for plan-only and applied replies once it is live (see 3.6).

### 3.4 MCP tools

- Each action gets one `[McpServerTool]` method in `src/MCP/Core/src/IdeTools.cs` with `[Description]` on the method and every
  parameter, and the standard routing arguments (`solutionPath`, `ide`, `instancePid`) via `IdeInstanceRouter.CallAsync`.
- Writes add `bool confirm = false`.
- Docs: each tool is listed in `src/Common/src/Resources/mcp-tools.md` and `docs/ide-bridge.md`.

### 3.5 Errors and strings

- Unknown or unsupported arguments throw `ArgumentException` through `RequireString`/`RequireInt`/`OptionalBool`.
- User-facing strings go to `Strings.resx` (`Strings.Format`, `IdeActions.cs:122`). Messages shown to the user must be in the resx.

### 3.6 Conformance and VS Code parity

- `conformance/fixtures.json` gets one fixture per new action:
  - VS-only actions: `<action>_refused_by_generic_bridge`, asserting `ok:false` (the generic bridge refuses VS-only actions).
  - Actions with a VS Code equivalent: a VS Code fixture asserting the same reply shape.
- VS Code: the same action names go into `src/Installers/VsCode/Installer/AiDevTools.VsCode/src/ideActions.ts` (switch,
  `case` per action). Where VS Code has no equivalent, the case returns `{"ok":false,"unsupported":true,"error":...}`
  (`protocol.md:83`). Replies are checked for shape only (`protocol.md:87`).
- Fixtures are shared. The VS Code core tests (`npm run test` under `src/Installers/VsCode/Core/AiDevTools.VsCode.Core`) must
  pass with them; their dependencies are installed first (phase 0 item).

### 3.7 Tests

- Bridge conformance: `dotnet test src/Bridge/tests/AiDevHelpers.Bridge.Tests.csproj -c Release` runs the shared fixtures. It
  passes with 39 of 39 after phase 1 unit 2.
- VS Core: `src/Installers/Vs2026/Core/tests` holds `SmokeTests.cs` only. Pure logic (argument helpers, reply shapes) gets unit
  tests there. Anything that needs EnvDTE runs live.
- Live checks run in Visual Studio through the bridge. Each unit has a live checklist (section 6).

### 3.8 Build

- VS Core and MCP Core build with `dotnet build <csproj> -c Release` (verified in phase 1). The VS installer project needs the
  VSSDK build tools and is built by `scripts/update-vs-extension.ps1`.

## 4. Confirmed and unconfirmed APIs

Confirmed by compiling against `Microsoft.VisualStudio.Interop` 17.14.40260 (the NuGet copy the Core project uses) or by Learn:

| Area | Member (interface) | Status |
|---|---|---|
| Processes | `Debugger.LocalProcesses`, `Process.ProcessID`, `Process.Name`, `Process.Attach()` | compiled, Learn |
| Processes | `Debugger.DetachAll()`, `Debugger2.DetachAll()` | Learn |
| Processes | `Debugger4.DebuggedProcesses` (`EnvDTE90a`) | compiled |
| Threads | `Debugger.CurrentThread`, `Debugger.CurrentProcess`, `Process3.Threads` (`EnvDTE90`), `Thread.ID`, `Thread.Name` | compiled |
| Exceptions | `EnvDTE100.Debugger5.ExceptionGroups` returns `EnvDTE90.ExceptionGroups` (enumerates `ExceptionSettings`, which enumerates `ExceptionSetting`); `ExceptionSetting`: `Name`, `Code`, `BreakWhenThrown`, `BreakWhenUserUnhandled`, `UserDefined`; `ExceptionSettings.SetBreakWhenThrown(bool, ExceptionSetting)`, `SetBreakWhenUserUnhandled(bool, ExceptionSetting)` | reflection on the interop DLL (`ExceptionGroups` and its item types); compile pending |
| Stack frames | `Thread.StackFrames`, `StackFrame.FunctionName`, `StackFrame.ReturnType` | Learn (used in examples), compile pending |
| Breakpoints | `Breakpoint.Condition`, `Breakpoint.Enabled`, `Breakpoints.Add` | `Breakpoints.Add(File, Line)` compiled. Condition overload: pending spike |
| Find | `Find2` (`Target`, `SearchPath`, `FindWhat`, `Execute`), `Find2.FindReplace` | Learn for `Find2` and `FindReplace` remarks. Results path: pending spike |
| Documents | `DTE.Documents`, `Document.Saved`, `Save`, `Close`, `ReadOnly`, `FullName` | pending spike |
| Undo | `DTE.UndoContext.Open(name)`, `Close()` | pending spike |
| Code model | `ProjectItem.FileCodeModel.CodeElements` | Learn |
| Projects | `Project.Properties` (`Item(name)`), `ProjectItems.AddFromFile`, `AddFromNew`, `ProjectItem.Remove()`, `Solution.Close(SaveFirst)` | pending spike for SDK-style names |
| Task list | `SVsTaskList` / `IVsTaskList`, `IVsTaskProvider4` | Learn |
| Status bar | `IVsStatusbar.SetText` | Learn |
| Info bar | `IVsInfoBar`, `IVsInfoBarHost`, `IVsInfoBarUIFactory` | Learn |
| Activity log | `IVsActivityLog` (`SVsActivityLog`) | Learn |
| Settings | `IVsSettingsReader` (`SVsSettingsReader`) | Learn list only; member names pending |
| Events | `SolutionEvents`, `BuildEvents`, `DebuggerEvents`, `DocumentEvents` (`DTE.Events`) | `SolutionEvents` in use (`BridgeHost.cs:27,86-90`). Others pending |
| Roslyn | `VisualStudioWorkspace` via `IComponentModel`, `SymbolFinder`, `Renamer`, `CodeFixProvider` | not confirmed for net48 (spike) |

Spike means: one short throwaway change that compiles a single call against the interop DLL (`dotnet build`), records the result
here, and is reverted if it does not compile. Learn results are not enough on their own.

## 5. Work units

Each unit is one commit on `feature/vs-api-coverage-wt`, with its own checks. A unit is done when its checks pass and its plan
boxes are ticked. Units run in order; a blocked unit does not stop the next unit unless it is a dependency.

### Phase 0: Wiring and verification (done except for the items below)

- [x] MCP tools in `src/MCP/Core/src/IdeTools.cs`; debugger control in `DebuggerControlService.cs`.
- [x] SDK version `Microsoft.VisualStudio.SDK` 18.12.1606 in `Core` and `Installer`.
- [ ] Install the VS Code extension dependencies (`npm ci`) in both VS Code projects so their tests can run.
- [ ] Spikes listed in section 4 as "pending". Each spike is recorded here with its result.
- [ ] Roslyn spike: whether `VisualStudioWorkspace` and `Microsoft.VisualStudio.LanguageServices` can be referenced from net48.
- [ ] Decide `ide_find_all_references` / `ide_go_to_definition` coexistence (recommended: coexist, Roslyn tools named
      `ide_semantic_references` and `ide_semantic_definition`).

### Phase 1: Debugger

Unit 1.1 (done, commit `4a41fbb`): `listProcesses`, `listDebuggedProcesses`, `OptionalBool`, refusal fixtures.

Unit 1.2 (done, commit `e10f11c`): `listThreads` (`Debugger.CurrentThread`, `Process3.Threads`; empty outside break mode).

Unit 1.3 (in progress): `listExceptions`.
- Action `listExceptions`, no args. Reply `{"exceptions":[{group,name,code,break_when_thrown,break_when_user_unhandled,user_defined}]}`.
- Code: `IdeActions.ListExceptions` (enumerates `EnvDTE90.ExceptionSettings` then `ExceptionSetting`).
- MCP: `IdeListExceptions`. Fixture: `list_exceptions_refused_by_generic_bridge`.
- Live check: the list matches the Exceptions window in VS.

Unit 1.4: `setExceptionBreak` (confirm). Args `group` (string), `name` (string), `breakWhenThrown` (bool, optional),
`breakWhenUserUnhandled` (bool, optional).
- Writes through `ExceptionSettings.SetBreakWhenThrown(bool, ExceptionSetting)` and `SetBreakWhenUserUnhandled`.
- Plan-only reply lists the current and proposed values per exception. No write without `confirm`.
- Fixture: refused by generic bridge. Live check: value changes in the VS Exceptions window, then is restored.

Unit 1.5: `listStackFrames` (read-only). No args (current thread, break mode required; otherwise
`{"frames":[]}` with `debugger_mode`). Reply `{"frames":[{index,function,return_type,module}]}`.
- Code: `dte.Debugger.CurrentThread.StackFrames` (compile first; spike if `module` is not available).
- MCP: `IdeListStackFrames`. Fixture: refused by generic bridge.

Unit 1.6: breakpoint detail.
- Extend `setBreakpoint` with optional `condition` (string), `hitCountType` (string: `always|equal|multiple|greaterOrEqual`),
  `hitCount` (int). Confirm is required when `condition` or `hitCount` is set.
- Spike first: the overload of `Breakpoints.Add` that takes a condition (EnvDTE `Breakpoints.Add` takes `Function`, `File`,
  `Line`, `Column`, `Condition`, `ConditionType`, `HitCountTarget`, `HitCountType`, `Language`, `Disabled`? confirm in the
  interop DLL). Record the exact signature here.
- Reply includes the stored `condition` and `hit_count_type` (reads already return `condition`, `IdeActions.cs:543`).

Unit 1.7: `enableBreakpoint` / `disableBreakpoint` (confirm). Args `file`, `line`. Writes `Breakpoint.Enabled`. Plan-only
reply names the breakpoints that would change. Fixture: refused by generic bridge.

Unit 1.8: `attachProcess` / `detachAll` (confirm). `attachProcess` args `pid` (int). Calls `Process.Attach()` on the
`LocalProcesses` item with that pid. `detachAll` calls `Debugger.DetachAll()`. Plan-only replies list the processes that would
change. Fixture: refused by generic bridge. Live check: attach to a throwaway console app, then detach.

Phase 1 checks: `dotnet build` clean; bridge conformance 39+ passing; live checklist per unit (section 6).

### Phase 2: Editor and documents

Unit 2.1: `findInFiles` (read-only). Args `pattern` (string), `matchCase` (bool, default false), `useRegex` (bool, default false),
`scope` (`solution|project|openDocuments`, default `solution`).
- Code: `Find2` with `Target` set from `scope` and `FindWhat` set from `pattern`, executed through `Find2.FindReplace` so the
  global find state is unchanged (Learn). Results: the Find Results window (`FindResults1`) is read after `Execute`.
  Spike the results read path first (`FindResults1` text or `Find2` results properties). Record the chosen path here.
- Reply `{"matches":[{file,line,text}],"truncated":false}` capped at 500 matches.
- Fixture: refused by generic bridge. Live check: a known string in the solution returns the expected line.

Unit 2.2: `replaceInFiles` (confirm). Same args plus `replacement` (string). Plan-only reply gives a count per file and never
writes. Applied reply gives the same counts. Fixture: refused by generic bridge.

Unit 2.3: documents.
- `listDocuments` (read-only): `DTE.Documents` with `fullName`, `saved` (bool), `readOnly`.
- `saveDocument` (confirm) and `closeDocument` (confirm) with `file`. Both plan-only unless `confirm`. `closeDocument` refuses a
  dirty document unless `discardChanges` is also true (also confirmed).
- Fixture: refused by generic bridge.

Unit 2.4: `undoGroup`. Args `name` (string) and `steps` (array of `{file,line,newText}` edits). Opens `DTE.UndoContext`, applies
the edits, and closes the context in a `finally`. Plan-only reply lists the edits. Edits run in one bridge call so the group
never spans calls. Fixture: refused by generic bridge.
- Spike first: `UndoContext.Open` / `Close` signatures in the interop DLL.

Unit 2.5: `listCodeElements` (read-only). Args `file` (string). Walks `ProjectItem.FileCodeModel.CodeElements` (types, members)
and returns `{"elements":[{kind,name,startLine,endLine}]}`. Fixture: refused by generic bridge.

Phase 2 checks: as phase 1.

### Phase 3: Solution and projects

Unit 3.1: `projectProperties` (read-only). Args `project` (string, name) and `names` (array of strings). Returns the named
values from `Project.Properties`. Spike first: confirm names such as `TargetFramework` on SDK-style C# projects; unknown names
return `null`, not an error. Fixture: refused by generic bridge.

Unit 3.2: `addProjectItem` (confirm). Args `project`, `file` (path), `mode` (`fromFile|fromNew`). Plan-only shows the item that
would be added. Fixture: refused.

Unit 3.3: `removeProjectItem` (confirm). Args `project`, `file`. Calls `ProjectItem.Remove()`. The reply says the file stays on
disk. Fixture: refused.

Unit 3.4: `closeSolution` (confirm). Args `saveFirst` (bool, default false). Calls `Solution.Close(saveFirst)`. The bridge's own
`AfterClosing` handler runs during this call, so unit 5.3 (handler unhooking) must be in place first. Fixture: refused.

Phase 3 checks: as phase 1.

### Phase 4: Tool windows, status and settings

Unit 4.1: `listTasks` (read-only) via `IVsTaskList`. Reply `{"tasks":[{category,text,file,line}]}` for tasks in the Task List.
Spike: `SVsTaskList` service access from the package. Fixture: refused.

Unit 4.2: `addTask` / `clearTasks` (confirm). An `IVsTaskProvider4` is registered by `BridgePackage` at initialisation (not in the
action switch). `addTask` args `text`, `file`, `line`, `category` (`Error|Warning|Message`). `clearTasks` removes only tasks this
bridge added (tracked by a bridge-owned category tag). Plan-only replies describe the change. Fixture: refused.

Unit 4.3: `setStatus` (confirm). Args `text` (string, may be empty to clear). Calls `IVsStatusbar.SetText`. Fixture: refused.

Unit 4.4: `showInfoBar` (confirm). Args `text`, `actions` (array of `{label, actionId}`, optional). Builds an `InfoBarModel`
(`Microsoft.VisualStudio.Shell`) and hosts it through `IVsInfoBarHost` from the solution window frame. Action clicks are not
returned in this unit (they need the event channel, phase 5). Fixture: refused.

Unit 4.5: `writeActivityLog` (confirm). Args `level` (`Error|Warning|Information`), `source`, `message`. Calls
`IVsActivityLog.LogEntry`. Fixture: refused.

Unit 4.6: `getSetting` (read-only). Args `key` (string). Reads a VS setting through `IVsSettingsReader` (spike the member names
first). Fixture: refused.

Phase 4 checks: as phase 1. Info-bar and status changes are checked visually in VS.

### Phase 5: Events (push channel)

The current protocol is one request and one reply per connection (`conformance/protocol.md`, Pipe). A push channel is a new
transport.

Unit 5.1: transport design, written into `conformance/protocol.md` before any code.
- A subscription is a `subscribe` request on a connection that stays open. The bridge replies `{"ok":true,"subscriptionId":"..."}`
  then writes event lines `{"event":"<name>","seq":N,"data":{...}}` on that connection.
- `unsubscribe` with the same id, or the connection closing, ends the subscription.
- Heartbeat: the bridge writes `{"event":"heartbeat","seq":N}` every 15 seconds. The MCP side treats 45 seconds of silence as
  disconnected.
- Pid change: the subscription is closed with `{"event":"closed","reason":"pidChanged"}`. The MCP reconnects to the new manifest.
- Watchdog restart: the subscription is closed with `{"event":"closed","reason":"bridgeRestarted"}`. Subscribers resubscribe.
- Event names: `solutionOpened`, `solutionClosing`, `buildBegin`, `buildDone`, `enterBreakMode`, `enterRunMode`,
  `contextChanged`, `documentSaved`, `documentOpened`, `documentClosing`, `dropped` (`data.count`).

Unit 5.2: event sources in the bridge.
- Keep the event objects as fields on `BridgeHost` (currently only `SolutionEvents` at `BridgeHost.cs:27`). Add `BuildEvents`,
  `DebuggerEvents`, `DocumentEvents`. Extend the existing `SolutionEvents` handlers (`BridgeHost.cs:86-90`) rather than adding
  a second subscription.
- Spike the `DebuggerEvents` and `DocumentEvents` member names first.
- Handlers only enqueue an event object. No handler waits on `_runOnMainThread` (`BridgeHost.cs:87` DTE events are already on the
  UI thread; a handler that waits would deadlock while `SuppressUI` is on during `Build()`, `IdeActions.cs:214`).

Unit 5.3: unhooking.
- `BridgeHost.Dispose` (`BridgeHost.cs:54-65`) unhooks every handler. `unsubscribe` unhooks nothing global; it removes that
  subscriber's queue only.
- Dispose also closes every open subscription with `reason: bridgeRestarted`.

Unit 5.4: per-subscriber queue.
- Bounded queue, 500 events per subscriber, oldest dropped. A `dropped` event with `count` is written before the next event.
- A subscriber that stops reading is closed after its queue has been full for 30 seconds.

Unit 5.5: MCP side.
- `ide_subscribe_events` starts a subscription and returns the subscription id. `ide_read_events` returns queued events since
  a sequence number. `ide_unsubscribe_events` ends it. These are the MCP tools; the MCP process holds the connection and
  buffers events.

Unit 5.6: tests.
- Bridge tests: a fake event source feeds the queue; drop, heartbeat and close reasons are asserted.
- Live: subscribe, build a project, expect `buildBegin` then `buildDone`; break in the debugger, expect `enterBreakMode`.

Phase 5 checks: bridge tests; live run above. VS Code parity: `subscribe` is `unsupported` for VS Code in this plan.

### Phase 6: Roslyn semantic tools

Unit 6.1: spike.
- Reference `Microsoft.VisualStudio.LanguageServices` from the net48 Core project. Get `VisualStudioWorkspace` through
  `IComponentModel` (`SComponentModel`). Record the result here. If it does not compile on net48, phase 6 stops here and the
  rest of this phase is blocked until the user decides (the Roslyn tools need a different host).

Unit 6.2: `ide_semantic_references` and `ide_semantic_definition` (read-only): `SymbolFinder.FindReferencesAsync` and
`SymbolFinder.FindDeclarationsAsync` or `ISymbol.Locations`. Args `file`, `line`, `column`. Replies `{"locations":[{file,line,column}]}`.
Existing `findAllReferences` and `goToDefinition` are kept.

Unit 6.3: `ide_rename_symbol` (confirm). Args `file`, `line`, `column`, `newName`. Plan-only reply lists affected files and the
count per file. Applied reply applies the changed documents through the workspace. Live only.

Unit 6.4: `ide_code_fixes` (read-only) and `ide_apply_code_fix` (confirm). `ide_code_fixes` lists `{id,title,file,line}`.
`ide_apply_code_fix` applies one by id; plan-only without `confirm`. Live only.

Unit 6.5: unsaved buffers.
- Roslyn sees the editor buffer. Tool descriptions say so. A live check edits a buffer without saving and confirms the result
  includes the edit.

Phase 6 tests: live only, with a fixture project under `tests/` for rename and references.

## 6. Live checklist (every unit that touches VS)

1. Reinstall the extension: `scripts/update-vs-extension.ps1` with all VS instances closed (one installer run). Check Community
   and Insiders.
2. Open a solution in VS. Call the new tool through the MCP. Check the result matches what the VS UI shows.
3. For writes: call with `confirm` absent (expect `planned:true`, no change), then with `confirm:true` (expect the change).
4. Restore any state changed in step 3.
5. Record the result in the plan (the unit's box) and in the commit message.

The aidevtools MCP is disconnected in this session. Until it is back, the live checklist is run manually and recorded here.

## 7. Verification (whole plan)

- `dotnet build` clean for `src/Installers/Vs2026/Core` and `src/MCP/Core` after every unit.
- `dotnet test src/Bridge/tests/AiDevHelpers.Bridge.Tests.csproj -c Release` passes after every unit that touches fixtures.
- VS Code core and installer tests pass after every unit that touches fixtures (after phase 0 `npm ci`).
- Lite gate (`.claude/reference/lite-gate.md`) before each commit where aidevtools is available; full gate before the PR to `dev`.

## 8. Risks

- Roslyn on net48 may not reference cleanly (unit 6.1). Phase 6 is blocked by it, not the other phases.
- Event handlers that block can deadlock VS. Handlers only enqueue (unit 5.2).
- Attach, detach, rename, replace, close and task writes change state outside the bridge. `confirm` is the only guard.
- `SuppressUI` is on for all of `Invoke`. A long action (rename, replace, build) can hide a prompt. Each long action gets a live
  run before it is enabled.
- The `RunTests` action refuses in some states (`IdeActions.cs:158-163`). New actions use the same refusal shape.
- VS Code parity doubles the fixture work. Each action states its VS Code status in the fixture.
- `System.Text.Json` must match the version VS ships (csproj comment).
- The worktree's git metadata was lost once this session; the worktree was re-cloned from origin. Keep pushing after each unit.

## 9. Open items

None that block phases 1 to 5. Phase 6 depends on unit 6.1. Pending spikes are listed in section 4 and phase 0.
