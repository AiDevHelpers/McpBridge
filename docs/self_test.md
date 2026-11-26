# IDE Bridge Self-Test

Use this checklist to exercise the standalone `AiDevHelpers.MCP` server and a live IDE bridge. Load the tool schemas before calling tools. If there is no registered IDE instance, record the live checks as unavailable rather than launching or changing an IDE as part of a read-only pass.

## Read-only checks

- `IdeListInstances` — confirm the registered IDE kind, process ID, solution path, git branch, and worktree. Pass `solutionPath` to read the `check` block (`solution_open`, `expected_branch`, `expected_worktree`, `matching_instance_pids`, `ok`).
- `IdeBridgeHealth` — check the listener and request counters; pass `solutionPath` when more than one IDE instance is open.
- `IdeGetOutput` and `IdeSearchOutput` — inspect an existing Build or Debug output pane.
- `IdeGetBuildDiagnostics` — read the current Error List.
- `IdeListConfigurations` — read the solution configurations.
- `IdeGetDebugState`, `IdeListBreakpoints`, and `IdeListWatches` — read the debugger state.
- `IdeGetTestResults` — read the newest test result: the Tests pane after a Test Explorer run (names and messages of failures), or the newest TRX in DotnetTest mode.
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

Record the full argument, result, PID, and confirmed loaded solution path. A successful process launch alone does not pass the test; the bridge registry and health response must identify the requested solution. The tool invokes Visual Studio with only the full solution path. It does not pass `/edit`, which opens the file in an already-running instance instead of starting a new one. This check also validates that launch behavior in the installed Visual Studio version.

## Repo setup, script and install checks

Run these on a scratch git repo, not on a user's active work. None of them installs software or starts an elevated process unless a step says so.

1. `repo_setup` with `action: scan` on a repo that has a project whose `app.manifest` asks for `requireAdministrator`. Expect `pendingProjects` to name it, a `live` block (`checked: false` when `gh` is missing, never "no protections"), and that no `.aidevhelpers` folder was created.
2. In a console run `elev repo-setup <repo>`. Answer yes to the project and no to "always elevated". Expect `.aidevhelpers/elevation.json` listing it, `.aidevhelpers/user.json` gitignored, and `repo_setup` with `action: status` showing it as listed with nothing pending.
3. With `.aidevhelpers/repo-rules.json` naming `main` as protected, call `git_plan` with `action: commit` on `main`. Expect a refusal with a request id and no token. Run `elev rules override <repo> <id>`, plan again, and expect a token that works once.
4. `script_run` on a `.ps1` that exits 1, three times in 10 minutes. Expect `advice` on the third. With the user's agreement, call it again with `interactive: true` and expect a visible tab and the tab output back.
5. `script_run` on a `.py` or `.sh` with its interpreter missing. Expect an install hint, a `tool_install` offer, and nothing installed.
6. `terminal_session` with `action: types`, and `elev terminals scan`. Expect the same types; run `elev terminals setup` and answer yes to one type, then check the Windows Terminal `settings.json` keeps its comments and has a `.bak`.
7. `tool_install` with `action: plan` for `node`. Expect the exact winget command and a token, and nothing installed. Do not confirm unless installing Node is intended and `elev tools` (elevated) was run.
8. Elevated launch (needs a UAC answer): with the repo's `elevation.json` listing a project of a scratch solution, call `IdeLaunchInstance` for it. Expect a UAC prompt, `elevated: true`, and the bridge answering `IdeBridgeHealth`. Then open the same solution non-elevated, call `IdeStartDebugging`, and expect the refusal naming the project. Record whether the elevated instance reads as elevated; this confirms the token check in `ProcessElevation`.
