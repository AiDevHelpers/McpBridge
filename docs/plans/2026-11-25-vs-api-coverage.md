# Visual Studio API coverage: plan, 2026-11-25

Status: draft, not approved. Revised after the Sonnet review (same day). Source: the gap list from the Visual Studio API review,
checked against `src/Installers/Vs2026/Core/src/IdeActions.cs`, `BridgeHost.cs`, `BookmarkActions.cs`, `src/MCP/Core/src/IdeTools.cs`,
and Microsoft Learn. Learn confirmed `Find2`, `Debugger4/5.DebuggedProcesses`, `IVsStatusbar`, and `SVsTaskList`. Other member
names are from memory and must be checked against Learn in phase 0 before each one is built.

## Decisions (from the user)

- Roslyn tools are in this plan (phase 6).
- Every write action takes a `confirm` flag. Without it the tool returns `{ planned: true, ... }` and changes nothing. This
  applies to every write in this plan, including breakpoint enable/disable and status or info-bar updates.
- Events are pushed from the bridge to the MCP server in this plan (phase 5), delivered only to the client that subscribed.
- One plan, grouped into phases.
- The VS Code bridge gets the same tools where VS Code has an equivalent API.

## Cross-cutting rules for every new action

- One `case` in `IdeActions.Invoke` (UI thread; `Invoke` already sets `SuppressUI`, `IdeActions.cs:33-41`).
- A matching MCP tool in `src/MCP/Core/src/IdeTools.cs` (`[McpServerToolType]` class, `:16`), with a description and argument schema.
  Tool docs go in `src/Common/src/Resources/mcp-tools.md` and `docs/ide-bridge.md`.
- A bool reader for `confirm`: add `OptionalBool(JsonElement, string)` beside `OptionalString`/`OptionalInt` (`IdeActions.cs:744-772`).
  Enforcement is on the bridge side, so a direct bridge call cannot skip it. The MCP tool passes `confirm` through unchanged.
- Conformance: VS-only actions are refused by the generic bridge, so each new action gets a fixture asserting that refusal
  (`fixtures.json` "refused_by_generic_bridge"). VS Code gets its own fixture for the same action, with `unsupported: true`
  where it has no equivalent (`protocol.md:83`). Replies are checked for shape only (`protocol.md:87`).
- Unit tests in `src/Installers/Vs2026/Core/tests` (currently only `SmokeTests.cs`) for pure logic. Live checks through
  `ide_run_tests` and the bridge for everything that needs a running IDE.
- User-facing strings go to `Strings.resx` (existing mechanism, `IdeActions.cs:122`).

## Phase 0: Wiring and verification (no code)

Results (2026-11-25, checked in the clone at `ac3e624`; Learn via search):

- MCP side: `IdeTools.cs` (`[McpServerToolType]` at :16) holds the `ide_*` tools; `DebuggerControlService.cs` handles debugger
  control. Confirmed.
- VS SDK package: `Microsoft.VisualStudio.SDK` 18.12.1606 in both `Core` and `Installer` csproj (the review's 18.11.1036 was stale).
- Confirmed on Learn: `Debugger.DetachAll` and `Debugger2.DetachAll`; `Process.Attach`; `Debugger` `LocalProcesses`;
  `CodeElements` via `ProjectItem.FileCodeModel.CodeElements`; `IVsInfoBar`, `IVsInfoBarHost`, `IVsInfoBarUIFactory`;
  `IVsActivityLog` (`SVsActivityLog`); `IVsTaskProvider4`; `SVsTaskList`; `IVsStatusbar`.
- Not confirmed (search found nothing; check in a phase 6 or phase 1 spike before relying on them): `Breakpoints.Add` condition
  parameters; `Find2.FindReplace` state effects; `Process.Threads`; `Debugger2.CurrentThread`; `DTE.UndoContext`;
  `IVsSettingsReader` (the review's `ISettingsManager` is not used); `Project.Properties` names on SDK-style C#.
- Roslyn: `Microsoft.VisualStudio.Workspace` (`IWorkspace`, the Open Folder API) is a different API from the Roslyn
  `VisualStudioWorkspace`. Learn did not confirm `VisualStudioWorkspace` or `Microsoft.VisualStudio.LanguageServices` for net48.
  This is a phase 6 spike.
- Existing `findAllReferences`/`goToDefinition` (`IdeActions.cs:101-102`): recommendation is coexist, with the Roslyn tools named
  `ide_semantic_references` and `ide_semantic_definition`. Needs user sign-off.
- [x] Confirm the MCP side: `src/MCP/Core/src/IdeTools.cs` for `ide_*` tools; `DebuggerControlService.cs` for breakpoints; the
      debugger contract in `docs/Host-MCP/debugger-control-contract.md`. Update the phase file lists to match.
- [ ] Check every member name in phases 1 to 6 against Microsoft Learn. Record the exact interface for each (EnvDTE, EnvDTE80,
      EnvDTE90a, EnvDTE100, or VS SDK). Include the review's doubts: `Find2.FindReplace` state effects, `Process.Threads`,
      `IVsSettingsReader` (not `ISettingsManager`), `Project.Properties` names on SDK-style C# projects.
- [ ] Confirm the VSSDK package version and that the net48 bridge can reference `Microsoft.VisualStudio.LanguageServices`
      (for `VisualStudioWorkspace`).
- [ ] Check `ide_find_all_references` and `ide_go_to_definition` (existing actions `findAllReferences`/`goToDefinition`,
      `IdeActions.cs:101-102`) against phase 6. Decide replace or coexist (see phase 6).

## Phase 1: Debugger

- [ ] `ide_list_processes`: `Debugger.LocalProcesses` (name, PID, title). Read-only.
- [ ] `ide_attach_process` (confirm): attach to a chosen process from `LocalProcesses`. `ide_detach_all` (confirm): `Debugger.DetachAll()`.
- [ ] `ide_list_debugged_processes`: `Debugger4.DebuggedProcesses`. Read-only.
- [ ] `ide_list_exceptions` / `ide_set_exception_break` (confirm): `Debugger.ExceptionGroups` and break-on-throw per type.
- [ ] `ide_list_threads` (`Process.Threads`, plus `Debugger2.CurrentThread`) and `ide_list_stack_frames` for the current thread.
- [ ] Extend `ide_set_breakpoint` (`IdeActions.cs:513-517` currently calls `Breakpoints.Add(File, Line)` only) with `condition`,
      `hitCountType`, `hitCount` (confirm when a condition is set). `ListBreakpoints` already returns `condition` and `enabled`
      (`IdeActions.cs:543`), so reads need no change.
- [ ] `ide_enable_breakpoint` / `ide_disable_breakpoint` (confirm): set `Enabled`.
- [ ] Tests: fixture refusal per tool. Debugger writes keep the `RequireBreakMode` check where break mode matters.

## Phase 2: Editor and documents

- [ ] `ide_find_in_files`: `Find2` with `Target` set to the solution, `SearchPath`, `FindWhat`, case and regex flags, through
      `Find2.FindReplace` where possible. Results come from the Find Results window text (`FindResults1`), since `Find2` has no
      results collection. Return matches as file, line, text. Verify the exact read path in phase 0.
- [ ] `ide_replace_in_files` (confirm): same find with a replacement. Returns a per-file count before it applies.
- [ ] `ide_list_documents` (read-only), `ide_save_document` (confirm), `ide_close_document` (confirm): `DTE.Documents` with
      `Saved`, `Save`, `Close`, `FullName`, `ReadOnly`. Both write tools confirm whether or not the document is dirty.
- [ ] `ide_undo_group`: `DTE.UndoContext.Open(name)` and `Close()` around a single bridge action's edits. It cannot span
      several bridge calls, so the tool wraps one batch, passed in as a single request, and closes the group before it returns.
- [ ] `ide_list_code_elements`: `FileCodeModel.CodeElements` (types, members, line ranges) for a file. Read-only. Non-Roslyn fallback
      for lookups.

## Phase 3: Solution and projects

- [ ] `ide_project_properties`: `Project.Properties` by name. Read-only. Names are checked in phase 0 for SDK-style projects.
- [ ] `ide_add_project_item` (confirm): `ProjectItems.AddFromFile` or `AddFromNew`.
- [ ] `ide_remove_project_item` (confirm): `ProjectItem.Remove()`. Removes from the project, not from disk. Say so in the result.
- [ ] `ide_close_solution` (confirm): `Solution.Close(SaveFirst)`. Handled with the event-unhook rules in phase 5, since
      the bridge's own `AfterClosing` handler runs during close.

## Phase 4: Tool windows, status and settings

- [ ] `ide_list_tasks` (read-only): Task List through `SVsTaskList`/`IVsTaskList`, or `ToolWindows.TaskList` (phase 0 decides).
- [ ] `ide_add_task` / `ide_clear_tasks` (confirm): add tasks through an `IVsTaskProvider` registered by the package at
      initialisation (not only a `case`). Clear only tasks this bridge added.
- [ ] `ide_set_status` (confirm): `IVsStatusbar.SetText` and progress. Used to show bridge activity.
- [ ] `ide_info_bar` (confirm): `IVsInfoBar` through `IVsInfoBarHost` and `IVsInfoBarUIFactory` (not only the solution window).
      Used for a stale-VSIX warning.
- [ ] `ide_activity_log` (confirm): `IVsActivityLog` writes for bridge errors.
- [ ] `ide_get_setting` (read-only): `IVsSettingsReader` read of a VS setting. Replaces parsing the instance settings JSON for the
      values the bridge needs.

## Phase 5: Events (push channel)

The current protocol is one request and one reply per connection (`conformance/protocol.md:11-18`). Push events need a
long-lived channel, which is a new transport, not just a new message type.

- [ ] Transport design (first task): a subscription connection that stays open. Define the message framing, heartbeats,
      and what happens on disconnect, on a pid change, and on a watchdog restart (`BridgeHost.cs:97-119`). Write it into
      `conformance/protocol.md` before any code.
- [ ] Extend the existing `SolutionEvents` subscription (`BridgeHost.cs:27,86-90`, Opened and AfterClosing) rather than adding a
      second one. Keep the event objects as fields: `SolutionEvents`, `BuildEvents` (OnBuildBegin, OnBuildDone),
      `DebuggerEvents` (OnEnterBreakMode, OnEnterRunMode, OnContextChanged), `DocumentEvents` (Saved, Opened, Closing).
- [ ] Unhook every handler in `BridgeHost.Dispose` (`:54-65`, which unhooks nothing today) and on an `unsubscribe` request.
- [ ] Subscribe per client: one queue per subscribed client, delivered only to that client.
- [ ] Buffer: a bounded queue per subscriber (for example 500 events, oldest dropped), with the drop count reported.
- [ ] Handlers only enqueue. DTE events already arrive on the UI thread (`BridgeHost.cs:87`), so a handler must never block on
      `_runOnMainThread`. Events can fire during an action: `OnBuildDone` fires during `Build()` with `WaitForBuildToFinish`
      (`IdeActions.cs:214`) while `SuppressUI` is on. The queue absorbs that; nothing waits.
- [ ] Tests: a fake event source in the bridge tests; a live run that builds a project and checks the events arrive.

## Phase 6: Roslyn (semantic) tools

- [ ] Get `VisualStudioWorkspace` through `IComponentModel` (MEF) inside the VS process. Confirm the assembly reference works
      from net48 (phase 0).
- [ ] Replace or coexist with the existing `findAllReferences` and `goToDefinition` actions (phase 0 decides). If
      coexist, name the Roslyn tools `ide_semantic_references` and `ide_semantic_definition` so they don't collide.
      The existing ones return IDE state only, not data, so they stay as-is.
- [ ] `ide_find_references(file, line, column)`: `SymbolFinder.FindReferencesAsync`. Read-only.
- [ ] `ide_go_to_definition(file, line, column)`: `SymbolFinder.FindDeclarationsAsync` or `ISymbol.Locations`. Read-only.
- [ ] `ide_rename_symbol(file, line, column, newName, confirm)`: `Renamer.RenameSymbolAsync` returns a new `Solution`. Without
      `confirm` the tool lists affected files. With it, apply the changed documents through the workspace.
- [ ] `ide_code_fixes(file, line, column)` lists fixes; `ide_apply_code_fix(id, confirm)` applies one.
- [ ] Unsaved buffers: Roslyn sees the editor buffer, so results include unsaved changes. Document this in the tool description.
- [ ] Tests: live only (needs a loaded solution). A fixture project under `tests/` for rename and references.

## Verification (whole plan)

- [ ] Lite gate per commit (`.claude/reference/lite-gate.md`). Full gate before the PR to `dev`.
- [ ] Reinstall the extension with `scripts/update-vs-extension.ps1` (one installer run, all VS instances closed) and check
      both Community and Insiders.
- [ ] Each phase ends with a live run in the bridge: list, then one write with `confirm` absent (plan only), then with
      `confirm: true`. Phase 6 tests are live-only, so its checks run in the same live pass.

## Risks

- Roslyn assembly versions may not match the VS 18 runtime from net48. Phase 0 decides this before phase 6 starts.
- Event handlers that block can deadlock VS. Handlers only enqueue (phase 5).
- Attach, rename and replace change state outside the bridge; the `confirm` flag is the only guard.
- `SuppressUI` is on for the whole of `Invoke`. A long action (rename, replace, build) can hide a prompt that needs an answer.
  Check each long action for modal prompts in a live run before enabling it.
- The `RunTests` action refuses rather than runs in some states (`IdeActions.cs:158-163`). New actions follow the same refusal
  shape so callers see one pattern.
- VS Code parity doubles the fixture work; each action lists its VS Code status in the fixture (equivalent or `unsupported`).
- `System.Text.Json` must stay on the version VS ships (csproj comment).

## Open questions

None. Decisions above cover the review findings. Phase 0 answers the remaining API-name and location checks before code starts.
