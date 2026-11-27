# Implementation plan: tray unit 6, IDE Instances page

Unit 6 of `docs/plans/2026-11-26-tray-service.md`. Branch from `wip`; PR into `wip`.

## Status (2026-11-27, after PR #129 merged to wip, merge commit f1e594e)

Built: 6.1 (Bridge seam and `InstanceFeed`), 6.2, 6.3, 6.4, 6.5, 6.6 (tab, not yet verified on screen), 6.7 (client and `OpenFileTracker`, not wired), 6.8 (CHANGELOG and features). Tests: Tray 48, Bridge 105, MCP Core 403, passing.

As built, where it differs from the steps below:
- 6.1: `InstanceFeed.Watch(read, interval, scheduler)` is a static function, not an `IInstanceSource`. Production reads on `TaskPoolScheduler.Default`.
- 6.2: `InstanceValidator.Problem(VsInstance) -> string?` (null when valid), not `Check -> Result`. Refusals are traced with `Trace.TraceWarning`.
- 6.4: `InstanceProcessFacts.StartedAt` and `OwnerOf` (WMI via `System.Management`) are passed into `InstanceRowBuilder` as functions; no `IProcessInfo` interface.
- 6.5: the view model takes the rows stream and a scheduler as written.
- 6.7: `InstanceEventsClient.RunOnceAsync` and `RunAsync` are static; `RunAsync` backs off 2 s to 30 s. Event field names checked: Visual Studio sends `{file}` for `documentOpened`/`documentClosing` (`BridgeHost.cs`), VS Code sends `{path}` (`events.ts`). Both are read.
- The IDE Instances tab is a `TabControl` page in `StatusWindow`, read-only.

Remaining for unit 6:
1. 6.7 wiring: one events subscription per live instance, started and stopped with the instance list, feeding `OpenFileTracker` into a row column. Needs a test with the fake pipe server for the wiring.
2. 6.6 manual checklist (5 items) with screenshots, and a UIA name check on each column, high contrast, and per-monitor DPI, per the cross-cutting rules in `tray-service.md`.
3. 6.9 close and reset: held. Its safety review is not done.
4. Lite gate: not run. Its IDE steps need an open Visual Studio on `AiDevHelpers.slnx`; run them before the PR is merged.
5. Docs: the tray-service cross-cutting rules also ask for a README line and a supportability entry. Add the instances list to the diagnostics bundle (pid, IDE, solution path, version of the bridge) when the bundle lands (unit 9).
6. `docs/ide-bridge.md` and `conformance/`: no protocol change; the events protocol is used as written. Confirmed in this unit.

## Goal

A page in the tray that lists the running IDE instances (Visual Studio and VS Code), live: IDE, repo, branch, solution or workspace path, start time. The open file is a separate slice (6.7) that needs a decision first.

## Facts checked against wip (2026-11-27)

- Manifests are written by each instance to `%ProgramData%\AiDevHelpers\vs-instances\<pid>.json` (`InstanceRegistry`, `Bridge`). `VsInstanceRegistry.ListRunning()` (`src/Bridge/src/VsInstanceRegistry.cs`) reads them, deletes the manifests of exited processes, and parses with `Parse`. `VsInstance` fields are all required: `Pid`, `PipeName`, `SolutionPath`, `UpdatedUtc`, `Ide`, `LogRoots`.
- `Parse` checks JSON shape and required fields only. It does not check the pid, the pipe name, or the path.
- `UpdatedUtc` is the last write of the manifest, not the start time.
- The directory is fixed to `%ProgramData%`, and `IsRunning` calls `Process.GetProcessById` directly. Neither can be faked, so the tests need a seam.
- `GitWorktree.RootFor(solutionPath)` and `GitWorktree.BranchFor(solutionPath)` (`src/Bridge/src/GitWorktree.cs`) read the repo and branch from files, with no git process. A detached HEAD gives no branch.
- The events protocol (`conformance/protocol.md`, "Events pipe") gives the pipe names `AiDevHelpers.VS.<pid>.events` and `AiDevHelpers.VsCode.<pid>.events`, one subscribe line, `seq` per subscription, heartbeat every 15 s, and a `closed` line with a reason.
- The tray already has the view-model pattern from unit 5 (`StatusViewModel`: streams in, `ObserveOn(scheduler)`, `IDisposable`). Unit 6 follows it.
- The Rx and MVVM rules in the plan apply: `IScheduler` injected, `Replay(1).RefCount()`, no `OnError` for disconnects, all subscriptions disposed.

## Decisions taken in this plan

1. **Reuse `Bridge`.** The tray references `AiDevHelpers.Bridge` (it targets `net10.0-windows`) and does not add a second manifest reader.
2. **Add a seam in `Bridge`, not in the tray.** `VsInstanceRegistry.ListRunning` gets an overload that takes the directory and the process check. The parameterless call keeps its current behaviour. MCP code is unchanged.
3. **Validate before display.** Every manifest passes `InstanceValidator` (6.2) before the page sees it. A rejected manifest is logged with its reason and not shown.
4. **Read only.** The page does not close, kill or reset instances.

## Steps

Each step names its failing test first, records the red run, then the green run. Lite gate before each commit.

### 6.1 Instance source with a seam

- Add `VsInstanceRegistry.ListRunning(string directory, Func<int, bool> isRunning)`. The existing `ListRunning()` calls it with the real values.
- Add `IInstanceSource` in the tray: `IObservable<IReadOnlyList<VsInstance>> Watch(TimeSpan interval, IScheduler scheduler)`, `Replay(1).RefCount()`, emits on change only. Production implementation wraps the Bridge call on a background scheduler.
- Red tests (`src/Bridge/tests`): a temp directory with one live manifest, one for a dead pid, one malformed, one missing a field. Expected: only the live one is returned, and the dead one is deleted.
- Red tests (`src/Tray/Tray/tests`): `Watch` with `TestScheduler` emits again only when the list changes.
- Done: both pass; the existing Bridge tests still pass.

### 6.2 Untrusted-input validation

- `InstanceValidator.Check(VsInstance) -> Result` with a reason on failure. Rules:
  - `Pid` > 0.
  - `Ide` is `VisualStudio` or `VsCode`.
  - `PipeName` is exactly `AiDevHelpers.VS.<pid>` for Visual Studio, or `AiDevHelpers.VsCode.<pid>` for VS Code, with the same pid.
  - `SolutionPath`, when set, is an absolute path with no control characters, at most 1024 characters.
  - `LogRoots` is never displayed.
- Red tests: pid 0; pipe name for another pid; `Ide` = `Other`; path with a control character; path too long; a valid manifest passes.
- Done: all pass. A rejected manifest is logged with its reason.

### 6.3 Repo and branch

- `InstanceRowBuilder.Build(VsInstance) -> InstanceRow`, using `GitWorktree.RootFor` and `BranchFor`. Repo is the last folder of the root. For VS Code, `SolutionPath` is the workspace folder, so the same code applies.
- Red tests: a solution inside a git checkout gives repo and branch; outside git gives "not a repo"; a detached HEAD gives "detached".
- Done: all pass.

### 6.4 Start time

- `IProcessInfo.StartTime(int pid) -> DateTime?`. Production uses `Process.GetProcessById(pid).StartTime`. Returns null when the process has gone; that row is dropped.
- Red test with a fake: a pid that is gone gives null, and the row is removed from the list.
- Done: passes.

### 6.5 Instances view model

- `InstancesViewModel(IObservable<IReadOnlyList<InstanceRow>> rows, IScheduler scheduler)`: `ObservableObject`, `Replay(1)`, an `ObservableCollection<InstanceRow>`, a count, and an empty-state message. Disposes its subscription.
- Red tests with `TestScheduler`: the collection follows the source; after `Dispose` no further changes arrive; an empty list shows the empty-state message.
- Done: passes.

### 6.6 Page

- A second page in the tray window next to Status (navigation in the existing window). DataGrid columns: IDE, Repo, Branch, Path, Started, PID. Live from 6.5.
- All UI strings in `Strings.resx`; run `string_scan`, `resx_check` and `localization_sync_check`.
- Manual checklist (UI, no automated done-state), with screenshots:
  1. Open a VS solution and a VS Code folder; both rows appear within 5 s.
  2. Switch branch in one repo; its branch updates within 5 s.
  3. Close one window; its row disappears.
  4. Dark and light theme, and 150% DPI: columns readable.
  5. Keyboard only: the grid is reachable and each column has a name for UI Automation.

### 6.7 Live file path

- `InstanceEventsClient(pipeName, scheduler)`: connects to `<pipe>.events`, writes the subscribe line for `documentOpened` and `documentClosing` only, reads lines, tracks `seq`, treats 45 s of silence as disconnected, and reconnects with backoff. A `closed` line ends the subscription; it is not an error.
- The last opened file is shown per instance, and "unknown" until the first event. A `documentClosing` for the same file clears it.
- Red tests: a fake named-pipe server (as `ApprovalPipeHostTests` does) sends subscribe reply, events, heartbeat, `dropped`, and `closed`; the client's state matches each.
- Confirm the event data field name against `conformance/protocol.md` before coding.

### 6.8 Docs and changelog

- `docs/features.md` (tray section), `CHANGELOG.md`. The conformance doc is not changed, since the events protocol is used as it is.

## Done for unit 6

- 6.1 to 6.7 green, and 6.9 green after its safety review.
- Lite gate passes; the full Bridge, MCP Core and tray test suites pass.
- Manual checklist (6.6) run with the screenshots attached to the PR.
- PR into `wip`, merged without asking, per the cross-cutting rules.

## Decisions (user, 2026-11-27)

1. **Live file path (6.7) is in unit 6.** It is part of this unit's PR set, not a follow-up.
2. **All users' instances.** The page lists every instance on the machine, with a user column, since a second signed-in user's VS can still be running.
3. **Close and reset are allowed** (6.9). This is a change to the read-only default: it needs the safety review in 6.9 before any code.

## 6.9 Close and reset from the page (needs a safety review first)

- Row actions: close, reset. Kill is not offered on the page.
- Safety review, done before 6.9 is coded; the review's outcome goes into this file:
  - Unsaved work. Close and reset must not discard unsaved documents. Check the instance for unsaved documents first and refuse, naming the files, unless the person confirms a discard.
  - Confirmation. Each action asks for a yes in the tray dialog, with the instance named (IDE, repo, pid). Deny is the default, as in the approval dialog.
  - Which path runs the action. Reuse the bridge actions the MCP tools use (`ide_close_solution`, `ide_reset_instance` semantics) through `IdeInstanceRouter`, so the tray does not add a second way to act on an IDE. Check the MCP side's [DangerousTool] rules still apply to the tray's call.
  - Running builds and debug sessions. Refuse while a build or debug session is running; show why.
- Red tests: refusal with unsaved documents; refusal while debugging; a deny leaves the instance untouched; confirm calls the bridge with the pid of the row.
- Done: tests pass; the manual checklist covers close and reset on a real VS and VS Code window with unsaved work.

## Not in unit 6

- Killing an instance from the tray (MCP `ide_kill_instance` only).
- Config page (unit 7) and Services page (unit 8).
