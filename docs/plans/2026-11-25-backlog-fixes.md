# Backlog fixes: implementation plan, 2026-11-25

Source: `C:\Users\rsper\.claude\docs\tool-backlog.md` (the 14 open table rows).
Status: revision 2, after the read-only Sonnet review of revision 1. Not approved for implementation.

## 1. Scope

Close every open backlog row that is still a gap in an aidevtools tool. Several rows are already built; those get a verification step and a row closure, not new code.

Already built (verified in code, unit 0 below closes their rows):
- `git_plan` `sequence` runs stage, commit, push under one token and stops at the first failure (`SourceControlTools.cs:69-105`).
- `test_hosts_list` and `test_hosts_stop(token)` kill only listed PIDs (`TestHostTools.cs`).
- `ide_attach_process(pid, confirm)` exists (`IdeTools.cs:558`, `IdeActions.cs:175`); its live check is tracked in `vs-api-coverage.md` item 1.8.
- `ide_format_document` and `ide_format_solution` exist (`IdeTools.cs:129,250`; `IdeActions.cs:90,229`); the solution format walks every `*.cs`.
- `ide_run_tests` has `project`, and a one-retry path with an hresult (`IdeTools.cs:95-125`, `RunTestExplorerWithRetryAsync`).
- `ide_kill_instance` saves first and refuses on unsaved documents (`IdeTools.cs:378`).
- Open-solution reuse and starting-instance listing exist (`IdeSolutionOpener.cs`, `IdeInstanceLifecycle.LaunchAsync`, `IdeInstanceRouter.ListInstances`).

## 2. Conventions every unit follows

- Logic in a helper or service with xUnit tests; the MCP tool stays a thin wrapper.
- Reply shapes: use the bridge shapes in `docs/plans/2026-11-25-vs-api-coverage.md` section 3.1 for bridge (VS) actions. MCP-only tools keep their own token pattern (`git_plan`, `test_hosts_list`, `tool_install`). Each unit names which pattern it uses.
- A write that changes VS state takes `confirm` (default false) and returns the plan without it.
- Each new or changed tool is listed in `docs/ide-bridge.md` or the README tool list (`DocsMatchCodeTests`).
- Each new tool gets an entry in its helper's `SelfTest.md` (`SelfTestCoverageTests`).
- User-facing text goes to `Strings.resx`; `HardCodedStringTests` must pass.
- Lite gate after each unit (`.claude/reference/lite-gate.md`); full gate before the PR.
- Any unit that changes Visual Studio behaviour gets a live check through the bridge and a row in `docs/dogfood.md`.
- One commit per unit.

## 3. Units

### Unit 0. Close rows already built

Verify, with a test or a live check, the behaviour the backlog row asks for, then mark the row done in `tool-backlog.md` (outside the repo, so a separate manual step):
- git_plan/git_confirm (row 11): add a test that a failing push reports the applied commit and stops.
- test_hosts / ide_kill_process (row 3): change the `ide_kill_process` refusal text to point to `test_hosts_list`.
- ide_attach_process (row 19 attach part): live check only.
- format (row 18): check non-`.cs` files; if none are missing, close the row.
- ide_kill_instance (row 17 part): the remaining gap is the `force` path, in unit 7.

### Unit 1. Default gates (rows 14)

1a. `GatePlanner` (`src/Helpers/GateHelper/src/GatePlanner.cs:20-24`) gains `mode` (lite or full). Lite = build, then `test_affected`. Full = clean, rebuild, run all tests. Add `mode` to `GateRun` and `GatePlan`. Tests: each mode, unknown mode is an argument error.
1b. `test_affected` becomes a gate step kind that `GateRunner` can execute (today `GateProcessRunner` shells out to `cmd.exe` only). Tests: step kind dispatch with a fake runner.
1c. Default gate content: write both defaults as `gate` blocks, copied from the existing `lite-gate.md` and `full-gate.md` format. "Fix every issue, warning and notice" is a human step, written in the gate text, not a command.
1d. Fallback lookup: repo `.claude/reference/<mode>-gate.md` first, then the global copy. Writing the global copy on first use is open question Q3.

### Unit 2. Test run: filter, reason, cancel, still-running (row 2)

- Map the bridge `refused` hresult to a reason string (`IdeActions.cs` `RunTests`).
- Add `filter` to `ide_run_tests`. It is passed to `dotnet test --filter` (Test Explorer cannot take a filter). `project` already exists.
- Cancel a stuck run: name the DTE command, or drop this item. Open question Q5.
- Report `still_running: true` when the pane is busy.
- Tests: `IdeToolsCallTests`, fake pane reader, hresult to reason table.

### Unit 3. Test results on a fresh instance (row 6)

- `ide_get_test_results` and `ide_get_test_summary` take `timeout_seconds` (default 60, max 300).
- Wait loop in `IdeTestRunService`, not in `TestSummaryParser`. Clock and delay are injected (as `IdeSolutionOpener` does) so tests do not sleep.
- Reply when nothing starts: `{ok:true, state:"not_started", discovered:false}`.
- Tests: states discovering, running, finished, absent.

### Unit 4. Open-solution verification (rows 22, 23)

- Reproduce the two reported failures against the current code first: a second instance for an open solution; `ok` while the list is empty.
- Keep only the gap a test reproduces, in `IdeSolutionOpener.cs`, `IdeInstanceLifecycle.cs`, `IdeInstanceRouter.cs` (`src/Bridge/src`). If no gap reproduces, close the rows.

### Unit 5. Solution in another worktree: choice reply (row 15)

- Depends on unit 4 (same router).
- When the target solution is not open, `ide_open_solution` and `ide_run_tests` return `{ok:false, needs_choice:true, choices:[{id:"open_new"},{id:"switch"}]}`.
- Both tools take `choice`. `switch` uses the in-bridge `openSolution` (`IdeActions.cs:500`) and needs `confirm`, since it closes the current solution. It reuses the unsaved-documents check from unit 7.
- The tool never picks a choice itself.
- Tests: needs_choice reply; each choice path with a fake router.

### Unit 6. Busy and modal state (rows 4, 20)

- One reply shape for both: `{ok:false, busy:true, modal:null | {text}, relaunch:"offer"}`. Replaces the timeout. Lives in `IdeInstanceRouter` and `BridgeServer.cs`, not `IdeTools.cs`.
- Modal detection runs out of process. The VS UI thread is blocked while a modal is open, so the bridge cannot read it. Use `EnumWindows` on the devenv PID, or UI Automation, from the MCP side. Open question Q2 decides the live check.
- While a modal is open, build, start-debugging and run-tests refuse without running.
- Tests: busy reply shape with and without modal text; refusal while modal is set.

### Unit 6b. Build-error run guard (row 20, second part)

- Separate decision: a build with errors must not start a run. That needs VS's "run when build errors occur" option or the prompt suppressed. Open question Q6. Not started until answered.

### Unit 7. Safe instance close (row 17)

- `ide_kill_instance` stays the default close path.
- Add `force` with `confirm`: without confirm it returns the plan. With confirm it closes without saving, listing the unsaved documents first.
- Tests: unsaved documents refuse without force; force plan-only without confirm.

### Unit 8. Gate reads the IDE error list (row 21)

- `GateRunner` runs shell commands only. Use `ide_get_build_diagnostics` through the MCP side for per-project notices, warnings and errors.
- The VSSDK failure (MSB4018) is matched by a known-issue table and reported as an environment issue.
- Tests: error-list parser; known-issue match.

### Unit 9. Build waits for its own server lock (row 16)

- Mechanism: build the gate to a separate output folder (`OutDir`), so the server's lock on its own output does not matter. Do not kill the server by name or restart it; repo CLAUDE.md says close `AiDevHelpers.MCP` by specific PID only.
- Tests: output folder used for the gate build.
- The "session survives a build" check is manual; record it in `dogfood.md`.

### Unit 10a. Elevation scanner: sudo marker (row 22, part)

- `src/ScriptHelper/src/ElevationScanner.cs` already matches `-Verb RunAs` and `#Requires -RunAsAdministrator`. Add the `sudo` marker. Open question Q7 (sudo.exe or a bash sudo in a launch script).
- Tests: `ElevationScannerTests.cs`.
- No `scripts/*.ps1` changes, so no pin change.

### Unit 10b. Decisions JSON and repo_setup apply (row 22)

- `repo_setup` gains an apply action that takes the decisions JSON the chat collected. It is a write, so it takes `confirm`.
- Files: the `repo_setup` tool (`RepoSetupToolsTests.cs` covers it), `src/ScriptHelper/client/Program.cs`, `SetupAbstractions.cs`.
- Tests: decisions JSON parsing; apply without confirm is plan-only.

### Unit 10c. Non-interactive approve and run (row 22)

- `elev approve --prepare/--confirm` stays the human-only step. A chat session never runs `--confirm` for a script; the user runs it in a terminal.
- `elev run` takes its answers as arguments; no console prompt.
- Tests: argument parsing; prepare output shape.
- Elevated-script changes need the pin rule (repo CLAUDE.md). Any change to a `scripts/*.ps1` file gets a separate reviewed commit with `-WritePin`. Expected: none.

## 4. Order and batching

Two PRs to `dev`, so a bridge regression does not block the gate and elevation fixes.

- PR A (no IDE): unit 0 (verifications), 1, 8, 9, 10a, 10b, 10c.
- PR B (IDE bridge): 2, 3, 4 then 5 (same router), 6 then 7 (shared unsaved check), 6b only after Q6.

Unit 0 and unit 4 start first: they decide which rows close and which remain.

## 5. Open questions

- Q2 (unit 6): can a live modal dialog be triggered for the check, or is a unit-level check enough?
- Q3 (unit 1d): may the default gates be written to `~/.claude/` on first use? Proposed: copy only if absent, never overwrite; if the home folder is unwritable, use the embedded default with no error.
- Q5 (unit 2): is there a DTE command to cancel a stuck test run, or drop the cancel item?
- Q6 (unit 6b): may the build-error run guard change VS's run-on-error option, or suppress the prompt?
- Q7 (unit 10a): does `sudo` mean `sudo.exe` (Windows 11) or a bash `sudo` in a launch script?
- Q8 (section 4): confirm two PRs to `dev`, and a base of `main` (repo convention, current branch `wip`).

Answered by this revision: the attach scope (already built), the elevated-script pin question (pin only on a reviewed `scripts/*.ps1` change; expected none).

## 6. Done when

- Every open row is either closed with a test or live check, or listed in section 5 with its question answered.
- Full gate passes on each PR; `DocsMatchCodeTests`, `HardCodedStringTests` and `SelfTestCoverageTests` pass.
- Live checks are recorded in `docs/dogfood.md` for units 0 (attach, format), 2, 3, 4, 5, 6, 7, 9.
- Rows are marked done in `tool-backlog.md` (manual step, outside the repo).
