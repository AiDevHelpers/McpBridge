# Plan: fix the code-review findings (2026-11-24)

Source: the Sonnet review of the non-test C# changed this session (19 files), then a Sonnet review of this plan.
Findings are numbered as in the code review. Scope: the seven high-priority items. Medium and low items are not scheduled.

## Status
- Units 1-7: done in code. Unit 3: a timed-out launch clears its starting marker (decided in chat). Live checks (2026-11-25): unit 4 passed (`ide_launch_instance` on the open solution, by path with other casing and slashes, returned the same instance). Unit 5: `ide_run_tests` accepted, but no finished run was reported, so it is not verified. Later the same day `ide_run_tests` returned `TestExplorerRefusedTwice` with `attempts: 2`, which shows the bridge-side retry ran once before giving up; the refusal itself is not resolved. Unit 6: a launch of a missing solution fails with `Solution file not found` and leaves no entry in `starting`; a successful launch and a repeat launch both work (`already_running`). Unit 2: not checked, no client-side cancel is available here. Unit 1: checked on a scratchpad test repo: `allow-all-apply` wrote a store matching the previewed hash and the user pointer (`.aidevtools/user.json`). The console `allow-all` now refuses (it asked on the console before). The test approval stays in the admin store for that scratch repo. Units 1, 2 and 6: not checked. Extension installed (exit 0).
- Unit 7: committed as the full split (9335f02), not only the helpers.
- Unit 1: store-first order with rollback; no test for the pointer-write failure path yet.

## Units

Each unit: build and test the touched projects, then the lite gate, then one commit on `wip`.

### Unit 1: allow-all write order (finding 5)  DONE
- Signed store first, then the user pointer. The tool layer trusts the pointer, so a pointer must never exist without its store.

### Unit 2: cancellable open wait (finding 2)
- Add a `CancellationToken` parameter to `IdeOpenSolution`. This changes the MCP tool signature, so the tool description and the docs table must be updated too.
- Replace `Thread.Sleep(500)` with `await Task.Delay(500, ct)`.
- `BridgeAnswers` is synchronous. Cancellation takes effect between polls only; say so in the tool description.
- The "cancels the wait" test is moved to unit 6, where the delay is injected.

### Unit 3: starting markers (finding 3)  DECIDED: a timed-out launch clears its starting marker (decided in chat)
- Markers stay as they are on success. On a failed launch or a timeout, clear the marker only for an exit that is not "still starting": a timeout must keep its marker, because `ide_list_instances` reports it as starting.
- Broker launches have no pid. Find the new devenv by snapshotting devenv pids before the launch and taking the one that appears. If none appears, write no marker.
- Decision: keep a timed-out launch listed as starting (current behaviour), or clear it?
- Depends on unit 2 (same loop) and is moved into unit 6's new class.

### Unit 4: shared path comparison (finding 4)  REVISED
- Location: `src/Bridge/src` (namespace `AiDevHelpers.Bridge`), not Common. `VsInstanceRegistry` is there, and `IdeTools` and `IdeInstanceRouter` already reference Bridge.
- Add `VsInstanceRegistry.FindBySolution(path)` and `PathCompare.Same(a, b)` in Bridge. Target net48-compatible code: `IdeActions` is net48.
- `IdeActions` has a `SameFile` helper at line 606 that compares files, not solutions. Leave it as is; only the solution comparison moves.
- Test `PathCompare` in `src/Bridge/tests`. `Vs2026.Core.Tests` has only a smoke test, so `IdeActions` changes are checked live.

### Unit 5: retry without a UI-thread sleep (finding 7)  DECIDED: bridge-side retry
- `IdeActions.RunTests` returns a distinct refusal result instead of sleeping. The bridge caller (`IdeTools.IdeRunTests`) waits 3 seconds on its own thread and calls `runTests` again once.
- Keep the `attempts` field and the `TestExplorerRefusedTwice` message in both `Strings.resx` files.
- The VS Code extension has no retry, so this is Visual Studio only.
- Live check: reinstall with `scripts/update-vs-extension.ps1` (VS closed), then run tests twice.

### Unit 6: split `IdeOpenSolution` (finding 1)
- Add `IdeSolutionOpener` in `src/MCP/Core/src` with an injected launcher, clock and delay. Introduce interfaces for `DevenvLauncher` and for the broker call (`IElevRunner` already exists).
- `IdeTools` keeps its constructor `(options, elevRunner)`: three test files use it.
- Tests: success, timeout, broker path, failed launch clears no marker, cancel during the wait (from unit 2).

### Unit 7: split `SetupCommands` (finding 6)  NEEDS SCOPE DECISION
- Touches `ISetupCommands`, `SetupHost` (client), DI wiring, and the test classes that construct `SetupCommands`: `SetupAllowAllTests`, `SetupAllowScriptTests`, `SetupCliTests`, `SetupVerifyTests`, `InstallToolFilesTests`.
- Each new class re-checks `IsElevated` itself.
- Depends on unit 1 (done).
- Decision: do the split now, or only extract `RequireProject` and `ResolveScript` helpers (finding 6) and leave the class whole?

## Checks
- Unit commits: run the lite gate (`gate_run` in lite mode if available; otherwise the affected test projects), not only `dotnet test`.
- Live checks where the unit changes the extension or the server: units 5 and 6 need the extension reinstall (VS closed) and a server restart.
- Commit and push `wip` after each unit.

## Not scheduled
- Finding 8: split `IdeActions` into handler classes.
- Finding 10: one shared list of skipped folders.
- Finding 11: move the allow-all trust check out of the tool layer. Depends on unit 1 and the decision in unit 7.
- Finding 12: `StartingInstances` N+1 registry scan and untyped results.
- Finding 13: low items.
