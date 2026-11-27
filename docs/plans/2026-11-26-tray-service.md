# Windows service + login tray app

Branch from `wip`; PRs and merges into `wip`. Status: reviewed twice (Sonnet, 2026-11-26), findings folded in. Not started.

## Decisions (user, 2026-11-26)
- SCM Windows Service (session 0) hosts machine-wide state and service control; a per-user WPF tray app starts at login and talks to it over a named pipe.
- UI: plain WPF on .NET 10 with the built-in Fluent theme (`ThemeMode`, follows system light/dark/high contrast). Syncfusion dropped after review (user, 2026-11-26): no license key, embedded resource or license CI secret. Tray icon via `H.NotifyIcon.Wpf` (pinned in unit 0).
- MVVM: System.Reactive + CommunityToolkit.Mvvm. Every page binds to view-model properties fed by `IObservable<T>` from the config and from the MCP side.
- Services page: MCP service install/reinstall/uninstall/start/stop/restart/startup type, VS and VS Code extension install/reinstall, run-at-login toggle for the tray, update check/install.
- Service and ported updater kept (user, 2026-11-26), despite the reviewer's tray-only suggestion.
- Updater: port the VideoForensics updater (`src/client/host/VideoForensics.Hosting/src/Updates/` plus `UpdatesFolderAcl`). One staging folder, two sources: a debug stage script and a GitHub release package.

## Design (after review)
- **One HTTP MCP for all clients (user, 2026-11-26).** A new `AiDevHelpers.MCP.Http` ASP.NET Core exe serves the existing MCP tools (`AddAiDevHelpersMcp`) over streamable HTTP with `ModelContextProtocol.AspNetCore`; one instance serves every IDE and chat client. The LocalSystem service launches and supervises it **in the logged-on user's session** (via `UserSession` in `src/Helpers/WindowsSession`: token, `CreateProcessAsUser`), restarts it on logon/logoff, crash and update. Tools therefore run in the user's context (profile, IDE pipes, UAC for elevated scripts as today). The stdio `AiDevHelpers.MCP` exe stays for clients that need it; both use the same Core.
- **HTTP hardening.** Bind `127.0.0.1` only on a fixed default port (configurable); validate `Host` and `Origin` (DNS-rebinding defense); require a bearer token generated at first run, stored in a user-only-ACL file, rotatable from the tray; no CORS. Client config snippets for Claude Code, VS, VS Code, and Claude Desktop (via `mcp-remote`-style bridge if it cannot send headers) go in docs.
- **Shared-instance state.** Tools with process-global or per-client state (`terminal_session`, `ide_subscribe_events`/`ide_read_events`, bookmarks, error snapshots) must be scoped per MCP session or documented as shared. Audit in unit 1a; tested with two concurrent clients.
- **MCP health** is real: the Host polls the MCP process (`/healthz` on the loopback port plus process state) and publishes it in `HostStatus`; the tray's status icon and pages observe it. Active-client count and tool-call activity are published on a status stream by the MCP process (Host relays).
- **Instances** (Host): manifests in `%ProgramData%\AiDevHelpers\vs-instances` (`VsInstance`: Pid, SolutionPath, Ide, UpdatedUtc; VS Code writes the same shape). Repo/branch from `GitWorktree.RootFor/BranchFor`. Start time from the process. Dead PIDs are filtered, never deleted (not `VsInstanceRegistry.ListRunning`); PID reuse checked by process start time.
- **Config** (Tray, user context): the service runs as LocalSystem, so user-profile config would resolve to the wrong profile. The tray watches config with `ConfigHelper` and exposes `IObservable<ConfigSnapshot>`: user Settings.json, `RepoConfig.PathFor` per open solution, the `.aidevhelpers` folder. Needs a file-watcher wrapper (helpers are one-shot).
- **Pipe security**: reuse `BridgeServer.CreatePipeSecurity()`; user SID from `UserSession.GetUserSid`; SYSTEM/Administrators as existing code grants; remote clients denied; caller-SID check; max frame size; fast user switching handled. The protocol carries a **version field**; the tray shows "restart for update" when versions differ.
- **Elevation**: pipe clients send only typed service commands, never a script path. Install/uninstall run `scripts/service-*.ps1` through a UAC prompt from the tray, each with a committed SHA-256 pin (`gate-tpm.ps1` model). The Host installs under `%ProgramFiles%\AiDevHelpers`, admin-write ACL. The install script verifies the Host binary against the **signed update manifest hash** (not a fixed pin, since updates replace the binary) and sets service **recovery actions (restart on failure)**, which the update-exit model relies on.
- **Tray and Host ship in one signed package.** The apply step renames locked files to `.old-*`; a running tray keeps old code until restarted, and `.old-*` files are cleaned on next start.
- **TFMs**: Host and Contracts `net10.0-windows` (project-standards.json); Host references `AiDevHelpers.Bridge` (multi-targets net48 and net10.0-windows).

## Updater hardening (from review)
- `allowDevUpdatePackages` lives in machine-only config (HKLM or ProgramData with admin-write ACL), never user config.
- Dev stage script elevates, or copies dev packages into an admin-only folder **before** verify, removing the TOCTOU between verify and copy as SYSTEM. Reparse-point checks repeat at apply time.
- Port cleanly: replace VideoForensics-only types (`IUpdateInstaller`, `GitHubReleaseAsset`, `UpdateCheckService`, `DefaultKeepList`) with local ones; keep-list is AiDevHelpers-specific. Signature lives in the package as `manifest.json.sig` (matches the verifier), not a separate release asset. Port `build-update-package` as a PowerShell script (`scripts/build-update-package.ps1`) that writes the manifest and signs it.
- New dependency BouncyCastle: dependency-selection check in unit 0.
- Signing key: private key in a GitHub **environment** secret (`UPDATE_SIGNING_KEY`) with required reviewers, so a tag pusher alone cannot sign. Rotation: verifier accepts a list of public keys embedded in the Host; revocation by shipping a package that removes a key. Download has a size cap and HTTPS only; downgrade rejected (newer-than-current check kept).

## Layout (new, under `src/Tray/`, added to `AiDevHelpers.slnx` under `/Tray/`)
| Project | Role |
|---|---|
| `AiDevHelpers.Host.Contracts` | DTOs, pipe protocol (versioned). `net10.0-windows`, no UI. |
| `AiDevHelpers.MCP.Http` | ASP.NET Core exe: streamable-HTTP MCP, loopback + token + Origin checks, `/healthz`, status stream. Runs in the user session. |
| `AiDevHelpers.Host` | Worker Service (`UseWindowsService`): launches/supervises MCP.Http in the user session, instances, service control, pipe server, update watcher. |
| `AiDevHelpers.Updates` | Ported updater: verifier, guard, applier, stager, downloader. |
| `AiDevHelpers.Tray` | WPF app: tray icon, windows, view models, views, config watcher, pipe client. |
| `*.Tests` | xUnit per project; admin-requiring tests `Category=Admin`, excluded in `.runsettings` like `Tpm`. |

## Cross-cutting rules for every unit
- Failing test first (record the red run); lite gate before commit; commit and push the unit branch; merge unit PR into `wip` without asking.
- All UI strings are `Strings.resx` keys (`string_scan`, `resx_check`, `localization_sync_check`).
- Docs (`docs/features.md`, README, CHANGELOG) updated in the unit that adds the feature.
- Filesystem and pipe behind interfaces so tests can fake them; a protocol-version test.
- Accessibility: UIA names on all controls, high contrast, per-monitor v2 DPI.
- The `UPDATE_SIGNING_KEY` CI secret is a manual step for the user; the plan reports it when unit 4b needs it. It passes through `env:` only; no `obj/` upload; the crash-evidence `**/*.dmp` upload is limited so a dump cannot leak it.

## Units
0. **Packages and scaffolding.** Dependency-selection check and pins (System.Reactive, CommunityToolkit.Mvvm, H.NotifyIcon.Wpf, Hosting.WindowsServices, BouncyCastle); scaffold the projects plus tests and slnx entries; fix install path and ACL.
0b. **CI.** Extend the existing `.github/workflows/ci.yml` (windows-latest, PR plus `v*` tag), not a second workflow, to build and test the new projects; Admin/Tpm tests excluded.
1a. **MCP over HTTP.** `AiDevHelpers.MCP.Http` with `ModelContextProtocol.AspNetCore` (version pinned like Core's 2.4.0), loopback bind, Host/Origin validation, bearer-token middleware, `/healthz`. Audit tools for global state and scope or document it. Tests: unauthenticated and bad-Origin requests rejected, two concurrent clients isolated, tool call round-trip over HTTP. Docs: client config for each IDE/chat client.
1. **Contracts + pipe protocol.** `HostStatus`, `IdeInstanceInfo(Ide, Repo, Branch, SolutionOrFilePath, Pid, StartedUtc)`, `ServiceCommand/Result`, version field. Length-prefixed JSON, request/response plus push. Tests: round-trip, bad frame, oversized frame, ACL/caller SID, version mismatch.
2. **Host instance observable.** Debounced watcher, dead/reused-PID filter, repo/branch derivation. Tests with fake FS and `TestScheduler`.
3. **Host service + pipe server + MCP supervisor.** Worker Service; instance and status streams; typed commands; starts MCP.Http in the active user session (session-change events), restarts on crash with backoff, health polling, token file creation with user-only ACL. Tests: current-then-updates; reconnect resumes; supervisor restarts a killed child; no user session means MCP stopped, not an error.
4. **Service control + scripts.** `service-install/uninstall.ps1` with pins, manifest-hash binary check, recovery actions; start/stop/restart/startup type; extension (re)install wrapper; uninstall cleanup (service, Run key, ProgramData, ACLs). Admin tests in `Category=Admin`.
4a. **Updater core.** `AiDevHelpers.Updates`: verifier, path guard (recheck at apply), applier, key list, `UpdatesFolderAcl`, machine-only dev flag. Depends on 3 and 4. Tests: tampered/unsigned rejected, traversal, rollback on failure, downgrade rejected, key rotation.
4b. **Stager and downloader.** `scripts/stage-update.ps1` (elevating), `scripts/build-update-package.ps1`, GitHub release downloader (size cap, HTTPS), Host watcher with drain and restart. New release workflow on tag with protected environment for the signing key.
4c. **Update UI hooks.** Pipe commands and status for "check / install staged update", tray "restart for update" prompt. Folded into unit 7 Services page.
5. **Tray client library** (can start after unit 1). Pipe client `IObservable<T>` per stream, auto-reconnect, `Disconnected` state, plus the config watcher/`ConfigSnapshot`.
6. **MVVM shell.** Fluent theme, tray icon (running / degraded / stopped / disconnected), context menu, single-instance guard, crash-loop guard, toast notifications for state changes.
7. **Pages.** Status (also MCP endpoint URL, copy-token and rotate-token, active clients and recent tool calls), IDE Instances (IDE, repo, file path), Config, Services (unit 4/4c commands, run-at-login via HKCU Run in the tray, no elevation), Logs (reuse `LogHelper`/`ErrorHelper`), About (version, build, license, links). View-model tests per page; smoke test launches the tray in the real session via `terminal_session`.
8. **Packaging + end-to-end + full gate.** Publish, install via Services page, verify: install service, log in, tray appears, launch VS and VS Code instances through the bridge, instances page updates, stage and apply an update, stop service shows disconnected. Run `.claude/reference/full-gate.md` before any PR to `dev`/`main`.

## Decided against reviewer suggestions
- Tray-only first (no SCM service) and reusing `update-aidevtools.ps1` instead of a new updater: user kept the service and ported updater.
