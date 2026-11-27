# Windows service, tray app, one HTTP MCP: consolidated plan

Replaces three plans, archived under `docs/plans/archived/`:
- `2026-11-26-tray-service.md` (the phase plan and its spikes)
- `2026-11-27-tray-unit6-instances.md` (unit 6 steps and status)
- `remaining-work-2026-11-19.md` (the 2026-11-19 migration of the IDE bridge; mostly done, its open items are carried below)

Branch from `wip`. At the end of a unit, the unit's work is pushed to `wip`; no PR is opened for it. Pull `wip` before the push, so the push is not rejected. Status as of 2026-11-27, after PR #129 (unit 6 first slice) merged as `f1e594e`.

## Status at a glance

| Unit | What | Status |
|---|---|---|
| 0a | WindowsSession: move, then add (console and logged-on session selection) | Done, PR 89 |
| 0b | Scaffold and pins | Done |
| 0c | Spikes A to D | A passed (with fallback); B not run; C partial; D partial (see below) |
| 0d | CI (`wip` triggers, test filters, pinned actions) | Done |
| 1a | MCP over HTTP: host and checks | Done, PR #109 |
| 1b | Shared-state fixes | Done in part; session-bound tokens dropped (see below) |
| 1c | Approval client | Superseded by wip's `ApprovalGate` (PR #109) |
| 2 | Host supervisor | Done, PR #109; session logon, logoff and console-connect handling is assumed, not tested |
| 3 | Service install and uninstall scripts | Done on `feature/tray-next`; real elevated install not run |
| 4 | Tray client library (health, service status, approval pipe) | Done; pipe hosted by unit 5 (PR #123) |
| 5 | MVVM shell and Status page | Done, PR #125; visual check and XAML localization open |
| 6 | IDE Instances page | In progress: 6.1 to 6.5 and 6.7 library merged (PR #129); page not verified on screen; open-file column, 6.9, lite gate and manual checklist open |
| 7 | Config page | Not started |
| 8 | Services page | Not started |
| 9 | Logs and log viewer | Not started |
| 10a | Updater core | Not started |
| 10b | Updater delivery | Not started; needs the user's signing key and protected environment |
| 11a | About page and per-client scoped tokens | Not started |
| 11b | Packaging, end to end, full gate | Not started |

## Open items

From the tray work:
1. Unit 5: visual check of the tray icon and windows. XAML text is localized (`XamlLocalizationTests`), in branch `fix/tray-unit5`.
2. Spike B (per-client HTTP checks: Claude Code, VS Code, Cursor, Visual Studio) and Spike C (light and dark follow, context menu, Explorer restart).
3. Unit 3: a real elevated install and uninstall of the service scripts.
4. Assumed, not tested: logoff, logon, and `terminal_session` from the service. Re-check before unit 2 ships.
5. Port: decided (2026-11-27) that the port comes from `%ProgramData%\AiDevHelpers\mcp-http\settings.json` (`{"port": N}`, default 5058). MCP.Http and the tray both read it through `McpPortSettings`. Done in `fix/tray-unit5`. An explicit `Http:Port` still overrides it on MCP.Http. Writing the file is an admin task; the Host does not write it.
6. Unit 10b: create the signing key and the protected GitHub environment (manual, by the user).
7. Cleanup: stale worktrees (`tray-merge`, `changelog-cleanup`, `tray-unit5`, `tray-unit6`); merged branches (`feature/tray-next`, `feat/tray-unit5`, `chore/changelog-tray-cleanup`, `chore/tray-unit5-strings`); the untracked `src/Tray/MCP.Http/src/Properties/` folder.
8. CI: confirm `build-and-test` on the merged unit 5 branch, which was still running at last check.

Carried from `remaining-work-2026-11-19.md` (its other items are done):
9. Publish on a `v*` tag: confirm the publish job pushes to the feed with `GITHUB_TOKEN` (not tested).
10. Verify the bridge end to end against a live Visual Studio: `IdeBridgeHealth` and `IdeListInstances` returned no instances at that time.

## Decisions

1. SCM Windows service (LocalSystem) supervises one HTTP MCP and applies updates. A per-user WPF tray app starts at logon. The service stays; spike A decides it, and it passed with fallback.
2. One HTTP MCP (`AiDevHelpers.MCP.Http`, streamable HTTP) serves every IDE and chat client. The stdio exe stays.
3. UI is plain WPF on .NET 10 with the Fluent theme (`ThemeMode`). Tray icon via `H.NotifyIcon.Wpf`. Syncfusion dropped.
4. MVVM with System.Reactive and CommunityToolkit.Mvvm. Every page binds to view-model properties fed by `IObservable<T>`.
5. Pages: Status, IDE Instances, Config, Services, Logs, About.
6. The updater is ported from VideoForensics: one staging folder; sources are a debug stage script and a GitHub release package.
7. Dangerous tools over HTTP (`terminal_session`, `script_run`, `run_elevated_script`, `ide_kill_*`, process kill) need a tray confirmation per call. Stdio is unchanged.
8. No Host pipe. The tray reads service state from SCM, and MCP health from `/healthz` and the status file. There is no bearer token (2026-11-27): the listener is loopback only.
9. Instances and config are read in the tray, in user context, never by the SYSTEM service. Manifests are untrusted input (see unit 6).
10. The service does two things only: launch and supervise MCP.Http in the console user's session (or the first logged-on session), and apply signed updates.
11. Unit 6: reuse `Bridge`'s instance registry and `GitWorktree`; no second manifest reader.
12. Unit 6: the page is read-only for now; close and reset (6.9) are allowed, pending a safety review.

## Design constraints (kept from the phase plan)

HTTP MCP:
- `ModelContextProtocol.AspNetCore` 2.4.0. **Stateful** sessions are mandatory (`Stateless = false`); without them no session id is issued. The SDK marks stateful mode and `IdleTimeout` obsolete (SEP-2567), so the warning is suppressed with a comment until the SDK offers a replacement.
- Bind `127.0.0.1`, fixed port 5058. Validate `Host` and `Origin`; no CORS. Sessions expire after 30 minutes idle and do not survive a restart.
- Port conflict: the cause goes to `%ProgramData%\AiDevHelpers\mcp-http\status.json` and `/healthz` cannot answer; the tray reads the file.
- Stdio and HTTP share `ServerControl.StatePath`, the terminal registry and the manifests. Event buffers are per process.

Approval (tray confirmation):
- Each request carries a nonce and a hash of tool plus arguments; the tray's reply must echo both. Deny on timeout (30 s), on a missing tray, or on any error. Fail closed.
- Tray is the pipe server: first instance, current user only, client pid check. Concurrent prompts queue; a locked screen defers the prompt until unlock, then it times out.
- Classification is deny-by-default (`[SafeTool]` / `[DangerousTool]`); a test fails for an unclassified tool.
- Known limit: image-path checks do not stop same-user spoofing. The per-call confirmation is the real control.

Shared state (audit):
- Static per process: `EventSubscriptions`, `ErrorTools.Services`, localization stores, `TestHostTools.Plans`, `TestingState`, `ManagedProcessRegistry`, `LiveRuns`, `TerminalSessionManager`, `SessionRegistry`. Shared by design.
- `ControlSession` keeps a set of servers and broadcasts pause, close and repo notices.
- Plan-then-confirm tokens are not bound to a session. They are random, single use and expire in 15 minutes, and only the planning client gets one. Binding would need an `McpServer` parameter in about ten tools for no practical gain (dropped in 1b).

Service and updater:
- Install under `%ProgramFiles%\AiDevHelpers` with an admin-only ACL, created before copy. The install script refuses a user-owned path. `scripts/service-*.ps1` carry committed SHA-256 pins with `-WritePin`.
- The service applies updates only from a SYSTEM-owned copy of a verified package, with `FILE_SHARE_NONE`. Reparse checks repeat at apply. Dev packages are honored only in debug builds and only from machine-only config.
- Signed manifest: monotonic version, `notAfter`, hashes of every file, `manifest.json.sig`. Public-key list for rotation. Downgrade rejected.
- Drain on update: `paused`, wait for in-flight calls, then `closing`.
- Port rules: record the VideoForensics commit in `PORTED.md`; a test pins verifier behavior.

Spike results (recorded, condensed):
- **A passed (2026-11-27):** the service launches MCP.Http in the user's session (RDP session 3 as the user, not elevated); IDE pipes reachable; lock survived. The console session had no user over RDP, so the supervisor falls back to the first logged-on session. Not tested: logoff, logon, `terminal_session` from the service.
- **B not run.** HTTP stays opt-in per client until verified. Claude Desktop uses stdio or `mcp-remote`.
- **C partial:** `ThemeMode.System` applies; the icon is created on .NET 10. `DispatcherScheduler` is absent for the plain `net10.0-windows` TFM, so view models use `ObserveOn(SynchronizationContextScheduler)` over the dispatcher. Not checked: theme follow, menu, Explorer restart.
- **D partial:** two clients get distinct session ids; a bad session id gets 404. The plan-confirm collision is covered by the audit and tests, not reproduced live.

Layout:
| Project | Role |
|---|---|
| `AiDevHelpers.MCP.Http` | ASP.NET Core exe, streamable HTTP, `/healthz`, status file, session counter |
| `AiDevHelpers.Host` | Worker Service (`UseWindowsService`): MCP supervisor, update apply |
| `AiDevHelpers.Updates` | Ported updater (unit 10a) |
| `AiDevHelpers.Tray` | WPF tray app: icon, pages, view models, readers, approval pipe host |
| `*.Tests` | xUnit per project; real Win32, SCM and UAC tests are `Category=Admin` or `Category=Manual`, excluded like `Tpm`, with elevated evidence attached to the unit PR |

Rules for every unit:
- A failing test or a verifiable done-state named up front; red run recorded; lite gate before commit; commit; at the end of the unit, pull `wip`, then push to `wip`. Do not open a PR for a unit.
- All UI strings in `Strings.resx`; `string_scan`, `resx_check`, `localization_sync_check`. UIA names, high contrast, per-monitor v2 DPI.
- Docs per unit: `docs/features.md`, CHANGELOG, and the client and troubleshooting docs for HTTP (`docs/mcp-http-clients.md`). Check `docs/ide-bridge.md` and `conformance/` for protocol impact.
- Supportability: a diagnostics bundle (tray, service, MCP and IDE extension versions, port, pid, redacted last lines, service state and recovery settings), from a tray button and `/healthz`; version-skew warning across stdio, HTTP and extensions. Unit 6 adds the instance list when the bundle lands.
- UI units with no automated done-state carry a manual checklist with screenshots.

## Unit detail (open units and parts still open)

### Unit 5 remaining
- Visual check of the icon, menu and windows; XAML localization.

### Unit 6: IDE Instances page

Done: 6.1 Bridge seam (`VsInstanceRegistry.ListRunning(directory, isRunning)`) and `InstanceFeed`; 6.2 `InstanceValidator` (pid, IDE, pipe name, path); 6.3 repo and branch (`GitWorktree`); 6.4 start time and user (`InstanceProcessFacts`, passed as functions); 6.5 `InstancesViewModel`; 6.6 read-only tab in the status window (built, not verified on screen); 6.7 `InstanceEventsClient` and `OpenFileTracker` (library only); 6.8 CHANGELOG and features. Tests: Tray 48, Bridge 105, MCP Core 403.

As built, differing from the earlier steps: the seams are functions and static methods, not interfaces; `Problem` returns a reason string, not a result type; the events client is static with `RunOnceAsync` and `RunAsync` (backoff 2 s to 30 s). The events field names are checked: Visual Studio sends `file`, VS Code sends `path`.

Remaining:
1. 6.7 wiring: one events subscription per live instance, started and stopped with the instance list, feeding `OpenFileTracker` into an open-file column. Test with the fake pipe server.
2. 6.6 manual checklist with screenshots: both IDEs appear within 5 s; a branch switch shows within 5 s; closing a window removes its row; dark and light theme and 150% DPI; keyboard only, with UIA names on each column.
3. 6.9 close and reset: held until the safety review is written here. Its requirements: refuse on unsaved documents unless the person confirms a discard; a deny confirmation in the tray dialog naming the instance; reuse the bridge actions through `IdeInstanceRouter` (no second path); refuse while a build or debug session runs. Kill is not offered.
4. Lite gate: its IDE steps need an open Visual Studio on `AiDevHelpers.slnx`. Run them before the unit's work is pushed to `wip`.
5. Diagnostics bundle: add the instance list when unit 9 or the supportability work lands.

### Units 7 to 11b (not started, unchanged from the phase plan)
- **7 Config page:** file watcher over `ConfigHelper`; `IObservable<ConfigSnapshot>` for user settings, `RepoConfig.PathFor` per open solution, and `.aidevhelpers`; secrets masked.
- **8 Services page:** install, reinstall, uninstall, start, stop, restart and startup type through the unit 3 scripts and UAC; VS and VS Code extension install through `update-vs-extension.ps1` (one installer run, all VS closed); run-at-login toggle (HKCU Run, no elevation). Update buttons come after unit 10b.
- **9 Logs:** file sink to `%ProgramData%\AiDevHelpers\logs\{host,mcp,tray}-*.log` with bounded retention, plus the Event Log for the service; read through `LogHelper`. Channels: Host, MCP, Tray, Event Log, IDE `LogRoots` (allow-listed, reparse points rejected). Plain text, CR and LF neutralized, lines capped. Targets: 100k lines in under 1 s, under 200 MB.
- **10a Updater core:** verifier, guard, applier, key list, `UpdatesFolderAcl`, `PORTED.md`. Tests: tampered or unsigned rejected; traversal, rollback, downgrade and replay rejected; key rotation.
- **10b Updater delivery:** stager scripts, GitHub downloader, service watcher with drain, release workflow with a protected environment. Needs the user's signing key.
- **11a About and scoped tokens:** version, build, components, links; per-client scoped tokens.
- **11b Packaging and full gate:** install the service, log in, tray appears, launch VS and VS Code through the bridge, instances update, stage and apply an update, stop the service and see disconnected, uninstall back to stdio-only. Run `.claude/reference/full-gate.md` before any PR to `dev` or `main`.

## Decided against reviewer suggestions

- Tray-only, without the SCM service, and reusing `update-aidevtools.ps1`: the user kept the service and the ported updater. Spike A can still reverse the service.
