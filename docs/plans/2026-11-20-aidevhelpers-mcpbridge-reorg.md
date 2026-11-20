# AiDevHelpers.McpBridge reorg and rename, 2026-11-20

Status: executed. Phases A–C done, initial commit pushed, old repo archived. Open items at the end. New helpers (ErrorHelper, TestingHelper, BookmarkHelper, ConfigHelper, ExtensionsHelper) are a separate follow-up plan.

## Approved decisions

- New repo `AiDevHelpers/McpBridge` (public). CI on `main` and `dev`. Local clone `F:\source\McpBridge`.
- Commit the old repo first (`91162c8` on `codex/record-ci-protection`), then copy the committed state. Fresh history, one initial commit (`63ab044`), no filter-repo.
- Scope: reorg and rename only.
- Q1 NuGet id `AiDevHelpers.Bridge` (rename).
- Q2 WindowsSession at `src/Helpers/WindowsSession`.
- Q3 per-area `src/` and `tests/`; sub-folder per project for MCP and Installers.
- Q4 rename wire and runtime identifiers now (pipes, ProgramData folder, MCP name, display text).
- Q5 change VSIX Identity and publisher. New Id `AiDevHelpers.Installers.Vs2026.Installer.A2CE85F0-DEB4-4524-93AE-7854F1FC31FB`, publisher `AiDevHelpers`.
- Q6 public repo; CI on `main` and `dev`; archive the old repo (confirmed separately, done).
- Q7 move `vscode/` as-is into `src/Installers/VsCode/Installer`.
- Q8 untrack and ignore the generated vsixmanifests; force-add the two git-ignored VSIX sources.
- Q9 copy `docs/plans` and `docs/Host-MCP` with a historical banner.
- Q10 one smoke test per project that had no tests.
- Q11 pack and publish only `AiDevHelpers.Bridge` and `AiDevHelpers.MCP.Core` to GitHub Packages (`AiDevHelpers`).
- Q12 local clone at `F:\source\McpBridge`.

## Layout as built

| Area | Projects (src / tests) |
|---|---|
| Common | `AiDevHelpers.Common` / `AiDevHelpers.Common.Tests` |
| Bridge | `AiDevHelpers.Bridge` (net48;net10.0-windows) / `AiDevHelpers.Bridge.Tests` |
| Helpers/LogHelper | `AiDevHelpers.Helpers.LogHelper` / `.Tests` |
| Helpers/TerminalControl | `AiDevHelpers.Helpers.TerminalControl` / `.Tests` |
| Helpers/WindowsSession | `AiDevHelpers.Helpers.WindowsSession` / `.Tests` |
| MCP/Core (NuGet library) | `AiDevHelpers.MCP.Core` / `AiDevHelpers.MCP.Core.Tests` |
| MCP/Server (exe) | `AiDevHelpers.MCP` / `AiDevHelpers.MCP.Tests` |
| Installers/Vs2026/Core | `AiDevHelpers.Installers.Vs2026.Core` / `.Tests` (net48) |
| Installers/Vs2026/Installer (VSIX) | `AiDevHelpers.Installers.Vs2026.Installer` / `.Tests` (net48) |
| Installers/VsCode/Core / Installer | Core split done 2026-11-20: pure protocol, buffer, log-root and DAP store modules in `Core` (no VS Code API); `Installer` depends on it via `file:../Core` |

Solution: `AiDevHelpers.slnx` at repo root, one solution folder per area.

## Status by step

| Step | Result |
|---|---|
| A1 ignored VSIX sources | Done. `BridgeLog.cs` and `BridgeToolWindow.cs` force-added; generated manifests untracked. |
| A2 commit old repo | Done, `91162c8`. |
| A3 baseline | Done. `dotnet build` on the VSIX fails by design (VSSDK needs VS MSBuild). Non-VSIX projects build. Baseline tests: LogHelper 196, TerminalControl 88, Bridge+MCP 120. LogHelper failed once and passed on rerun. VSIX built through `scripts/build-vs-extension-vsix.ps1`. |
| B4 create repo | Done, public. |
| B5 copy committed state | Done via `git archive HEAD`. |
| C6 moves and csproj renames | Done. |
| C7 namespace and assembly rename | Done. Leftover `TokenSaver` prose in comments kept (see open items). |
| C8 csproj edits | Done. |
| C9 MCP split | Done. `AddAiDevHelpersMcp` extension in Core (stdio variant and bare `IMcpServerBuilder` variant). `Program.cs` is the host only. |
| C10 test split and smoke tests | Done. `.gitattributes` added (`* text=auto eol=lf`). |
| C11 VS extension Core split | Done. `BridgeHost` and `DebugBridgeLogger` in Core; `BridgeLog` and `IdeActions` made public; `BridgePackage` is registration and startup only. VSIX contains Core and Bridge DLLs. |
| C12 VS Code | Done as-is. Fixture path fixed to five levels up. `npm test` 24/24, `tsc --noEmit` clean. |
| C13 solution, scripts, CI, docs | Done. `AiDevHelpers.slnx`; scripts point at the new VSIX path; CI on `main`/`dev`, packs Bridge and MCP.Core, feed `AiDevHelpers`. README and docs references renamed. |
| C14 verification gate | Partly done. Tests, VSIX build, VS Code tests and grep gate passed. NuGet pack not run. VSIX not installed into VS. |
| C15 initial commit and push | Done, `63ab044`, pushed to `origin/main`. First CI run queued, not checked. |
| Archive old repo | Done. |

## Deviations from the draft plan

- `dotnet build` of the VSIX cannot run VSSDK tasks. VSIX builds go through VS MSBuild in the scripts and in CI.
- Bridge tests fixture link is three levels up (`..\..\..\conformance`), not four as the draft said. MCP Core tests are four levels.
- The draft's Q4 "defer" was overridden: wire names renamed now. Consequences below.
- Shell `Move-Item` failed on this machine; moves were done with Git Bash `mv`. No effect on the result.
- VS Code Core split was skipped under Q7; it was added after the reorg on 2026-11-20 when the user approved it.
- Smoke tests replaced the draft's "no test project" option for projects without tests (Q10).
- LogHelper `ScaffoldTests` forbidden-prefix list now names `AiDevHelpers.Common`, `AiDevHelpers.Bridge` and `AiDevHelpers.MCP.Core`.
- `tokensaver-tab.ps1` file name kept; the C# and PowerShell pipe names match.

## Breaking changes users must act on

- MCP client config: server name is now `aidevhelpers-ide-bridge`, and the exe path is `AiDevHelpers.MCP`.
- Uninstall the old `SperryDev IDE Bridge` VSIX before installing the new one (new Identity).
- TokenSaver: switch to NuGet `AiDevHelpers.Bridge`, and to the new pipe and registry names.
- Any running VS instance must be restarted to pick up the new pipe names.

## Open items

- Install the new VSIX into VS and verify the bridge end to end; `update-vs-extension.ps1` not run.
- Check the first CI run on `main`. CI does not run the VS Code tests (`npm test` in `Core` and `Installer`); consider adding them.
- `scripts/run-dev-server.ps1` and the `TokenSaverMCP` service named in `docs/ide-bridge.md` (Live checks) are not in this repo; confirm or update.
- `.tokensaver/` state paths, `tokensaver-tab.ps1`, and `tokensaver-*` window and temp names are runtime identifiers kept on purpose. Rename only if TokenSaver's side is changed too.
- `vsce package` needs a VS Code manifest (publisher, engines, main) that the repo does not have. The packaging steps in `docs/ide-bridge.md` are unverified.
- Branch protection and required CI check on the new repo.
- NuGet packages build with missing-readme warnings; add a readme before publishing.

## Resumed 2026-11-20 (done)

- Plan file committed on `docs/reorg-plan` (`6baa8f0`).
- VS Code Core split: `src/Installers/VsCode/Core` holds the four pure modules and their tests. Core: 24/24 tests, build clean. Installer: type-check and build clean, new smoke test passes.
- Prose "TokenSaver" comments reworded in TerminalControl, Bridge, MCP.Core, VS Code; identifiers kept.
- `README.md` component paths and links, and `docs/ide-bridge.md` paths, config section (`IdeTools`) and VS Code build steps, corrected.
- NuGet pack: `AiDevHelpers.Bridge` 0.1.0 and `AiDevHelpers.MCP.Core` 1.0.0 both pack.
- .NET tests all pass: Bridge 18, MCP.Core 102, LogHelper 196, TerminalControl 88, Common, WindowsSession, Vs2026 Core, Vs2026 Installer, MCP 1 each.
- Stdio smoke: `AiDevHelpers.MCP` answers `initialize` and `tools/list` (32 tools; the count was recounted as 32 on 2026-11-20) when stdin stays open. Closing stdin immediately produced no stdout, which may matter for clients that close stdin early.
- Follow-up plan: ErrorHelper, TestingHelper, BookmarkHelper, ConfigHelper, ExtensionsHelper (empty slots not created).
