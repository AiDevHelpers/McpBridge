# Repo setup helper: elevation scan, repo rules, script helper

Status: proposed. Checked against the code on `wip` (8ca1c5b) and reviewed three times (the third by a read-only Sonnet pass). Builds on `docs/plans/archived/2026-11-23-elevated-terminal-and-repo-config.md` (all units done).

## Decisions

From the user:
- On repo open with no `.aidevtools`, run the scan automatically; the calling AI can also run it on demand.
- Detect elevation need from script markers, project manifests and debug-only needs.
- Launch the IDE elevated only for projects on the repo's elevated list; otherwise launch normally. Setup asks once whether the whole repo should always be elevated.
- The shared list lives in a separate committed file, `.aidevtools/elevation.json`, because an `allowAll` approval binds the hash of all of `settings.json`.
- Repo setup also checks CI, required-branch and protection rules (instruction files, CI definitions, live GitHub protections through `gh`, local hooks and config), and the tools enforce what it records.
- Script helper: a script that runs over and over or hangs is offered an interactive terminal run; the helper picks the best interpreter and terminal; it scans terminal types and asks before adding a missing one; it offers to install a missing interpreter through a package helper.
- Keep everything in one plan. The install capability is the last unit and lives in its own small project, not in `PackageHelper`.

Defaults chosen in review (the user did not answer; change them if wrong):
- **Facts committed, choices per-user.** `elevation.json` (committed) says which projects need elevation and why. The user's own choices (always-elevate mode, declined findings) go in the gitignored `user.json`.
- **Auto-run scans only; it writes on confirm.** Opening a repo must not dirty its working tree. Auto-run scans in memory and notifies; `.aidevtools/`, `elevation.json` and the `.gitignore` line are created when the user runs `repo-setup`. This departs from "build the folders when opened".
- **Thresholds are settings** (global, per-user override, never repo-settable): 3 runs in 10 minutes, 60 seconds of silence.

## Code facts that shaped the plan

1. `allowAll` approvals bind the whole `settings.json` hash, so elevation data stays out of that file.
2. `RepoOpenService` passes `root => File.Exists(root/.elevated.json)` to `RepoOpenChecker` as the "track this repo" test (`RepoOpenService.cs:23`) and polls every 5 seconds. The new trigger is the opposite condition and must not run a slow scan on the tick.
3. `setup/Program.cs` refuses to run unless elevated. Repo setup needs no elevation and would write admin-owned files into the repo, so it goes in the non-elevated `elev` client (`client/Program.cs`). That client treats fewer than 2 arguments as a usage error, knows only `--verify`, and calls `ConfigRepository.FromEnvironment()` at startup.
4. `MCP.Core` does not reference `ScriptHelper.Core`, and `ScriptHelper.Core` references only `Common`. MCP reaches ScriptHelper by running `elev` (`IElevVerifier`), so the trigger and `repo_setup scan` call `elev --repo-scan <root>`. Typed readers for the repo files go in `Common`.
5. The broker runs only fingerprinted scripts and cannot start an IDE. All three IDE launch paths use `UseShellExecute = false` (`IdeInstanceLifecycle.cs:43`, `DevenvLauncher.cs:53`, `IDELauncherService.cs:195,226`); `LaunchAsync` returns `already_running` for any matching instance; `VsInstance` fields are all `[JsonRequired]` (`VsInstanceRegistry.cs:107-112`).
6. `LaunchScriptScanner.Scan` returns only path and source file, and its walk has no reparse-point check or depth cap.
7. No `.sln`/`.slnx` parser exists: `GatePlanner` only picks the first `*.sln*` file name and `AffectedTests` tests an extension.
8. `git_plan`/`git_confirm` are MCP tools; the agent confirms its own plans today (`SourceControlService.ConfirmAsync` runs the token with no user check). `Plan` has no protected-branch data, `push` with no branch is `["push"]`, there is no force-push action, and `delete_branch` uses `-d`. The "user confirms in the console" rule exists only for the `elev` setup path (`ConsoleConfirmer`).
9. `GitHubService` only does `pr checks` and `run view`; it has no branch-protection or ruleset call. It lives in `SourceControlHelper`.
10. `TerminalConfigurationDiscovery` already finds profiles, WSL instances and extensions, and `TerminalSettingsManager` already backs up and rewrites Windows Terminal `settings.json`. `TerminalControl` references only `WindowsSession`.
11. The terminal tab command is a fixed `pwsh -NoProfile -File Wrapper.ps1 -StdoutPipe ... -CommandPipe ...` (`StandardTerminalSession.cs:201-204`), headless path included. `ProcessScriptRunner` (the broker's runner) is `pwsh.exe` only.
12. `PackageHelper` references only `Common` and runs `dotnet` through `ProcessPackageRunner`; it has no system-tool installer.

## Files

`.aidevtools/elevation.json` (committed, facts):

```json
{
  "projects": [
    { "path": "src/Driver/Driver.csproj", "reason": "app.manifest requireAdministrator" }
  ]
}
```

`.aidevtools/repo-rules.json` (committed, confirmed structured rules): `defaultBranch`, `protectedBranches`, `requiredChecks`, `branchNamePattern` (optional), `gateCommands`.

`.aidevtools/user.json` (gitignored, this user's choices; the allow-all pointer already lives here), new keys:

```json
{
  "elevation": { "mode": "listed", "declined": ["src/Tool/Tool.csproj"] },
  "terminals": { "registered": [], "declined": [] },
  "interpreters": { ".ps1": "powershell" },
  "scriptWatch": { "repeatCount": 3 },
  "livePolicy": {}
}
```

- `mode`: `listed` (default) | `always` | `never`. `never` lets one user opt out of elevated launches even for listed projects.
- Paths are repo-relative and validated: no `..`, no rooted path, must resolve inside the repo, else ignored with a warning.
- A missing, unknown or malformed file means never elevate, no rule, no override, plus a warning. (Installs are the one case where the user's chat yes is enough; see unit 15.) Nothing in the broker, allowlist or allow-all logic reads these files.
- Interpreter names and install ids never come from repo files, only from `user.json` or built-in tables.

## Units

Numbered in build order. Each: tests, lite gate, commit, push to `wip`; full gate before the PR.

**Part A: elevation scan and settings**

1. **Scanner groundwork (ScriptHelper.Core).** Extend `LaunchScriptScanner` to return which profile or configuration owns each script and its owning project, keeping the allowlist behavior. The walk does not follow reparse points, has a depth cap, and skips `packages` and `dist` as well as the current skips, because it now runs on repos the user did not choose. Tests: existing scanner tests unchanged, owner reported per source, symlink loop terminates.
2. **Elevation scanner.** `ElevationScanner.Scan(root)` returns `(Project, Kind, Evidence)`:
   - *Script markers:* `#Requires -RunAsAdministrator`, `Start-Process -Verb RunAs`, `net session`, `IsInRole(Administrator)`, `requireAdministrator` in `.ps1/.cmd/.bat/.sh`.
   - *Project manifests:* a new small `.sln`/`.slnx` project-list parser (none exists; where no solution is found, glob `**/*.csproj`), then `app.manifest` `requestedExecutionLevel level="requireAdministrator"`.
   - *Debug-only:* a project whose F5 target (launchSettings profile, launch.json config, startup project) is a marker script or elevated exe.
   Read only. Tests: one per kind, markers in comments or non-script files are not hits, project mapping, unreadable file skipped, no solution falls back to csproj glob.
3. **Files and reader.** `ElevationFiles` in `Common`: typed read of `elevation.json` and the `elevation` block of `user.json`, path validation, in-place JSON writers that keep unknown keys. `RepoSettings.Resolve` returns the merged view. Tests: read, malformed = never, validation, writers keep other keys, an `allowAll` approval stays valid after a write (hash of `settings.json` unchanged).
4. **`elev` client changes.** First restructure the argument parser (verbs with one argument must not hit the usage error) and make local, read-only verbs independent of `ConfigRepository`. Then add `elev --repo-scan <root>` (findings and current settings as JSON; no write, no broker) and `elev repo-setup <root>`: runs the scan, creates the folder, `elevation.json` and the `.gitignore` line, shows findings per project and asks "add to the elevated list?" (no goes to `declined`), then asks once "always run this repo elevated?" (sets `mode`). Prompts come from the console, never from arguments. A re-run shows only findings that are neither listed nor declined. Tests with a fake confirmer, as `SetupCliTests` does; existing `elev` invocations unchanged.
5. **MCP surface and trigger.**
   - `repo_setup` tool: `action: scan` (runs `elev --repo-scan`, returns findings) and `action: status` (what would launch elevated, and why; findings new since the last confirmed setup). It never applies decisions; it tells the AI to ask the user and run `repo-setup`.
   - A second checker beside `RepoOpenChecker` (its predicate is the opposite): for a newly opened git repo with no `.aidevtools`, once per repo per server session, run the scan off the tick loop (a queued task, never blocking the next 5-second check) and push one notification ("N projects need elevation; run `repo-setup <root>`"). It never prompts and never writes.
   Tests beside `RepoOpenCheckerTests`, including a slow scan not delaying the next tick.

**Part B: repo rules**

6. **Repo rules scan.** The file scan (`RulesScanner`, ScriptHelper.Core, read only, reached through `elev --repo-scan`) finds:
   - *Instruction files:* `CLAUDE.md`, `AGENTS.md`, `CONTRIBUTING.md`, PR templates, `docs/**` pages naming branches, gates or release rules. Listed with the matching lines for the user to turn into rules; never enforced by guesswork.
   - *CI definitions:* `.github/workflows/*.yml` (triggers, target branches, job names that become required checks), Azure Pipelines and similar.
   - *Local hooks and config:* `.githooks`, `.husky`, `core.hooksPath`, `CODEOWNERS`.
   The live part is separate, because `ScriptHelper.Core` cannot reach `GitHubService`: new `GitHubService` calls (`gh api repos/{o}/{r}/branches/{b}/protection` and rulesets) in `SourceControlHelper`, called from MCP.Core. A missing or signed-out `gh` is "not checked", never "no protections". Live results are per machine, so they are cached in `user.json` (`livePolicy`), not the committed file. `repo-setup` shows both next to the elevation findings and writes the confirmed structured rules to `repo-rules.json`. Tests: each source, `gh` absent, malformed workflow skipped, nothing written without confirmation, new `gh api` calls parse real output samples.
7. **Enforcement in the tools.** `RepoRules` (`Common`) reads `repo-rules.json` plus `livePolicy`. `SourceControlService.Plan` takes an injected `IRepoRules` and resolves the current branch with a `git` call before checking (a bare `push` has no branch argument). Covered actions: `commit` and `push` onto a protected branch, `delete_branch` of a protected branch, `create_branch` with a name that breaks `branchNamePattern`; each is refused with the rule named and the allowed alternative (create a branch, open a PR). There is no force-push action today, so none is claimed; add a force-push check only if such an action is added. `git_confirm` re-checks the rules at confirm time, so a rule changed between plan and confirm is seen. `pr_checks` and the gate tools compare against `requiredChecks` and `gateCommands` and report what is missing. Rules only restrict: a missing, malformed or hostile file never widens anything. An override is a new console command, `elev rules override <token>`, because `git_confirm` itself is agent-callable and the agent would otherwise waive its own rules. Merges stay outside the tools. Tests: refused and allowed case per action, bare `push` resolves the branch, rule change between plan and confirm refused, no file = no change, malformed = no change, stale live data falls back to the committed rules, override needs the console command.

**Part C: script helper**

8. **Terminal type scan and registration.** Machine-level. Extend `TerminalConfigurationDiscovery` (profiles, WSL instances) and `TerminalSettingsManager` (backup and rewrite of Windows Terminal `settings.json`) instead of writing a new scanner. Probe PowerShell 7, Windows PowerShell 5.1, `cmd`, Git Bash, each WSL distro (`wsl -l -q`, UTF-16 output) and the Visual Studio developer shells; compare with the known terminal types and with Windows Terminal's profiles (packaged, Preview and unpackaged locations), counting dynamic profiles as present.
   - *Missing means installed but not registered.* A tool that is not installed is reported with an install hint and handed to unit 15; the scan installs nothing.
   - *Ask, then add:* `elev terminals setup` asks per type from the console, never from tool arguments. A yes registers the type in `user.json` and adds a profile to Windows Terminal. A no goes to `declined`; `--all` re-asks.
   - *Editing safely:* first check how `TerminalSettingsManager` handles comments and trailing commas (Windows Terminal allows both) and reuse its backup and revert code. The insert must not drop the user's comments; deterministic GUID per profile makes a re-run a no-op; existing profiles are never changed; an unreadable file is left untouched with a message.
   - *Triggers:* on demand (`terminal_session` action `types`, `elev terminals scan`, both read only) and when unit 9 picks an interpreter whose terminal type is unregistered. Never on a timer, never writes without the yes.
   Tests: each type found and missing, dynamic profile counted, WSL parsing (UTF-16, none, not installed), declined remembered, comments and trailing commas survive, backup written, re-run adds nothing, unreadable file untouched.
9. **Interpreter detection.** `ScriptInterpreterResolver.Resolve(scriptPath)` in `Common` (TerminalControl cannot see ScriptHelper.Core, and `script_run` needs it too) returns `(Interpreter, Path, Version, Reason)`:
   - a `#!` line, matched by interpreter name only (`pwsh`, `powershell`, `bash`, `sh`, `python`, `node`), never by a path from the file;
   - `.ps1`: `#Requires -Version 7` or `-PSEdition Core` → `pwsh`; `-Version 5` or `-PSEdition Desktop` → Windows PowerShell 5.1; otherwise `pwsh` when installed, else 5.1;
   - `.cmd`/`.bat` → `cmd.exe /c`; `.sh` → Git Bash, then another real `bash`, never the WSL launcher `System32\bash.exe` by accident; `.py` → repo `.venv`, then `py`, then `python`; `.js`/`.mjs` → `node`.
   It probes PATH and well-known install folders once, caches path and version, and reports a missing interpreter with an install hint. `pwsh` stays the host of every terminal tab (the wrapper is fixed); a 5.1, bash or python script runs as a child command inside it, so these are commands, not different hosts. Per-user override in `user.json` (extension → interpreter name); never repo-settable. Elevated runs stay `pwsh`-only: the approval fingerprint records the interpreter (`PwshInterpreterInfo`), so an elevated script that needs another interpreter is reported unsupported, and widening the fingerprint is out of this plan. Tests: each marker and extension, `#Requires` wins, shebang name matched but path ignored, missing hint, WSL `bash.exe` not chosen, venv preferred, override order, cache invalidation.
10. **Script run tracker and settings.** `ScriptRunTracker` (MCP.Core), in memory per server session, records each run keyed by repo, script path and argument hash: start, end, exit code, duration, last output time. It flags `repeating` (default 3 runs in 10 minutes) and `hung` (default 60 seconds without output, or the timeout hit). Global `ScriptWatch` options in the MCP `appsettings.json` (`RepeatCount`, `RepeatWindowMinutes`, `NoOutputSeconds`, `TimeoutSeconds`), per-user override in `user.json`, never repo-settable. Tests: counting window, argument hash, hung by silence and by timeout, override order, flag clears after a clean run.
11. **`script_run` tool.** A new `TrackedScriptRunner` in MCP.Core, separate from the broker's `ProcessScriptRunner`; before writing its timeout, output-drain and process-tree-kill code, reuse the shared `Common` command runner where it already does these. It runs a `.ps1/.cmd/.bat/.sh/.py/.js` through unit 9's interpreter with captured output, a no-output watchdog and a timeout, records the run, and returns exit code, output tail, interpreter and `advice`; a flagged script gets the reason and an offer of `interactive: true`. `terminal_session` script runs and `elev` runs (`ScriptHelperTools`) feed the same tracker; `script_run` does not replace them. A script the AI starts with its own shell tool outside this MCP cannot be seen; the tool description tells the AI to use `script_run`, and the docs give an optional Claude Code hook that reports plain shell runs. Tests: tracked run, flagged repeat and hang, unknown extension refused, script outside the repo refused, watchdog kills the tree.
12. **Interactive mode, on the user's yes.** `script_run(interactive: true)` opens a visible `terminal_session` tab, runs the script there so the user sees prompts and uncaught errors, and returns the tab output tail and exit code once the script ends or the user stops it. Offered only after a flag; the user's yes in chat starts it. Elevated scripts: the broker stays the executor, but `ProcessScriptRunner` redirects output, so nothing shows live. This needs a new broker request flag, `interactive`: the broker runs the approved script in a visible console in the user's session and tees output to a file under the project data root. All allowlist, fingerprint, approval and allow-all checks run first and unchanged. Start with a spike (does a window from the elevated broker show in the user's session; can output be teed) and record the result here before building the elevated part. Tests: non-elevated run returns tail and exit code, declined offer runs nothing, elevated interactive run refused for an unapproved script, tee file readable by the requester only.

**Part D: elevated IDE launch**

13. **Spike: is an instance elevated?** Check whether a non-elevated process can read the elevation of an elevated `devenv` from its pid (token query is likely denied). If not, the VS bridge reports its own elevation in the instance manifest. Any new `VsInstance` field must be nullable and not `[JsonRequired]`, or manifests written by an older bridge fail to deserialise. Record the result here before unit 14.
14. **Elevated launch.** `ElevationPolicy.Decide(solution, settings)` (testable, no process code): `never` → normal; `always` or the solution/project is listed → elevated; else normal. An `IElevatedLauncher` seam does `UseShellExecute = true, Verb = "runas"` (one UAC prompt per launch, Win32 error 1223 when declined; the returned handle may be null or unusable for `ManagedProcessRegistry.Track`, so tracking goes by pid after the instance registers). Applied to `ide_launch_instance`, `ide_open_solution` and `IDELauncherService`:
    - `Find` takes an elevation filter; an elevated launch never reuses a non-elevated running instance; the result says so and offers a relaunch (it closes the IDE and can lose unsaved work, so only on the user's yes);
    - `ide_start_debugging` on a listed project from a non-elevated instance returns an error naming the project; the AI re-calls with `relaunch_elevated: true` after the user agrees;
    - kill and track of an elevated `devenv` report "needs elevation" instead of throwing.
    Visual Studio only; VS Code is out until its elevated launch is checked. Tests for each decision and for declined UAC. Real-IDE check through the bridge: elevated VS launches, the bridge connects, debug from a normal instance gives the error.

**Part E: installs and docs**

15. **Offer to install a missing interpreter.** A new small project (not `PackageHelper`, which is named for and limited to dotnet, pip and npm checks). When unit 9 reports an interpreter missing, the result offers to install it.
    - *Plan, then the user's yes:* same shape as `git_plan`/`git_confirm`. `tool_install_plan` returns the exact command, package id, source and a single-use token and the AI asks the user in chat; the user's yes is sufficient permission, and the AI then calls `tool_install_confirm(token)`. The token is single use and expires, and the tool description says it may be confirmed only after the user's yes. (User decision; this replaces the earlier console-only confirmation.)
    - *Runs through the elevated script helper:* the confirm step submits the install to the broker like any elevated script, so there is no second UAC path and no new elevation code; the one UAC prompt is the broker's own start. The script is a shipped `install-tool.ps1` in the admin-owned install directory, registered as a built-in project and fingerprinted at `setup` time, so the broker's allowlist, fingerprint and approval checks run on it unchanged. The script takes the interpreter name (not a package id), looks the id up in its own built-in table, and refuses anything else, so the table is enforced inside the admin-owned script as well as in MCP. To check at the start of the unit: that the broker accepts a script from the install directory as a registered project root.
    - *Installer:* `winget` only, `--source winget --exact`, package agreements accepted explicitly in the plan text; a missing `winget` is reported with how to get it.
    - *Curated ids, not free text:* a built-in table (`pwsh` → `Microsoft.PowerShell`, Git Bash → `Git.Git`, `python` → `Python.Python.3.x`, `node` → `OpenJS.NodeJS.LTS`). Any name outside the table, or from repo content, is refused.
    - *After install:* the resolver re-probes install folders and the registry, since the server's PATH is stale, then the original script request is re-run. WSL (`wsl --install`) is instructions only, because it needs a reboot.
    - *Declined* goes to `user.json` and is not asked again.
    Tests: plan for a known id, refuse an unknown name and one from repo content (in MCP and in the script), no winget, token single use and expiry, nothing runs before confirm, the install goes through the broker and fails when the script's fingerprint changed, declined remembered, re-probe finds a fresh install without a PATH change, the scan installs nothing.
16. **Docs.** `docs/ide-bridge.md`, `docs/self_test.md`, `src/ScriptHelper/README.md`, repo `CLAUDE.md` dogfood section, `docs/plans/tool-backlog.md` rows for `repo_setup`, `script_run`, terminal types and installs, `docs/dogfood.md` rows for any tool that fell short.

## Order

Build in number order. Parts A and B come first (no elevated rights involved). Within Part C, 8 comes before 9 so the resolver can use registered terminal types; 11 and 12 are blocked on 9, and the elevated half of 12 on its spike. Part D is independent and can move earlier; 14 is blocked on 13. 15 is blocked on 9 and 8.

## Risks

- A committed `elevation.json` can make a cloned repo request an elevated IDE. Mitigation: it only raises a UAC prompt the user can decline, `never` opts out per user, and paths are validated.
- Enforcement can block legitimate work if a rule is wrong or stale. Mitigation: only structured, user-confirmed rules are enforced, refusals name the rule and the way out, live protections are re-read on each `repo-setup`/`status`, and `elev rules override` exists.
- Script watch only sees runs that go through the MCP. Plain shell runs are invisible unless the optional hook is used.
- An interactive broker run shows a window with elevated rights; it stays limited to scripts that already pass the full allowlist and approval checks.
- Editing Windows Terminal's `settings.json` touches a user file outside the repo. Mitigation: only after a yes, backup, atomic write, idempotent, existing profiles never altered.
- Installing software is outward-facing and the user's chat yes is the only gate (the agent calls the confirm tool). Mitigation: curated names enforced in MCP and in the admin-owned script, winget only, plan shows the exact command first, single-use expiring token, nothing taken from repo content, the broker's fingerprint checks on the script.
- Prose instructions cannot be parsed reliably; setup surfaces them but never turns them into enforced rules on its own.
- Marker scanning is heuristic. Findings are suggestions, always confirmed by the user.
- Elevated VS talking to a non-elevated MCP server relies on the pipe ACL fix in 596501b; unit 14's real-IDE check covers it.

## Progress

- Units 1 to 4 done. `LaunchScriptScanner` reports owners, skips links and `packages`/`dist`, and stops at 12 levels (shared `RepoWalk`); `ElevationScanner` finds manifest, debug-script and script-marker findings; `ElevationFiles` (Common) reads and writes `elevation.json` and the `elevation` block of `user.json`, and `RepoSettingsFile.UpdateUserFile` keeps other keys (the allow-all pointer writer now uses it); `RepoSettings.Resolve` returns the elevation view; `elev --repo-scan <root>` and `elev repo-setup <root>` run locally before the broker config is touched. Tests: ScriptHelper 367 passed, Common 14, MCP.Core 191 (Release, `dotnet test`).
- Unit 5 done: `IElevScanner` (`elev --repo-scan`), `RepoSetupChecker` (once per repo per session, own task, read only), `RepoOpenService` runs it beside the allowlist check, `repo_setup` tool (scan, status; never writes). MCP.Core 205 passed.
- Dogfood gap: while Test Explorer runs the whole solution the IDE bridge stops answering (`ide_get_build_diagnostics` timed out) and its testhosts lock the Debug output, so `dotnet build` of the same projects fails; used `dotnet test -c Release` instead. Logged in `docs/dogfood.md`.
- Units 6 and 7 done. `RulesScanner` (instruction lines, workflow branches and job names, hooks, CODEOWNERS, origin default branch) feeds `elev repo-setup`, which asks before writing `repo-rules.json` and remembers a no in `user.json`; `GitHubService.LivePolicyAsync` reads protection and rulesets through `gh api` ("not checked" is never "no protections") and `repo_setup` caches it in `user.json` only for a repo that already has `.aidevtools`; `SourceControlService.PlanAsync` refuses commit and push on a protected branch, delete of one and a bad new branch name, and `ConfirmAsync` checks again; `elev rules override <repo> <id>` waives one refused write after a console yes; `pr_checks` returns `missingRequired` and `gate_plan` returns `missingRequiredCommands`. There is no force-push action, so none is claimed. Tests: Common 31, SourceControlHelper 41, ScriptHelper 380, MCP.Core 206.
- Unit 8 done, with two corrections to the plan text: (1) `TerminalConfigurationDiscovery` and `TerminalSettingsManager` could not be extended. They parse settings strictly (comments fail), read only the packaged location, read `wsl --list` as UTF-8, rewrite the whole file with no backup, and run only for temporary settings. The scanner, registry and text-level profile writer are new, in `Common/Terminals` (so `elev` and the MCP both reach them). (2) The registered and declined types are per machine, not per repo, so they live in `%LOCALAPPDATA%\AiDevHelpers\terminals.json`, not in `user.json`. Windows Terminal makes profiles for PowerShell 7, Git Bash, WSL and VS shells itself unless the source is in `disabledProfileSources`, so a type counts as covered unless its source is disabled and no profile runs it. `elev terminals scan|setup [--all]` and `terminal_session action: types` (read only) ship. Tests: Common 43, ScriptHelper 386, MCP.Core 207.
- Unit 9 done: `ScriptInterpreterResolver` and `ToolLocator` in `Common/Terminals` (override, `#!` name, `#Requires` markers, extension; Git Bash never the WSL launcher; repo `.venv` then `py` then `python`, Store stub skipped; a missing interpreter returns a hint, never a silent fallback when the script names its version). `BuildArguments` passes arguments as data. Per-extension overrides come from the `interpreters` block of `user.json`, known names only. The terminal tab host and the broker's `ProcessScriptRunner` are unchanged (pwsh only). Common 65 tests. One `AGrant_ExpiresAfterTenMinutes...` failure appeared once in a combined run and did not repeat in 8 reruns; cause not found.
- Unit 10 done: `ScriptRunTracker` (in memory; key = repo, script, argument hash; repeating after 3 non-clean runs in 10 minutes, hung when the last run was silent or hit the limit, a clean run clears it) and `ScriptWatchOptions` (server section `ScriptWatch`, per-user `scriptWatch` block in `user.json`, never from a committed file). `CommandRunner` gained `NoOutputTimeout`, `OnOutput` and `Silent`, so unit 11 reuses the shared runner's timeout, drain and tree kill instead of new process code. Tests: Common 69, MCP.Core 214.
- Unit 11 done: `ScriptRunService` and the `script_run` tool (shared `CommandRunner`, interpreter from unit 9, tracker from unit 10; arguments as data; script must be inside the repo; missing interpreter gives a hint; advice offers the visible terminal; a note when the interpreter's terminal type is not registered). `run_elevated_script` and `terminal_session exec` feed the same tracker. The `interactive` argument comes in unit 12. MCP.Core 226 tests.
- Unit 12, non-elevated half done: `script_run(interactive: true)` runs the script in a visible `InteractiveUser` terminal tab through `InteractiveScriptRunner` (every argument a quoted PowerShell literal, smart quotes included; PowerShell without `-NonInteractive` so it may prompt; folder set to the repo), returns the tab output, exit code, session and tab ids, and the run still counts in the tracker. MCP.Core 232 tests. **Elevated half is blocked on the spike**: the broker `interactive` request kind (visible console in the user's session, output teed to a file) cannot be checked without answering a UAC prompt and watching the window, so it is not built; an elevated script keeps running through `run_elevated_script` with captured output.
- Unit 13 done (spike, partial): `ProcessElevation.Of(pid)` in `Common`. Measured on this machine from a non-elevated shell: the token of a same-user, non-elevated process (devenv, explorer, pwsh) is readable; SYSTEM processes (winlogon, services) cannot be opened at all (access denied). No elevated same-user process was available, so the last case, a refused token query on an openable process read as elevated, is Windows' documented behaviour but not confirmed here; unit 14's real-IDE check with an elevated Visual Studio confirms it. A manifest field from the VS bridge stays the fallback if it does not hold. Common 72 tests.
- Unit 14 done in code (real-IDE check still open). `ElevationPolicy` (Common: `never` beats the list, `always`, or a solution project on the committed list; never on a guess), `SolutionProjects` (shared `.sln`/`.slnx` reader, the elevation scanner now uses it), `ElevatedLaunch.Plan` (pure) and `StartElevated` (`runas`, one UAC prompt, declined reported, the elevated process is neither tracked nor inspected), `IdeInstanceLifecycle.LaunchAsync(relaunchElevated)` (a non-elevated instance of such a solution is not reused: `needs_elevated_relaunch`; with the user's yes it saves, closes, then starts elevated and ignores the closed instance's lingering manifest), `ide_open_solution` under the same rule, `DebugElevationGuard` on `ide_start_debugging`, and kill of an elevated instance reports instead of throwing. `IDELauncherService` is not on any tool path (nothing calls it), so it is unchanged. VS Code stays out. **Open: the real-IDE check** (an elevated Visual Studio launching, its bridge connecting, and the refused token query reading as elevated) needs a UAC answer from the user. MCP.Core 244 tests, Common 80.
