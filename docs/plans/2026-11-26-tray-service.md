# Windows service + login tray app

Branch from `wip`; PRs and merges into `wip`. Status: reviewed (Sonnet, 2026-11-26), findings folded in. Not started.

## Decisions (user, 2026-11-26)
- SCM Windows Service (session 0) hosts machine-wide state and service control; a per-user WPF tray app starts at login and talks to it over a named pipe.
- UI: WPF on .NET 10 with Syncfusion WPF controls. Host never loads Syncfusion.
- License key (user, 2026-11-26): an embedded resource file `syncfusion.license` in `AiDevHelpers.Tray`, gitignored and never committed. Locally an MSBuild target (`GenerateSyncfusionLicense`) writes it from `dotnet user-secrets` (`Syncfusion:LicenseKey`) before compile. In CI a GitHub Actions secret `SYNCFUSION_LICENSE_KEY` is written to the same file before build. Startup reads the resource and calls `SyncfusionLicenseProvider.RegisterLicense`. If the file or secret is missing: Debug builds embed an empty file (trial banner); Release/CI builds fail.
- MVVM: System.Reactive + CommunityToolkit.Mvvm. Every page binds to view-model properties fed by `IObservable<T>` from the config and from the MCP side.
- Services page: MCP service install/reinstall/uninstall/start/stop/restart/startup type, VS and VS Code extension install/reinstall, run-at-login toggle for the tray.

## Design (after review)
- The service does not replace the stdio `AiDevHelpers.MCP` server that clients launch. It supervises and exposes machine-wide state. "MCP health" = service state plus bridge manifests; there is no per-MCP-process heartbeat (dropped from `HostStatus`).
- **Instances** (Host): manifests in `%ProgramData%\AiDevHelpers\vs-instances` (`VsInstance`: Pid, SolutionPath, Ide, UpdatedUtc; VS Code writes the same shape). Repo and branch derived with `GitWorktree.RootFor/BranchFor` from SolutionPath. Start time comes from the process, not `UpdatedUtc`. Dead PIDs are filtered, never deleted (do not call `VsInstanceRegistry.ListRunning`, it deletes); PID reuse checked via process start time.
- **Config** (Tray, user context): the service runs as LocalSystem, so user-profile config would resolve to the wrong profile. The tray watches config itself with `ConfigHelper` (`ConfigReader.ReadRedacted`, `ConfigPathResolver.Resolve`) and exposes `IObservable<ConfigSnapshot>`. Watched: user Settings.json, `RepoConfig.PathFor` per open solution (from the instances stream), the `.aidevhelpers` folder. Needs a file-watcher wrapper since the helpers are one-shot.
- **Pipe security**: reuse `BridgeServer.CreatePipeSecurity()`; user SID from `UserSession.GetUserSid` (src/Helpers/WindowsSession); SYSTEM/Administrators as the existing code grants; remote clients denied; caller-SID check; max frame size; fast user switching handled (one pipe per session or SID-filtered).
- **Elevation**: pipe clients may send only typed service commands, never a script path. Install/uninstall run `scripts/service-*.ps1` through a UAC prompt from the tray, each with a committed SHA-256 pin (`gate-tpm.ps1` model, `-WritePin`). Host binary installs under `%ProgramFiles%\AiDevHelpers`, ACL'd admin-write only, and the install script verifies the binary hash before `sc create`. `update-vs-extension.ps1` is not elevated and stays unpinned; if it ever becomes elevated it needs a pin.
- **TFMs**: Host and Contracts target `net10.0-windows` (project-standards.json allows only the `-windows` suffix; Host references `AiDevHelpers.Bridge`, which multi-targets net48 and net10.0-windows).

## Layout (new, under `src/Tray/`, added to `AiDevHelpers.slnx` under a `/Tray/` folder)
| Project | Role |
|---|---|
| `AiDevHelpers.Host.Contracts` | DTOs and pipe protocol. net10.0-windows, no UI. |
| `AiDevHelpers.Host` | Worker Service (`UseWindowsService`): instance observable, service-control handler, pipe server. |
| `AiDevHelpers.Tray` | WPF app: tray icon, windows, view models, Syncfusion views, config watcher, pipe client. |
| `*.Tests` | xUnit per project; admin-requiring tests get `Category=Admin`, excluded in `.runsettings` like `Tpm`. |

## Cross-cutting rules for every unit
- Failing test first (record the red run); lite gate before commit; commit and push the unit branch; merge unit PR into `wip` without asking.
- All UI strings are `Strings.resx` keys (`string_scan`, `resx_check`, `localization_sync_check` in the lite gate).
- Docs (`docs/features.md`, README, CHANGELOG) updated in the unit that adds the feature.
- Filesystem access behind an abstraction (existing registry code uses static `System.IO`) so tests can fake it.
- Tests run via Test Explorer (`ide_run_tests` / `ide_get_test_results`).

## Units
0. **Packages and install location.** Dependency-selection check and version pins for Syncfusion, System.Reactive, CommunityToolkit.Mvvm, Microsoft.Extensions.Hosting.WindowsServices; scaffold the three projects plus tests, slnx entries; fix install path and ACL decision. License plumbing: `UserSecretsId` on the Tray project, `.gitignore` entry for `syncfusion.license`, the `GenerateSyncfusionLicense` MSBuild target, embedded-resource item, a test that the resource loads and registration is called, and a check that the file is untracked (`git ls-files` in the gate).
0b. **GitHub Actions CI.** Workflow building and testing the solution on `windows-latest` (`Category=Admin`/`Tpm` excluded); a step writes `${{ secrets.SYNCFUSION_LICENSE_KEY }}` to `src/Tray/AiDevHelpers.Tray/syncfusion.license` before build, with no echo and no artifact upload of that file; fork PRs without the secret build with an empty file (Debug) and skip Release publish. User adds the repo secret; plan reports it as a manual step.
1. **Contracts + pipe protocol.** `HostStatus`, `IdeInstanceInfo(Ide, Repo, Branch, SolutionOrFilePath, Pid, StartedUtc)`, `ServiceCommand/Result`. Length-prefixed JSON, request/response plus push. Tests: round-trip, bad frame, oversized frame, ACL/caller SID.
2. **Host instance observable.** Debounced watcher over the manifest folders, filter dead/reused PIDs, derive repo/branch. Tests with fake file system and `TestScheduler`.
3. **Host service + pipe server.** Worker Service publishing instance and status streams, handling typed service commands. Tests: subscribe gets current then updates; reconnect resumes.
4. **Service control + scripts.** `service-install/uninstall.ps1` with pins, ServiceController start/stop/restart/startup type, extension (re)install wrapper. Tests: command building, pin mismatch fails; admin tests in `Category=Admin`.
5. **Tray client library** (parallel with 2-4 after unit 1). Pipe client with `IObservable<T>` per stream, auto-reconnect, `Disconnected` state, plus the config watcher/`ConfigSnapshot` observable. Tests against a fake host and fake FS.
6. **MVVM shell.** Syncfusion theming, tray icon (running / degraded / stopped / disconnected), context menu, single-instance guard, license from the embedded resource (unit 0). UI-free view models, dispatcher scheduler, tested.
7. **Pages.** Status, IDE Instances (IDE, repo, file path), Config, Services (unit 4 commands, run-at-login via HKCU Run in the tray, no elevation), About. View-model tests per page; smoke test launches the tray in the real session via `terminal_session` per CLAUDE.md.
8. **Packaging + end-to-end + full gate.** Publish, install via Services page, verify: install service, log in, tray appears, launch VS and VS Code instances through the bridge, instances page updates, stop service shows disconnected. Run `.claude/reference/full-gate.md` before any PR to `dev`/`main`.

## Risks
- Session 0 cannot show UI; the split avoids it.
- Unlicensed Syncfusion shows a banner. The embedded key ships inside the Tray assembly (Syncfusion's intended model), so published binaries must not go public unless that is acceptable; the file itself never enters git.
- Tray and Host are separate processes, so the VS `System.Text.Json` pin applies only where the bridge assembly is loaded into VS.
