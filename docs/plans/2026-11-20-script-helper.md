# Script Helper, 2026-11-20

Status: revised after the Sonnet review (all 10 findings folded in). Awaiting approval. New project inside McpBridge at `src/ScriptHelper/`.

Purpose: run allowlisted debug scripts elevated through one hidden, long-lived broker that is started with a single UAC prompt.

## Decisions

- D1 Project location: `src/ScriptHelper/` with `src/` (library `AiDevHelpers.ScriptHelper.Core`, then broker and client) and `tests/` (xunit, `AiDevHelpers.ScriptHelper.Core.Tests`), matching the repo's per-helper layout. Requires an NTFS volume.
- D2 Enforcement: hash-pinned. The broker copies each requested script into an admin-only staging directory, hashes the copy, and executes that copy, so a user-writable file cannot be swapped between check and run. The approved hash covers the script, the interpreter path and version, and every file it dot-sources or imports. A changed item is denied until re-approved.
- D3 Storage: everything lives under `%ProgramData%\AiDevHelpers\script-helper\` (see D9). One approvals file per project in `approvals\<project-id>.json`, holding that project's approved scripts, the hash of its `.elevated.json`, and a version counter. The registry (`projects.json`) holds project roots only, no hashes. Ownership and ACLs: the `script-helper` folder and everything under it are writable by Administrators only, with users read-only at most. The shared `AiDevHelpers` root is not locked down, because other components (such as the Bridge's `vs-instances`) write there as normal users.
- D4 Project allowlist: `<repo>\.elevated.json` lists script paths (relative to the repo root) and argument patterns. Deny by default. `*` is the only wildcard and never matches quotes, `;`, `|`, `&` or other shell metacharacters. The `.elevated.json` hash is pinned in the approvals file at approve time, so any edit to the allowlist or argument patterns needs re-approval. Arguments are passed as an argv array with no shell.
- D5 TPM in v1 with fallback: when a TPM 2.0 is present and enabled, approvals files are signed with a non-exportable key in the Microsoft Platform Crypto Provider (CNG). The key's ACL grants sign and use to Administrators only. The signed payload covers the file content, the project ID, the project root path, and a monotonic version counter. The last-seen counter is stored in the TPM (NV index) or a protected file, so a replayed older approvals file is denied. When no TPM is present or enabled, the broker uses ACL-only protection, but only if started with an explicit `--allow-no-tpm` flag. The mode is stored out of band in an admin-only file, not in the approvals file, and the broker never downgrades to ACL-only on a machine where a TPM was previously seen (it denies instead). A warning is logged at each start. This machine has TPM 2.0 (PnP device `Trusted Platform Module 2.0`, status OK).
- D6 Transport: each user has a request subfolder under `%ProgramData%\AiDevHelpers\script-helper\queue\<user-SID>\`, ACL'd so only that SID can write, and the broker can read and write results. Request IDs are unguessable GUIDs. Files are created with CreateNew (no overwrite, no reparse-point following). The broker checks the file owner SID against the project's registered user. Results go to `results\<SID>\<request-id>.json`, readable only by that SID.
- D7 Elevation: the broker starts hidden via `Start-Process -Verb RunAs -WindowStyle Hidden`. One UAC per broker start. Setup commands (register project, approve script) each need an elevated run, and approve shows the script path and hash and requires interactive confirmation.
- D8 Broker lifecycle: the client finds a running broker through a named mutex with an admin-only ACL and a fixed pipe name. The client never spawns a broker itself. The broker has an idle timeout, a `stop` command, and crash recovery (stale queue entries are marked failed on start). The broker binary lives in an admin-only install directory, not in a user-writable repo path, and the client verifies the broker's signed binary path before sending requests.

- D9 Shared code placement: interfaces live in `AiDevHelpers.Common` (`Contracts`): `IApprovalSigner`, `IElevationCheck`, `IVolumeInfo`, `IPathInspector`, `IInterpreterInfo`, `IConfirmer`, `ISetupCommands`, and the `SetupResult` record. The config repository lives in the new `AiDevHelpers.Core` library (`src/Core`): `ConfigRepository` resolves `%ProgramData%\AiDevHelpers` and exposes a `ScriptHelper` element (`ScriptHelperConfig`) for the registry, approvals, versions, mode file and key name. Implementations stay in `AiDevHelpers.ScriptHelper.Core`. The Script Helper projects target `net10.0-windows`, like Common.

- D10 Approval without a console (decided 2026-11-20): a calling AI can ask for an approval in two steps. `approve --prepare` returns the labelled prompt plus the fingerprint and allowlist hash, and writes nothing. The AI must show that prompt to the person and ask them with the question form. Then `approve --confirm <fingerprint> <allowlistHash>` writes the approval only if the script and allowlist still hash to those values, so a script changed after the person looked is refused. This does not trust the AI's answer: the gate is that the person saw the exact values, and any change to the script or allowlist between the two steps fails. The console question (`approve` with no flags) stays as the stronger, person-only path. Phase 5 (the broker) may later add a host-UI elicitation with a one-time ticket if the AI-in-the-middle path proves too weak; that is not built.

## Test-first rule (applies to every phase)

Each unit follows red, green, refactor:
1. Write the failing tests for the acceptance criteria and run them to confirm they fail for the expected reason.
2. Write the minimum code to pass.
3. Refactor with the tests green.

No production code is written before its failing test exists. Each phase's gate is the full test suite plus the phase's acceptance check. UAC and ACL checks are manual and listed separately from the unit gate.

## Open questions

None blocking.

## Phases (each ends with a review gate)

### Phase 1: Allowlist model (pure logic, no I/O)
- Tests first: path resolution stays inside the project root; rejects `..`, absolute paths outside the root, and reparse points (junctions, symlinks) in any path segment; canonicalizes with the final resolved path, so 8.3 short names, case differences and UNC paths resolve to the same entry; pattern match on script paths; argument pattern match; `*` does not match quotes or shell metacharacters; default deny.
- Acceptance: a request for a path outside the root, a reparse-point path, an unlisted path, or unmatched arguments is denied; a listed path with matching arguments is allowed.
- Gate: unit tests pass.

### Phase 2: Hashing and approvals store
- Tests first: SHA-256 of a script matches a known value; the hash covers dot-sourced and imported files and the interpreter; approvals load/save round trip; a changed hash denies; a changed `.elevated.json` hash denies; a missing or malformed approvals file denies; an older approvals file with a lower version counter is denied (replay); a file moved between projects is denied (project ID binding).
- Signing behind an interface (`IApprovalSigner`) so tests use a fake signer.
- Acceptance: an approved, unchanged script allows; an edited script or allowlist denies with a "re-approval needed" reason.
- Review fixes folded in: explicit canonical payload with a version tag and length-prefixed fields; path normalization (separators, `.`/`..`, trailing dots and spaces, case); ProjectRoot compared; fail-closed reads (size cap, I/O errors, null fields); atomic save; length-prefixed fingerprint fields; duplicates rejected.
- Deferred: the replay high-water mark needs a persisted counter (Phase 3, with the TPM NV index or a protected file). ACLs and reparse-point checks on the approvals directory land in Phase 4 with setup commands.
- Gate: unit tests pass (55 at the time of writing).

### Phase 3: TPM signing and fallback
- Tests first (fake CNG wrapper): sign then verify succeeds; tampered payload fails; unsigned file denies when a TPM is present; the fallback runs only with `--allow-no-tpm`; a machine previously seen with a TPM denies rather than downgrading; mode is read from the out-of-band admin-only file, not the approvals file.
- Then a real-TPM integration test, marked so it runs only on demand (`[Trait("Category","Tpm")]`).
- Acceptance: with a TPM, the key is created non-exportable with an ACL granting use to Administrators only. Without a TPM and with `--allow-no-tpm`, the broker runs with ACL-only protection and logs it.
- Gate: unit tests pass for both modes; the TPM integration test passes on this machine.
- Status: unit tests pass (73) and both Tpm-trait tests pass on this machine (Infineon TPM 2.0, run from an elevated tab). The earlier "Access denied" was the "Security Descr" key property needing the DACL_SECURITY_INFORMATION flag (4) instead of None; probed with five creation variants.
- Manual check (not a unit test): an unelevated process cannot use the signing key. Verified by attempting to sign from a non-elevated session and confirming access denied.

### Phase 4: Setup commands (register and approve)
- Moved before the broker so the broker's manual gate has an approved script to run.
- Tests first: register adds a project root and rejects roots that are not on NTFS or are user-profile roots by default; approve writes a signed approvals entry, pins the `.elevated.json` hash, and requires confirmation; both refuse when not elevated.
- Acceptance: approve shows the script path and hash and needs interactive confirmation before writing.
- Gate: unit tests pass.
- Manual: one UAC for each setup run.
- Status: code and unit tests done (158 passing, test-first). Built: `ManifestParser`, `Allowlist.IsPathListed`, `DependencyScanner`, `AdminOnlyDirectory` (ACL), `ProjectRegistry`, `FingerprintBuilder` (script, dot-sourced and imported files, interpreter; shared with the broker), `ApprovalStore.TryLoad`, `SetupCommands` (register, approve with confirmation, pinned allowlist hash, version counter), `SetupCli`, and the `scripthelper-setup` exe (`src/ScriptHelper/setup`), which picks TPM or ACL-only mode through `ModeResolver`. Added to `AiDevHelpers.slnx`.
- Manual gate done (2026-11-20, elevated tab, Infineon TPM 2.0): `scripthelper-setup register` and `approve` on a scratch project. `approve` showed the script, fingerprint and allowlist hash and waited for a typed `y`. Results: `%ProgramData%\AiDevHelpers\script-helper` created with SYSTEM and Administrators full control and Users read-only (not inherited); the shared `AiDevHelpers` root ACL unchanged; `mode.txt` = `tpm`; the approvals file is version 1 with a 128-character TPM signature; the version counter is 1; an unelevated write into the folder is denied. This also exercised `PwshInterpreterInfo` and `ConsoleConfirmer` and the real ACL.
- Not yet done: a full-solution build, and unit tests for `PwshInterpreterInfo` and `ConsoleConfirmer`. Installing the exe to an admin-only directory is Phase 5 (D8). The work branch `script-helper` is committed locally but not pushed (the push was denied by the auto-mode classifier).

### Phase 5: Broker
- Health check: the broker answers a `ping` request with its PID, protection mode and version. The client never sends a script until a ping succeeds within a short timeout (default 3 s), so a missing or hung broker fails fast instead of hanging.
- Tests first (fake queue directory and fake process runner): unlisted request denied and recorded; allowed request runs from the staged copy and writes stdout, stderr and exit code; a script swapped between check and run is denied (copy-and-hash test); malformed request denied without crashing the loop; a process exceeding the timeout is killed with its child tree; stdout over the size cap is truncated; requests from a SID not registered to the project are denied.
- Lifecycle tests: single instance via the named mutex; idle timeout; stop command; stale queue entries marked failed on start.
- Audit log: every request and decision is appended to `%ProgramData%\AiDevHelpers\script-helper\logs\`, admin-only.
- Acceptance: processes requests in order, runs hidden (process started with CreateNoWindow), and runs only staged, approved copies.
- Gate: unit tests pass; manual run with one approved script; manual check that the window stays hidden.
- Status (unit 1 of 3 done, 2026-11-21): `RequestHandler` decides and runs one request: registration, allowlist (path and arguments), copy-and-hash staging of the whole approved file set, approval check against the staged bytes, run of the staged copy only, staged copy removed after, every decision audited. Tests: 9 (red first on stubs). 204 Script Helper tests pass.
- Unit 2 done (2026-11-21): `ProcessScriptRunner` runs pwsh with no shell (argument list), `CreateNoWindow`, output capped at 64 KB with a truncation marker, and a timeout that kills the whole process tree. Tests: 6, red first on the stub, including a child-process kill check. 210 Script Helper tests pass. The hidden window is set in code but not yet confirmed by eye.
- Unit 3 done (2026-11-21): the registry records the owner SID (the elevated user's SID, which is the same account under UAC). `RequestHandler` refuses any request whose requester SID is missing or differs from the owner (case-insensitive). Tests: 5 owner tests, red first on the record and the check. 216 Script Helper tests pass. A project registered before this change has no owner and is refused until it is registered again (no compatibility code, per the rule).
- Not yet done in Phase 5: (3) the queue, the named mutex, ping, idle timeout, stop, and the broker exe. The earlier note about the SID check is now done. (the registry does not record an owner SID yet), the queue folder, the named mutex, ping, idle timeout, stop, stale-entry recovery, the audit log file, and the hidden-window manual check. The broker host exe does not exist yet.

### Phase 6: Client
- Tests first: `elev <script> [args]` pings the broker first and stops with a clear message if the ping fails; then it writes a request under its SID's queue folder, waits for the result with a timeout, and returns the script's exit code; the client refuses to talk to a broker whose binary path is not in the admin-only install directory.
- Same rule for ad-hoc elevated work: open the elevated shell, confirm it is elevated and responsive (for example `whoami /groups` shows High Mandatory Level), then run the script. Never send a script to a shell that has not answered.
- Acceptance: exit codes pass through; a denied request reports the reason.
- Gate: unit tests pass.

### Phase 7: Docs and McpBridge wiring
- Tests first where code changes; docs are reviewed.
- Acceptance: README in `src/ScriptHelper/` covers setup, the `.elevated.json` format, the lifecycle, and the threat model (accidental or out-of-scope elevation is blocked; a determined process running as the user is not, and `--allow-no-tpm` lowers the protection).
- Gate: review gate and full repo build.
