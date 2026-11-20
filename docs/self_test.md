# IDE Bridge Self-Test

Use this checklist to exercise the standalone `AiDevHelpers.MCP` server and a live IDE bridge. Load the tool schemas before calling tools. If there is no registered IDE instance, record the live checks as unavailable rather than launching or changing an IDE as part of a read-only pass.

## Read-only checks

- `IdeListInstances` — confirm the registered IDE kind, process ID, and solution path.
- `IdeBridgeHealth` — check the listener and request counters; pass `solutionPath` when more than one IDE instance is open.
- `IdeGetOutput` and `IdeSearchOutput` — inspect an existing Build or Debug output pane.
- `IdeGetBuildDiagnostics` — read the current Error List.
- `IdeListConfigurations` — read the solution configurations.
- `IdeGetDebugState`, `IdeListBreakpoints`, and `IdeListWatches` — read the debugger state.
- `IdeGetTestResults` — read the newest TRX result for a supplied solution path.
- `IdeGetLocals` — inspect locals only when the debugger is already stopped at a breakpoint.

Record each result as PASS, FAIL, or NOT RUN with the exact tool arguments and any error message.

## Terminal session checks

- Run `dotnet test tests/AiDevHelpers.Helpers.TerminalControl.Tests/AiDevHelpers.Helpers.TerminalControl.Tests.csproj` for terminal session, pipe, and wrapper coverage.
- Run `dotnet test tests/AiDevHelpers.Bridge.Tests/AiDevHelpers.Bridge.Tests.csproj --filter FullyQualifiedName~TerminalSessionToolsTests` for MCP input validation.
- The real Windows Terminal smoke tests open windows; run them only when an operator intends that action. The LocalSystem interactive path requires the bridge service running as SYSTEM and remains an explicit live check.

Record each result as PASS, FAIL, or NOT RUN and include the exact command and counts.

## Actions requiring a deliberate live check

These tools change IDE or project state. Run them only when the test operator intends the specific action and has a suitable scratch solution:

- `IdeEvaluate` — evaluates an expression in the live debug context and may have side effects.
- `IdeRunTests`, `IdeBuild`, `IdeClean`, and `IdePublish`.
- `IdeSetStartupProject` and `IdeSetConfiguration`.
- `IdeStartDebugging`, `IdeStopDebugging`, `IdeStep`, `IdeSetBreakpoint`, and `IdeRemoveBreakpoint`.
- `IdeFormatDocument` and `IdeCodeCleanup`.
- `IdeLaunchInstance`, `IdeKillInstance`, and `IdeKillProcess`.

Record the selected solution, expected state change, result, and final state. Do not run kill or publish checks against a user's active work.

## VSIX action coverage

The automated MCP tests exercise tool registration/routing and the no-instance failure path. The bridge service tests cover reply parsing and state mapping. They do not execute the VSIX `IdeActions` EnvDTE calls. In particular, `IdeFormatDocument`, `IdeCodeCleanup`, and `IdeRunTests` currently have no automated test that verifies the corresponding Visual Studio command ran; a passing MCP test is not evidence that those IDE commands are available or work in a particular VS installation.

Use a disposable scratch solution for the following integration checks. Open a source file with intentionally unformatted code and an available Code Cleanup profile, then call `IdeFormatDocument` and `IdeCodeCleanup`; verify the file changes as expected and is saved only if the IDE command itself saves it. Add a small test project, call `IdeRunTests`, and verify Test Explorer reports completion. Also exercise `IdeBuild`, `IdeClean`, and `IdeGetBuildDiagnostics` against the scratch solution. For debugger coverage, use a scratch program with a deterministic breakpoint and verify start, break state, locals/evaluation, step, and stop. Record unsupported command names, missing profiles, or unavailable debugger features as explicit failures rather than treating a successful bridge response as proof that the action completed correctly.

Do not run these mutation checks on a user's active solution. Until a VSIX integration harness is added, these checks remain operator-run live coverage; the test project does not mock or automate EnvDTE.

## Launching Visual Studio with a solution argument

There is an automated negative test for a nonexistent solution path, but no automated test that starts Visual Studio and confirms it loaded the requested `.sln`. To perform that live check safely:

1. Choose a disposable `.sln` that is not already open and record its absolute path.
2. Call `IdeLaunchInstance` with that exact path as `solutionPath`. Expect `ok: true`, `already_running: false`, and the same solution path in the result.
3. Call `IdeListInstances`; verify the returned PID and solution path match the launch result. Call `IdeBridgeHealth` with that exact `solutionPath` to confirm the bridge in the newly opened IDE responds.
4. Call `IdeLaunchInstance` again with the same path. Expect `already_running: true` and the same PID, showing that it reuses the instance.
5. Close only the instance created for this check after confirming its documents are saved. Do not close an instance that was already open before the check.

Record the full argument, result, PID, and confirmed loaded solution path. A successful process launch alone does not pass the test; the bridge registry and health response must identify the requested solution. The tool currently invokes Visual Studio with `/edit` followed by the full solution path, so this check also validates that launch behavior in the installed Visual Studio version.
