# Elevated terminal, allow-all, and repo-level settings

Status: proposed. Decisions from the user: one UAC prompt then reuse; allow-all persisted in the repo with a double confirmation; the allow-all flag is repo-only and ignored in global settings; `.aidevtools/settings.json`, repo wins per key.

## Units

1. **Repo settings layer.** Read `<repo>/.aidevtools/settings.json` (repo root = nearest folder with `.git` above the solution). Merge over the global MCP settings per key, repo wins. Applies to `IdeTools` options such as `IdeTestRunMode`. Tests: override, fall-through, missing folder, malformed file (global used, error reported).
2. **Repo-only key `allowAll`, and `user.json`.** `allowAll` is read only from the repo `settings.json`; a global `allowAll` is ignored and reported once. Add the gitignored per-user `user.json` layer and its `.gitignore` entry. Tests: global ignored, repo read, `user.json` never committed.
3. **Background elevated terminal.** On the first elevated request in a session, open one elevated `terminal_session` (one UAC prompt), keep it, and send every later elevated task through it with no prompt. Session end closes it. Tests: one launch for N tasks, relaunch after the terminal dies, no launch when nothing elevated is asked.
4. **Route elevated tasks through it.** ScriptHelper `elev` requests and `gate-tpm` style runs use the background terminal instead of a fresh UAC each time. The allowlist and fingerprint checks stay in front of it.
5. **Allow-all with double confirmation.** A ScriptHelper command that turns on allow-all for a repo: confirmation 1 shows the repo path and the risk and needs a typed phrase; confirmation 2 is a separate prompt that repeats the repo path. Only then is the signed approval written to `.aidevtools/user.json`. Tests: one confirmation is not enough, a wrong phrase aborts, an edited `settings.json` or an unsigned entry is rejected.
5b. **Verify on repo open (added by the user).** When a repo is opened, ScriptHelper checks that every file in the repo allowlist (the `.elevated.json` entries and the scripts they name) has a valid per-user signature in the admin store. For any file that is unsigned or whose fingerprint changed, it asks the user, from an elevated console, to confirm. Confirming signs the current fingerprint for this user. This lets a team update the committed lists freely while each user re-approves the changes on their own machine. Open: what counts as "repo open" (IDE solution open event, MCP start, or the first elevated request), to settle when the unit starts.
5c. **Scan for launch and debug scripts (added by the user).** A ScriptHelper command scans the repo for launch and debug entry points: `Properties/launchSettings.json` profiles (executable, commandLineArgs), `.vscode/launch.json` and `tasks.json` entries, and scripts they call. It lists what it found, marks which are already in the allowlist, and asks the user which to add. Nothing is added without the user's yes per item, and added entries go through the same signing as 5b. Tests: finds each source, skips entries already listed, adds nothing on "no".
6. **Docs.** `docs/ide-bridge.md`, `docs/self_test.md`, the repo `CLAUDE.md` dogfood section.

## Three config layers

1. Global: the MCP `appsettings.json`.
2. Repo, committed: `<repo>/.aidevtools/settings.json`. Overrides global per key. May request `allowAll`.
3. Repo, per user, gitignored: `<repo>/.aidevtools/user.json`. Holds this user's permission hashes: the record that this user approved allow-all for this repo. Unit 1 adds `.aidevtools/user.json` to the repo `.gitignore` if it is missing.

## Security rule for allow-all

A repo file alone must never grant elevation: anyone who clones a repo with `allowAll: true` would give that repo admin on their machine. So `settings.json` only requests allow-all; it takes effect only when `user.json` holds a matching approval, written by the double confirmation. The approval binds the repo path and the hash of `settings.json`, so editing `settings.json` invalidates it and asks again.

`user.json` sits in a folder the unelevated user can write, so the hash alone is not proof: any unelevated process could write one. Each approval therefore carries a signature from the ScriptHelper approval signer (the admin-only key, TPM when present), and only the elevated broker verifies it. An unsigned or wrongly signed entry is ignored.

## Review findings (Sonnet, read-only)

- **Hook points.** Repo settings: `IdeToolsOptions` is bound once (`McpServiceCollectionExtensions.cs:31`) and read from a singleton `IOptions` in `IdeTools`; it needs a per-call resolver taking the solution path, and a whitelist of repo-overridable keys. Terminal: `ElevatedTerminalSession` is a thin subclass; the logic is in `StandardTerminalSession`, where `CreateTabAsync` raises UAC per tab (`runas`) and `ExecuteInTabAsync` runs plain strings. So "one UAC then reuse" means one tab kept alive. Elevated runs: `ProcessElevRunner` (`ScriptHelperTools.cs:18`) and the `RequestHandler.Decide` gate stay.
- **Routing through a terminal bypasses the broker.** The broker is admin-owned and runs only fingerprinted, signed-approved scripts. The terminal tab is driven over named pipes the user owns, so any unelevated process of the same user could inject commands. The terminal may only carry a fixed `elev`-style invocation with arguments as data, never a concatenated string, and must record the owner SID and refuse others.
- **Stale reuse.** `IsAliveAsync` only checks state. Needs a ping probe and teardown on any timeout, so late output cannot contaminate later runs.
- **user.json signing.** `AclOnlyApprovalSigner.Verify` accepts an empty signature, so on a machine without a TPM a forged `user.json` verifies. The approval must bind the registry `ProjectId`, the project root, a `Version` for replay, and the `settings.json` hash over canonical bytes, and the project must be registered (registration needs elevation).
- **Confirmation channel.** The typed phrase and second prompt come from an elevated console, never through MCP tool arguments, or the agent could approve itself.
- **Scope of allow-all.** It skips the per-script approval step only. It never bypasses the `.elevated.json` manifest allowlist.
- **Repo root.** Nearest `.git` ignores worktrees (`.git` is a file) and submodules; handle both. A malformed repo file falls back to the global value and reports, it never widens anything.
- **Order.** Decide the executor model and the allow-all design (scope, signer, storage) before the terminal units. `.gitignore` handling lives in the unit that owns `user.json`.

## Decisions after the review

- **Executor:** the broker executes. The background elevated terminal only carries a fixed `elev`-style invocation with arguments as data, so the one UAC prompt is reused and the broker's checks and TPM-backed trust stay.
- **Approval store:** the signed allow-all approval lives in the admin-only data root, keyed by the registered project. The gitignored `.aidevtools/user.json` holds only this user's pointer and hash; a forged `user.json` grants nothing.
- **Allow-all scope (user's choice, against the review's recommendation):** allow-all bypasses file verification, so any script in the repo may run elevated when requested or needed. It does not skip the manifest by accident: the repo must be registered, the settings hash must match, and the double confirmation from an elevated console must have been given. Risk: a script added to a registered repo later runs elevated without a new approval; the settings hash binds only `settings.json`, not the scripts.

## Order

1 and 2 first (no elevation involved), then 3 and 4, then 5, then 6. Each unit: tests, lite gate, commit, push to `wip`.

## Progress

- Unit 1 done: `RepoSettings` merges `.aidevtools/settings.json` (whitelisted `IdeTestRunMode`) over the global value; `IdeTools` uses it for the test run mode. Warnings are returned by `Resolve` but not yet shown to the caller.
- Unit 2 done: `allowAll` is read only from the repo file (a global one is ignored with a warning on stderr), `RepoSettings.HashFile` hashes the settings file over canonical bytes, and `EnsureUserFileGitIgnored` adds `.aidevtools/user.json` to `.gitignore`. The `user.json` reader and writer come with unit 5, which owns the signed approval.
- Units 3 and 4 done: `BrokerSession` starts the elevated broker on the first elevated request with one UAC prompt (`RunAsBrokerStarter`), shares one start between concurrent calls, does not re-prompt during a cooldown after a declined prompt, and refuses an untrusted install. The `elev` client calls it before sending a request, so every caller, including the MCP runner, gets it. The broker stays the executor.
