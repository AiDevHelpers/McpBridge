# Repo setup helper: elevation scan, repo settings, elevated IDE launch

Status: proposed. Checked against the code on `wip` (8ca1c5b) and reviewed twice. Builds on `2026-11-23-elevated-terminal-and-repo-config.md` (units 1-5c done).

## Decisions

From the user:
- On repo open with no `.aidevtools`, run the scan automatically; the calling AI can also run it on demand.
- Detect elevation need from script markers, project manifests and debug-only needs.
- Launch the IDE elevated only for projects on the repo's elevated list; otherwise launch normally. Setup asks once whether the whole repo should always be elevated.
- The shared list lives in a separate committed file, `.aidevtools/elevation.json`, because an `allowAll` approval binds the hash of all of `settings.json`.

Defaults I chose in the review (the user did not answer; change them if wrong):
- **Facts committed, choices per-user.** `elevation.json` (committed) says which projects need elevation and why. The user's own choices (always-elevate mode, declined findings) go in the gitignored `user.json`, so one person's "no" or "always" never applies to the team.
- **Auto-run scans only; it writes on confirm.** Opening a repo must not dirty its working tree (third-party clones, read-only checkouts). Auto-run scans in memory and notifies; `.aidevtools/`, `elevation.json` and the `.gitignore` line are created when the user runs `repo-setup`. This departs from "build the folders when opened"; say if you want it to write on open.

Added later by the user: repo setup also checks for CI, required-branch and protection rules (sources: instruction files, CI definitions, live GitHub protections through `gh`, local git hooks and config) and the tools enforce what it records. See units 9 and 10.

## Code facts that shaped the plan

1. `allowAll` approvals bind the whole `settings.json` hash, so elevation data stays out of that file.
2. `RepoOpenChecker` only tracks repos that already have `.elevated.json`; the new trigger needs its own check.
3. `setup/Program.cs` refuses to run unless elevated. Repo setup does not need elevation and would write admin-owned files into the repo, so it goes in the non-elevated `elev` client (`client/Program.cs`), next to `--verify`.
4. `MCP.Core` does not reference `ScriptHelper.Core`. MCP already reaches ScriptHelper by running `elev` (`IElevVerifier`), so the auto-trigger and `repo_setup scan` call `elev --repo-scan <root>` (local, read only, no broker, JSON out) instead of moving the scanner. Only a small typed reader for the two repo files goes into `Common`, because MCP needs it to decide launches.
5. The broker runs only fingerprinted scripts and cannot start an IDE. Elevated launch uses `UseShellExecute = true, Verb = "runas"`: one UAC prompt per launch, Win32 error 1223 when declined. All three launch paths use `UseShellExecute = false` today (`IdeInstanceLifecycle.LaunchAsync`, `DevenvLauncher.Open`, `IDELauncherService.LaunchVisualStudio`).
6. `IdeInstanceLifecycle.LaunchAsync` reuses any instance with the solution open, `VsInstance` has no elevation field and all its fields are `JsonRequired`, and a non-elevated server cannot kill or track an elevated `devenv`.
7. `LaunchScriptScanner.Scan` returns only path and source file. Debug-only detection needs the profile or configuration that owns each script, so the scanner must expose that.

## Files

`.aidevtools/elevation.json` (committed, facts):

```json
{
  "projects": [
    { "path": "src/Driver/Driver.csproj", "reason": "app.manifest requireAdministrator" }
  ]
}
```

`.aidevtools/user.json` (gitignored, this user's choices; the allow-all pointer already lives here), new key:

```json
{ "elevation": { "mode": "listed", "declined": ["src/Tool/Tool.csproj"] } }
```

- `mode`: `listed` (default) | `always` | `never`. `never` lets one user opt out of elevated launches even for listed projects.
- Paths are repo-relative and validated: no `..`, no rooted path, must resolve inside the repo, else ignored with a warning.
- A missing, unknown or malformed file means never elevate, plus a warning. Nothing in the broker, allowlist or allow-all logic reads these files; elevation stays the user's UAC consent.
- `reason` makes a reviewer's diff of `elevation.json` meaningful.

## Units

1. **Scanner groundwork (ScriptHelper.Core).** Extend `LaunchScriptScanner` to return which profile or configuration owns each script and its owning project, keeping the current allowlist behavior. Reparse points are not followed and the walk has a depth/size cap, because it now runs on repos the user did not choose. Tests: existing scanner tests unchanged, owner reported for each source, symlink loop terminates.
2. **Elevation scanner.** `ElevationScanner.Scan(root)` returns `(Project, Kind, Evidence)`:
   - *Script markers:* `#Requires -RunAsAdministrator`, `Start-Process -Verb RunAs`, `net session`, `IsInRole(Administrator)`, `requireAdministrator` in `.ps1/.cmd/.bat/.sh`.
   - *Project manifests:* projects from the `.sln`/`.slnx` (reuse the parsing in `GatePlanner`/`AffectedTests` if it fits), then `app.manifest` `requestedExecutionLevel level="requireAdministrator"`.
   - *Debug-only:* a project whose F5 target (launchSettings profile, launch.json config, startup project) is a marker script or elevated exe.
   Read only. Tests: one per kind, markers inside comments or strings in non-script files are not hits, project mapping, unreadable file skipped, no `.sln` falls back to repo-wide findings without a project.
3. **Files and reader.** `ElevationFiles` in `Common`: typed read of `elevation.json` and the `elevation` block of `user.json`, path validation, in-place JSON writers that keep unknown keys. `RepoSettings.Resolve` returns the merged view. Tests: read, malformed = never, validation, writers keep other keys, the `allowAll` approval stays valid after a write (hash of `settings.json` unchanged).
4. **`elev --repo-scan <root>` and `elev repo-setup <root>`.** `--repo-scan` prints findings plus current settings as JSON; it writes nothing and starts no broker. `repo-setup` runs the scan, creates the folder, `elevation.json` and the `.gitignore` line, shows findings per project and asks "add to the elevated list?" (no goes to the user's `declined`), then asks once "always run this repo elevated?" (sets `mode`). Prompts come from the console, never from arguments. Re-run shows only findings that are neither listed nor declined. Tests with a fake confirmer, as `SetupCliTests` does.
5. **MCP surface and trigger.**
   - `repo_setup` tool: `action: scan` (runs `elev --repo-scan`, returns findings) and `action: status` (what would launch elevated, and why). It never applies decisions; it tells the AI to ask the user and run `repo-setup`.
   - `RepoOpenService`: for a newly opened git repo with no `.aidevtools`, once per repo per server session, run the scan and push one notification ("N projects need elevation; run `repo-setup <root>`"). It never prompts and never writes.
   - `status` and the notification also report findings that are new since the last confirmed setup, so a project that gains an elevation need later is noticed.
   Tests beside `RepoOpenCheckerTests`.
6. **Spike: how to know an instance is elevated.** Before relying on it, check whether a non-elevated process can read the elevation of an elevated `devenv` from its pid (token query is likely denied). If not, the VS bridge reports its own elevation in the instance manifest as an optional field. Result is recorded in the plan before unit 7 starts.
7. **Elevated launch.** A single `ElevationPolicy.Decide(solution, settings)` (testable, no process code) returns elevated or normal: `never` → normal; `always` or the solution/project is listed → elevated; else normal, never on a guess. An `IElevatedLauncher` seam does the `runas` start. Applied to `ide_launch_instance`, `ide_open_solution` and `IDELauncherService`:
   - elevated launch never reuses a non-elevated running instance; the result says so and offers a relaunch (it closes the IDE and can lose unsaved work, so only on the user's yes);
   - `ide_start_debugging` on a listed project from a non-elevated instance returns an error naming the project; the AI re-calls with `relaunch_elevated: true` after the user agrees;
   - kill and track of an elevated `devenv` report "needs elevation" instead of throwing.
   Visual Studio only; VS Code is out until its elevated launch is checked. Tests for each decision and for declined UAC. Real-IDE check through the bridge: elevated VS launches, the bridge connects, debug from a normal instance gives the error.
9. **Repo rules scan.** `RulesScanner` (ScriptHelper.Core, read only, reached through `elev --repo-scan` like the rest) finds:
   - *Instruction files:* `CLAUDE.md`, `AGENTS.md`, `CONTRIBUTING.md`, PR templates, `docs/**` pages naming branches, gates or release rules. Code can locate and quote these but cannot reliably parse prose, so they are listed with the matching lines for the user and the AI to turn into rules; they are never enforced by guesswork.
   - *CI definitions:* `.github/workflows/*.yml` (triggers, target branches, job names that become required checks), Azure Pipelines and similar.
   - *Local hooks and config:* `.githooks`, `.husky`, `core.hooksPath`, `CODEOWNERS`.
   - *Live GitHub protections:* branch protection or rulesets (protected branches, required checks, required reviews, force-push and deletion rules) through the existing `GitHubService` (`gh`). A missing or signed-out `gh` is reported as "not checked", never as "no protections". Live results are per machine, so they go in `user.json`, not the committed file.
   `repo-setup` shows these next to the elevation findings and writes the confirmed structured rules to `.aidevtools/repo-rules.json` (committed): `defaultBranch`, `protectedBranches`, `requiredChecks`, `branchNamePattern` (optional), `gateCommands`. Tests: each source, `gh` absent, malformed workflow skipped, nothing written without confirmation.
10. **Enforcement in the tools.** `RepoRules` (Common) reads `repo-rules.json` plus the live protections cached in `user.json`. `git_plan` checks the planned write before it returns a token: push or commit onto a protected branch, deleting a protected branch, a force push, and a new branch whose name breaks `branchNamePattern` are refused with the rule named and the allowed alternative (create a branch, open a PR). `pr_checks` and the gate tools compare against `requiredChecks` and `gateCommands` and report what is missing. Rules only restrict: a missing, malformed or hostile file never widens anything, and a user can override one call by confirming through the console, not through tool arguments, so the agent cannot waive a rule. Merges stay outside the tools. Tests: refused and allowed case per rule, no file = no change, malformed file = no change, live protections stale or unavailable fall back to the committed rules, an override needs the console confirmation.
8. **Docs.** `docs/ide-bridge.md`, `docs/self_test.md`, `src/ScriptHelper/README.md`, repo `CLAUDE.md` dogfood section, `docs/plans/tool-backlog.md` row for `repo_setup`, `docs/dogfood.md` rows for any tool that fell short.

## Order

1, 2, 3 (no elevation involved), 4, 5, then 9 and 10 (rules; independent of the elevated launch), then 6 and 7, then 8 (docs cover everything). Each unit: tests, lite gate, commit, push to `wip`; full gate before the PR.

## Risks

- A committed `elevation.json` can make a cloned repo request an elevated IDE. Mitigation: it only raises a UAC prompt the user can decline, `never` opts out per user, and paths are validated.
- Enforcement can block legitimate work if a rule is wrong or stale. Mitigation: only structured, user-confirmed rules are enforced, refusals name the rule and the way out, live protections are re-read on each `repo-setup`/`status`, and a console override exists.
- Prose instructions cannot be parsed reliably; setup surfaces them but never turns them into enforced rules on its own.
- Marker scanning is heuristic. Findings are suggestions, always confirmed by the user.
- Elevated VS talking to a non-elevated MCP server relies on the pipe ACL fix in 596501b; unit 7's real-IDE check covers it.
