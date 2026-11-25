# Visual Studio API coverage: implementation plan, 2026-11-25

Status: revision 2, after the Sonnet review of revision 1. Full specification. Each unit is one commit with a checklist.

## 1. Scope

Add the Visual Studio areas the bridge does not use yet, as `ide_*` MCP tools over the existing bridge protocol. Covers debugger
processes, exceptions, threads, stack frames, breakpoint conditions, find/replace in files, documents, undo groups, code model,
project items, task list, status bar, info bars, activity log, settings read, push events, and Roslyn semantic tools.

Out of scope: existing behaviour of current actions, the aidevtools server, packaging.

## 2. Decisions

- Roslyn tools are in scope (phase 6).
- Every write takes `confirm` (default false). Without it: plan-only reply. The MCP description tells the model to show the plan
  and ask the user before setting `confirm`.
- Events are pushed to the MCP server over a dedicated events pipe (phase 5). The request pipe is not changed.
- Events go only to the client that subscribed.
- Existing actions keep their contracts. `setBreakpoint` stays as it is; conditions use a new action.
- Global services are reached through `ServiceProvider.GetGlobalService`, as `BookmarkActions.cs` and `OutputChannelActions.cs`
  already do. No constructor changes to `IdeActions` or `BridgeHost` are needed for services.
- VS Code gets each tool where it has an equivalent; otherwise `{"ok":false,"unsupported":true,"error":...}`.

## 3. Cross-cutting contracts

### 3.1 Reply shapes

- Read action: `{"ok":true,"pid":N,"result":{...}}`. A read in the wrong state returns a result with the state (for example
  `debugger_mode`, `threads: []`), not an error.
- Write action, plan-only (no `confirm`): `{"ok":true,"pid":N,"result":{"planned":true,"would":{...}}}`. Nothing changes.
- Write action, applied: `{"ok":true,"pid":N,"result":{"planned":false,"applied":{...}}}`.
- Partial failure: `{"ok":false,"pid":N,"error":"...","result":{"planned":false,"applied":{...},"failed":[{...,"error":"..."}]}}`.
  Applied items are listed; each failed item says why. `ok` is false whenever `failed` is non-empty.
- Wrong state for a write (for example not in break mode): `{"ok":false,"pid":N,"error":"..."}`, nothing changed, even without
  `confirm`.
- Argument errors: thrown `ArgumentException` becomes `{"ok":false,"error":...}` (`BridgeServer.cs:165-168`).
- Plan-only validates arguments exactly as the applied path does, so a bad plan never becomes a surprise later.

### 3.2 Confirm enforcement

- Enforced in the bridge. `IdeActions.OptionalBool(args,"confirm") == true` selects the applied path; anything else is plan-only.
- The MCP tool forwards `confirm` unchanged.
- Tool descriptions for every write say: "Without confirm it only returns the plan. Show the plan to the user and get their yes before
  setting confirm to true." (This is the same instruction style as `IdeResetTools.cs`.)

### 3.3 Dispatch and threading

- Each VS action is a `case` in `IdeActions.InvokeOnUiThread` (`IdeActions.cs:80`). `Invoke` already runs on the UI thread with
  `SuppressUI` (`IdeActions.cs:26-45`).
- Break-mode checks: `RequireBreakMode` (`IdeActions.cs:691`).

### 3.4 MCP tools

- One `[McpServerTool]` per action in `src/MCP/Core/src/IdeTools.cs`, with `[Description]` on the method and every parameter, and the
  routing arguments (`solutionPath`, `ide`, `instancePid`) through `IdeInstanceRouter.CallAsync`.
- Writes add `bool confirm = false`.
- Each tool is listed in `src/Common/src/Resources/mcp-tools.md` and `docs/ide-bridge.md`.

### 3.5 Strings

- User-facing text goes to `Strings.resx` via `Strings.Format` (`IdeActions.cs:126`).

### 3.6 Conformance and parity

- `conformance/fixtures.json` gets an optional `"bridges"` field: `["visualstudio"]`, `["vscode"]`, or both (default both).
  Unit 0.2 adds it and changes the VS Code test (`bridgeProtocol.test.ts`) to read it. Before this change the VS Code test compares
  reply keys exactly, so a VS-only fixture would fail there.
- VS-only actions get one fixture: `<action>_refused_by_generic_bridge` with `bridges: ["visualstudio"]`, asserting `ok:false`.
  A VS-only fixture asserts the generic refusal, not the VS contract; the VS contract is tested in the VS Core unit tests and live.
- Actions with a VS Code equivalent get a fixture for each bridge with its own reply keys.
- Every tool in section 6 lists its VS Code status.

### 3.7 Tests

- Bridge conformance: `dotnet test src/Bridge/tests/AiDevHelpers.Bridge.Tests.csproj -c Release` (40 passing at `0d3b894`).
- VS Core unit tests (`src/Installers/Vs2026/Core/tests`): pure logic only. Reply builders (`planned`/`applied`/`failed` shape),
  argument validation, and the confirm switch are factored into static helpers so they can be tested without EnvDTE.
- VS Code: `npm run test` and `npm run lint` for the core project; build, lint and test for the installer project. Requires
  `npm ci` first (unit 0.1).
- Live checks: section 7.

### 3.8 Build

- `dotnet build src/Installers/Vs2026/Core/src/AiDevHelpers.Installers.Vs2026.Core.csproj -c Release` and
  `dotnet build src/MCP/Core/src/AiDevHelpers.MCP.Core.csproj -c Release`. The VS installer project is built by
  `scripts/update-vs-extension.ps1`.

## 4. API status

Compiled against the interop DLL (`Microsoft.VisualStudio.Interop` 17.14.40260, `net472` copy) or checked by reflection on it:

- `Debugger.LocalProcesses`, `Process.ProcessID`, `Process.Name`, `Debugger.CurrentThread`, `Debugger.CurrentProcess`,
  `Thread.ID`, `Thread.Name`, `Process3.Threads`, `Debugger4.DebuggedProcesses`.
- `EnvDTE100.Debugger5.ExceptionGroups` → `EnvDTE90.ExceptionGroups` → `ExceptionSettings` → `ExceptionSetting`
  (`Name`, `Code`, `BreakWhenThrown`, `BreakWhenUserUnhandled`, `UserDefined`). Setters:
  `ExceptionSettings.SetBreakWhenThrown(bool, ExceptionSetting)`, `SetBreakWhenUserUnhandled(bool, ExceptionSetting)`.

Learn only (compile pending): `Process.Attach`, `Debugger.DetachAll`, `ProjectItem.FileCodeModel.CodeElements`, `Thread.StackFrames`,
`StackFrame.FunctionName`, `StackFrame.ReturnType`, `IVsInfoBar`, `IVsInfoBarHost`, `IVsInfoBarUIFactory`, `IVsActivityLog`,
`IVsTaskProvider4`, `SVsTaskList`, `IVsStatusbar`.

Pending spike (not yet checked): `Breakpoints.Add` condition overload; `Find2` results read path (`FindResults1`); `Document`
members; `UndoContext`; `Project.Properties` names on SDK-style C#; `ProjectItem.Remove` behaviour; `IVsSettingsReader` members;
`DebuggerEvents`/`DocumentEvents` members; `VisualStudioWorkspace` on net48.

**Spike rule:** a spike is a throwaway call that compiles against the interop DLL (`dotnet build`), with the result recorded here.
If it does not compile, the unit is blocked and its alternative is recorded. Learn alone does not confirm a member.

## 5. Units

Units run in order. A blocked unit does not stop a later unit unless the later unit depends on it.

### Phase 0: wiring

- [x] MCP tool locations (`IdeTools.cs`, `DebuggerControlService.cs`). SDK 18.12.1606 in both VS csproj.
- [ ] **0.1** `npm ci` in `src/Installers/VsCode/Core/AiDevTools.VsCode.Core` and `src/Installers/VsCode/Installer/AiDevTools.VsCode`.
      Done when both test/lint/build commands run.
- [ ] **0.2** Fixture `bridges` field (section 3.6), plus the VS Code test change. Done when the bridge tests and VS Code tests pass
      with the new field.
- [ ] **0.3** Confirm the coexist decision for `findAllReferences`/`goToDefinition` (recommended: coexist, Roslyn tools
      `ide_semantic_references`, `ide_semantic_definition`). Needs user yes.

### Phase 1: debugger

- [x] **1.1** `listProcesses`, `listDebuggedProcesses`, `OptionalBool`, refusal fixtures (`4a41fbb`).
      Fix: `ide_list_processes` description promises `title`; the code returns only `pid` and `name`. Remove `title` from the
      description (`IdeTools.cs`) in unit 1.1b.
- [x] **1.2** `listThreads` (`e10f11c`). Read in wrong state returns `threads: []` with `debugger_mode`.
- [x] **1.3** `listExceptions` (`0d3b894`). Reply `{exceptions:[{group,name,code,break_when_thrown,break_when_user_unhandled,user_defined}]}`.
- [ ] **1.4** `setExceptionBreak` (confirm). Args: `group` (string), `name` (string), `breakWhenThrown` (bool, optional),
      `breakWhenUserUnhandled` (bool, optional). At least one flag required; neither is an argument error. Not found is a
      per-item `failed` entry (3.1). Plan-only lists current and proposed values per item.
      Spike: exact setter call through `ExceptionSettings`. Live: value changes in VS Exceptions window, then is restored.
- [ ] **1.5** `listStackFrames` (read). No args. Current thread, break mode; otherwise `frames: []` with `debugger_mode`.
      Reply `{frames:[{index,function,return_type}]}`. Module is out of scope unless the spike finds it cheaply.
      Spike: `Thread.StackFrames` compile. Live: frames match the Call Stack window.
- [ ] **1.6** `setBreakpointCondition` (confirm). New action; `setBreakpoint` is unchanged. Args: `file`, `line`, `condition`
      (string, optional), `hitCountType` (`always|equal|multiple|greaterOrEqual`, optional), `hitCount` (int, optional).
      At least one of the three. Plan-only lists the breakpoint and the new values. Breakpoint not found is a `failed` entry.
      Spike: `Breakpoints.Add` condition overload, or `Breakpoint` property setters (`Condition`, `HitCountType`, `HitCountTarget`).
      Reads already return `condition` (`IdeActions.cs:547`); unit 1.6 adds `hit_count_type` to `listBreakpoints`.
- [ ] **1.7** `enableBreakpoint` / `disableBreakpoint` (confirm). Args `file`, `line`. Writes `Breakpoint.Enabled`. Not found
      is a `failed` entry. Plan-only names the breakpoints that would change.
- [ ] **1.8** `attachProcess` (confirm) and `detachAll` (confirm). `attachProcess` args `pid`; calls `Process.Attach()` on the
      `LocalProcesses` item with that pid. Elevation mismatch: `Attach` throws; the reply carries the message, `ok:false`.
      `detachAll` calls `Debugger.DetachAll()`. Live: attach to a throwaway console app, detach, check it keeps running.

Phase 1 done when: each unit's build and conformance tests pass, and its live check is recorded.

### Phase 2: editor and documents

- [ ] **2.1** `findInFiles` (read). Args: `pattern`, `matchCase` (default false), `useRegex` (default false), `scope`
      (`solution|project|openDocuments`), `project` (required when scope is `project`). Reply `{matches:[{file,line,text}],truncated}`,
      capped at 500. Spike first: `Find2` with `Target`/`SearchPath`, and the read path (`FindResults1` text after `Execute`, or
      a `Find2` results property). The spike must show the global find state is not changed (`Find2.FindReplace`, Learn remarks).
      VS Code: `vscode.workspace.findFiles`/search, equivalent for read.
- [ ] **2.2** `replaceInFiles` (confirm). Same args plus `replacement`. Plan-only counts matches per file without writing: it uses
      the same search, so regex and match-case behaviour are identical. Dirty open buffers are listed as `skipped` in plan and
      applied replies. Applied: replace, then report `applied` and `failed` per file. Spike: `Find2.FindReplace` availability.
- [ ] **2.3** Documents. `listDocuments` (read): `{documents:[{fullName,saved,readOnly}]}`. `saveDocument` (confirm) and
      `closeDocument` (confirm), arg `file`. `closeDocument` on a dirty document: `failed` entry unless `discardChanges` is
      also true, which is a second confirmed input (plan-only lists the discard). Spike: `Document.Saved`, `Save`, `Close`,
      `ReadOnly` compile (`FullName` already compiles, `IdeActions.cs:209`).
- [ ] **2.4** `undoGroup` (confirm). Args: `name`, `edits:[{file,startLine,startColumn,endLine,endColumn,newText}]`
      (ranges, not single lines). Opens `DTE.UndoContext`, applies all edits, closes it in `finally`. Partial failure: edits that
      failed are listed; the group still closes. Plan-only validates ranges and lists each edit. Spike: `UndoContext.Open`/`Close`
      compile, and edit application through `TextDocument`/`EditPoint`.
- [ ] **2.5** `listCodeElements` (read). Arg `file`. Walks `ProjectItem.FileCodeModel.CodeElements`, returns
      `{elements:[{kind,name,startLine,endLine}]}`. Spike: `FileCodeModel` access from a `ProjectItem`.

### Phase 3: solution and projects

- [ ] **3.0** Named event handlers and unhooking. Refactor `BridgeHost` so `SolutionEvents` handlers are named fields
      (`BridgeHost.cs:88-89` today are lambdas), and `Dispose` (`BridgeHost.cs:54-65`) unhooks them. This must land before 3.4.
- [ ] **3.1** `projectProperties` (read). Args `project` (name), `names` (array). Returns values from `Project.Properties`; unknown
      names return `null`. Spike: SDK-style C# names (`TargetFramework`, `OutputType`).
- [ ] **3.2** `addProjectItem` (confirm). Args `project`, `file` (path), `mode` (`fromFile|fromNew`), `template` (string, required
      for `fromNew`). Spike: `ProjectItems.AddFromFile`/`AddFromNew` compile. Plan-only shows the target project and path.
- [ ] **3.3** `removeProjectItem` (confirm). Args `project`, `file`. Calls `ProjectItem.Remove()`. The reply says the file stays on
      disk. Spike: confirm `Remove` on an SDK-style item does not delete the file (live check before enabling).
- [ ] **3.4** `closeSolution` (confirm). Arg `saveFirst` (bool, default false). Depends on 3.0. After close the instance has no
      solution, so `solutionPath` routing finds no match; callers must use `instancePid`. With `SuppressUI` on, a dirty document
      with `saveFirst:false` is discarded without a prompt, so plan-only lists dirty documents and applied refuses when any are dirty
      and `saveFirst` is false (`failed` entries).

### Phase 4: tool windows, status, settings

- [ ] **4.1** `listTasks` (read). `SVsTaskList` through `ServiceProvider.GetGlobalService`. Spike: `IVsTaskList` enumeration.
      Reply `{tasks:[{category,text,file,line}]}`.
- [ ] **4.2a** Task provider registration in `BridgePackage` (Installer project). Registers an `IVsTaskProvider4` at package init.
      Not testable in Core unit tests; live only.
- [ ] **4.2b** `addTask` / `clearTasks` (confirm). `addTask` args `text`, `file`, `line`, `category` (`Error|Warning|Message`).
      `clearTasks` removes only bridge-owned tasks (tagged on creation). Plan-only describes the change.
- [ ] **4.3** `setStatus` (confirm). Arg `text` (empty clears). `IVsStatusbar.SetText`. Spike: `SVsStatusbar` via global service.
- [ ] **4.4** `showInfoBar` (confirm). Args `text`, `actions:[{label}]` (labels only; clicks are not returned in this unit).
      Builds an info bar and hosts it through `IVsInfoBarHost` on the solution window frame. Spike: host acquisition (`VSFPROPID_InfoBarHost`
      or `VSSPROPID_MainWindowInfoBarHost`, per Learn).
- [ ] **4.5** `writeActivityLog` (confirm). Args `level` (`Error|Warning|Information`), `source`, `message`. `IVsActivityLog.LogEntry`.
- [ ] **4.6** `getSetting` (read). Arg `key` (VS setting path, documented in the reply schema). Reply `{key,value,found}`. Spike:
      `IVsSettingsReader` member names; unknown key returns `found:false`.

Phase 4 units are checked visually in VS (info bar, status, task list, activity log).

### Phase 5: events over a dedicated pipe

The request pipe is not changed. It stays one-instance, one request, one reply (`BridgeServer.cs:127-134`).

- [ ] **5.1** Protocol text in `conformance/protocol.md` (new section "Events pipe"), before code.
      - Pipe name: `AiDevHelpers.VS.<pid>.events`. Separate from the request pipe.
      - `maxInstances`: 8 (one per MCP client; the MCP limits subscriptions to 8 per bridge).
      - Subscribe: the client connects, writes `{"action":"subscribe","args":{"events":[...]}}` as one line. The bridge replies
        `{"ok":true,"pid":N,"subscriptionId":"..."}` then writes event lines.
      - Event line: `{"event":"<name>","seq":N,"data":{...}}`. `seq` is per subscription, starting at 1.
      - Heartbeat: `{"event":"heartbeat","seq":N}` every 15 s. Client treats 45 s of silence as disconnected.
      - Closing: the bridge writes `{"event":"closed","reason":"..."}` then closes. Reasons: `bridgeStopped` (dispose),
        `slowSubscriber` (3.4 below), `unsubscribed`.
      - Unsubscribe: the client closes its connection. No separate request, because the events connection carries nothing
        else.
      - Watchdog: restarting the events pipe closes existing subscriptions with `bridgeRestarted`; clients resubscribe.
      - Event names: `solutionOpened`, `solutionClosing`, `buildBegin`, `buildDone`, `enterBreakMode`, `enterRunMode`,
        `contextChanged`, `documentSaved`, `documentOpened`, `documentClosing`, `dropped` (`data.count`).
      - Pid change is not a case: the pid is part of the pipe name, so a new pid is a new bridge with new subscriptions.
- [ ] **5.2** `EventServer` class in the bridge (new file). Owns the events pipe and its accept loop, separate from `BridgeServer`.
      Subscribers are tracked in a list; each subscriber has its own queue and writer task.
- [ ] **5.3** Event sources. Named handlers (from 3.0) for `SolutionEvents` (existing). New: `BuildEvents` (`OnBuildBegin`,
      `OnBuildDone`), `DebuggerEvents` (`OnEnterBreakMode`, `OnEnterRunMode`, `OnContextChanged`), `DocumentEvents` (`DocumentSaved`,
      `DocumentOpened`, `DocumentClosing`). Spike: member names on each. Handlers only enqueue an event object and never wait.
      Reason: `Build(WaitForBuildToFinish:true)` runs on the UI thread with `SuppressUI` (`IdeActions.cs:216`), so a waiting handler
      would deadlock; enqueueing lets the build finish. Events fired during build are delivered after it completes.
- [ ] **5.4** Per-subscriber queue: bounded at 500. Overflow drops the oldest and records a count; a `dropped` event with `count` is
      written before the next event.
- [ ] **5.5** Slow subscriber: if a subscriber's write does not complete within 30 s, close that subscriber with
      `reason:"slowSubscriber"`. Other subscribers are unaffected.
- [ ] **5.6** `BridgeHost.Dispose` closes all subscriptions with `bridgeStopped` and unhooks all handlers (3.0 and 5.3).
- [ ] **5.7** MCP side. `ide_subscribe_events(events?)` opens the events pipe for the routed instance and returns a subscription id.
      A background reader keeps the last 500 events in memory. `ide_read_events(subscriptionId, sinceSeq)` returns events with
      `seq > sinceSeq`, plus `closed` if the subscription ended. `ide_unsubscribe_events(subscriptionId)` closes the connection.
      Limit: 8 subscriptions per bridge.
- [ ] **5.8** Tests: bridge unit tests for queue overflow, slow-subscriber close, and heartbeat. Live: subscribe, build a project
      (expect `buildBegin`, `buildDone`), break in the debugger (expect `enterBreakMode`).

Phase 5 is a new transport. Its check is the bridge test suite plus the live run in 5.8. VS Code: `subscribe` is `unsupported`.

### Phase 6: Roslyn

- [ ] **6.1** Spike: `VisualStudioWorkspace` through `IComponentModel`, with `Microsoft.VisualStudio.LanguageServices` referenced
      from the net48 Core project. If it does not compile, phase 6 is blocked and the user decides the host (the Roslyn tools need
      a different host). Phases 1 to 5 proceed.
- [ ] **6.2** `ide_semantic_references`, `ide_semantic_definition` (read). `SymbolFinder.FindReferencesAsync`,
      `SymbolFinder.FindDeclarationsAsync`/`ISymbol.Locations`. Args `file`, `line`, `column`. Reply `{locations:[{file,line,column}]}`.
      Existing `findAllReferences` and `goToDefinition` are unchanged.
- [ ] **6.3** `ide_rename_symbol` (confirm). Args `file`, `line`, `column`, `newName`. Plan-only lists affected files and counts.
      Applied applies changed documents through the workspace; per-file `failed` entries as 3.1. Live only.
- [ ] **6.4** `ide_code_fixes` (read) and `ide_apply_code_fix` (confirm, arg `id`). Live only.
- [ ] **6.5** Unsaved buffers: tool descriptions say results include unsaved edits. Live: edit a buffer without saving, confirm the
      result includes the edit.

## 6. Tools at a glance

| Tool | Action | Write | VS Code |
|---|---|---|---|
| ide_list_processes | listProcesses | no | unsupported |
| ide_list_debugged_processes | listDebuggedProcesses | no | unsupported |
| ide_list_threads | listThreads | no | unsupported |
| ide_list_exceptions | listExceptions | no | unsupported |
| ide_set_exception_break | setExceptionBreak | yes | unsupported |
| ide_list_stack_frames | listStackFrames | no | unsupported |
| ide_set_breakpoint_condition | setBreakpointCondition | yes | unsupported |
| ide_enable_breakpoint / ide_disable_breakpoint | enableBreakpoint / disableBreakpoint | yes | equivalent (VS Code breakpoints API) |
| ide_attach_process / ide_detach_all | attachProcess / detachAll | yes | unsupported |
| ide_find_in_files | findInFiles | no | equivalent |
| ide_replace_in_files | replaceInFiles | yes | equivalent |
| ide_list_documents | listDocuments | no | equivalent |
| ide_save_document / ide_close_document | saveDocument / closeDocument | yes | equivalent |
| ide_undo_group | undoGroup | yes | unsupported |
| ide_list_code_elements | listCodeElements | no | unsupported (VS Code symbols differ) |
| ide_project_properties | projectProperties | no | unsupported |
| ide_add_project_item / ide_remove_project_item | addProjectItem / removeProjectItem | yes | equivalent |
| ide_close_solution | closeSolution | yes | unsupported |
| ide_list_tasks | listTasks | no | unsupported |
| ide_add_task / ide_clear_tasks | addTask / clearTasks | yes | unsupported |
| ide_set_status | setStatus | yes | equivalent (status bar) |
| ide_show_info_bar | showInfoBar | yes | equivalent (message) |
| ide_write_activity_log | writeActivityLog | yes | unsupported |
| ide_get_setting | getSetting | no | unsupported |
| ide_subscribe_events / ide_read_events / ide_unsubscribe_events | (events pipe) | no | unsupported |
| ide_semantic_references / ide_semantic_definition | (Roslyn) | no | unsupported |
| ide_rename_symbol / ide_code_fixes / ide_apply_code_fix | (Roslyn) | yes (rename, apply) | unsupported |

"equivalent" means VS Code has an API for the same operation; the VS Code case is implemented in `ideActions.ts` in the same unit.

## 7. Live checklist (every unit that touches VS)

1. Close all VS instances, reinstall with `scripts/update-vs-extension.ps1` (one installer run). Check Community and Insiders.
2. Open a solution. Call the tool through the MCP. Check the result matches the VS window it describes.
3. For writes: call without `confirm` (expect `planned:true`, no change), then with `confirm:true` (expect the change).
4. Restore any state changed.
5. Record the result in the unit's checkbox and the commit message.

The aidevtools MCP is disconnected in this session. Until it is back, the live checks are run by hand and recorded here.

## 8. Verification

- `dotnet build` clean for `src/Installers/Vs2026/Core` and `src/MCP/Core` after every unit.
- Bridge conformance tests pass after every unit that touches fixtures.
- VS Code core and installer tests pass after every unit that touches fixtures (after 0.1).
- Lite gate before each commit where aidevtools is available. Full gate before the PR to `dev`.

## 9. Risks

- Roslyn on net48 may not reference cleanly (6.1). Blocks phase 6 only.
- Events pipe: a subscriber that never reads holds a writer. Covered by 5.5. A slow VS handler can delay events; handlers only enqueue (5.3).
- `SuppressUI` is on for all of `Invoke`. Rename, replace, close and build run without prompts, so a dirty document can be discarded
  silently. Closing and replacing refuse dirty state unless the caller says so (2.2, 3.4).
- `Find2` results land in the shared Find Results window and change user-visible state (2.1).
- Attach and detach act on other processes. Elevation mismatch fails with an error (1.8).
- Destructive or state-changing writes depend on the model asking the user first (3.2). The bridge cannot tell a user's yes from a
  model's guess. The `confirm` flag is a guard against accidents, not a security boundary.
- `System.Text.Json` must match the version VS ships (csproj comment).
- The worktree's git metadata was lost once this session. Push after each unit.

## 10. Open items

- 0.3: coexist decision for the existing reference and definition actions. Needs the user's yes.
- 6.1: Roslyn host. Decides whether phase 6 runs.
- Pending spikes in section 4 are recorded as they are checked.
