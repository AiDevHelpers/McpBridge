# Plan: fix the code-review findings (2026-11-24)

Source: the Sonnet review of the non-test C# changed this session (19 files). Findings are numbered as in that review.
Scope: the seven high-priority items first. Medium and low items are listed at the end and are not scheduled.

## Units

Each unit builds and passes its tests before the next starts. Each unit is one commit on `wip`.

### Unit 1: `AllowAllCore` writes the pointer and the store in a safe order (finding 5)
- Problem: the signed store is written before the user-file pointer. If the pointer write fails, the approval exists with no pointer.
- Change: write the pointer first, then the store. If the store write fails, remove the pointer.
- Split `AllowAllCore` into `PreflightAllowAll` (project, settings hash), `ConfirmAllowAll` (console questions or hash match) and `CommitAllowAll` (pointer, then store, with rollback).
- Files: `src/ScriptHelper/src/SetupCommands.cs`.
- Tests: add tests for a failing pointer write (store not written) and a failing store write (pointer removed). Existing allow-all tests must still pass.

### Unit 2: remove the blocking sleep from the open path (finding 2)
- Problem: `Thread.Sleep(500)` in an async method ignores the cancellation token.
- Change: take a `CancellationToken` in the wait loop and use `await Task.Delay(500, ct)`.
- Files: `src/MCP/Core/src/IdeTools.cs`.
- Tests: the existing open-solution test still passes; add one that cancels the wait.

### Unit 3: clear starting markers on every exit path (finding 3)
- Problem: a failed launch leaves its marker behind; the broker path never writes one.
- Change: clear the marker in a `finally` block. For the broker path, find the new devenv pid in the registry and write the marker with it.
- Files: `src/MCP/Core/src/IdeTools.cs`, `src/Bridge/src/StartingInstances.cs`.
- Tests: a failed launch leaves no marker; a broker launch with a registered pid writes one.

### Unit 4: one path comparison (finding 4)
- Problem: the same solution-path comparison is copied in `IdeTools`, `IdeInstanceRouter` and `IdeActions`, and none of the copies handles a malformed path.
- Change: add `VsInstanceRegistry.FindBySolution(path)` and `PathCompare.Same(a, b)` in Common. Use them in all three places.
- Files: `src/Common/src/...`, `src/MCP/Core/src/IdeTools.cs`, `src/Bridge/src/IdeInstanceRouter.cs`, `src/Installers/Vs2026/Core/src/IdeActions.cs`.
- Tests: a malformed path returns no match instead of throwing.

### Unit 5: move the UI-thread retry off the UI thread (finding 7)
- Problem: `Thread.Sleep(3000)` in `IdeActions.RunTests` freezes Visual Studio during the retry.
- Change: return a retry signal to the bridge caller, which waits on its own thread, then calls `runTests` again. The IDE does not sleep.
- Files: `src/Installers/Vs2026/Core/src/IdeActions.cs`, `src/MCP/Core/src/IdeTools.cs`.
- Tests: the retry is driven from the caller; no sleep in the IDE code.
- Needs the extension reinstall to check live.

### Unit 6: split `IdeOpenSolution` (finding 1)
- Problem: seven jobs in one method, untestable without real processes and a real 60-second wait.
- Change: an `IdeSolutionOpener` service with an injected launcher, clock and delay. `IdeOpenSolution` becomes a single call.
- Depends on units 2 and 3.
- Files: `src/MCP/Core/src/IdeTools.cs`, new `src/MCP/Core/src/IdeSolutionOpener.cs`.
- Tests: opener tests with a fake launcher and fake clock cover the success, timeout and broker paths.

### Unit 7: split `SetupCommands` (finding 6 and finding 7 of the review's structure)
- Problem: four interfaces and eight dependencies in one class; the elevation and "not registered" checks are repeated.
- Change: add `RequireProject(root)` and `ResolveScript(project, rel)`. Then split the class into `RegistrationCommands`, `ApprovalCommands` and `AllowCommands`, each behind its own interface. `SetupCli` routes to them.
- Depends on unit 1.
- Files: `src/ScriptHelper/src/SetupCommands.cs` and new files, `src/ScriptHelper/src/SetupCli.cs`, `src/Common/src/Contracts/ISetupCommands.cs`.
- Tests: existing ScriptHelper tests pass unchanged except for constructor and type names.

## Checks per unit
- `dotnet test` for the touched projects, then the MCP Core and ScriptHelper suites.
- Live checks where the unit changes the extension or the server (units 5, 6): reinstall, then open a solution and run the path.
- Commit and push `wip` after each unit.

## Not scheduled
- Finding 8: split `IdeActions` into handler classes.
- Finding 9 (the UI-thread sleep) is covered by unit 5.
- Finding 10: one shared list of skipped folders.
- Finding 11: move the allow-all trust check out of the tool layer (depends on unit 7).
- Finding 12: `StartingInstances` N+1 scan and untyped results.
- Finding 13: low items (magic strings, duplicated `.aidevtools` setup in `RepoSetup`, `GatePlanner` parse edge cases).

## Decisions for you
1. Scope: all seven units, or units 1 to 3 first?
2. Unit 5 changes how `runTests` retries. Is a bridge-side wait acceptable, or should the retry be dropped?
