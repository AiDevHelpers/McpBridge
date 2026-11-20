# AiDevHelpers helpers follow-up, 2026-11-20

Status: draft, revised after the read-only Sonnet review. Awaiting approval. Builds on [the reorg plan](2026-11-20-aidevhelpers-mcpbridge-reorg.md), which is executed.

## Decisions

- D1 Test project suffix stays `.Tests` (as built), e.g. `AiDevHelpers.Common.Tests`. No rename.
- D2 One combined plan (this file), seven phases, one review gate per phase. MCP tools are wired per helper, not in a final phase, so each phase is demoable over MCP.
- D3 ErrorHelper reads three sources first: MSBuild/compiler errors, Roslyn analyzers (CodeAnalysis), NuGet restore. IDE live diagnostics are a later phase.
- D4 TestingHelper coverage tool is configurable. Coverlet and Visual Studio coverage are both implemented. Default is VS when Coverlet is not referenced in the open repo, and Coverlet when it is. Failure behaviour is open (see Q-COV).
- D5 BookmarkHelper uses Visual Studio's own bookmarks, reached through the IDE bridge. Bookmarks are not stored in a repo file.
- D6 ExtensionsHelper adds extensions through the IDE CLI. Each install needs explicit user approval. Install flow is open (see Q-EXT).
- D7 TestingHelper hot-reload trigger: read both the IDE Hot Reload setting and the `dotnet watch` setting. Use whichever is active. No file watcher of our own. Precedence when both are active is open (see Q-HR).
- D8 BookmarkHelper v1 is Visual Studio only. VS Code bookmark calls return a not-supported error until a fallback is designed.
- D9 ExtensionsHelper `Add` accepts any extension id, after user confirmation for each install.
- D10 ConfigHelper v1 resolves: remote debugger settings (XML/JSON), `launchSettings.json`, `appsettings.json`, user settings, solution settings.
- D11 (proposed by review, adopted) Phase order puts the bridge-dependent BookmarkHelper last, because it has the most unknowns.
- D21 ErrorHelper reuses LogHelper's `LogSeverity` (Error, Warning, Information as notice).
- D22 Snapshots live in memory per process; they are lost on restart.
- D23 Suppression is read from the solution .vsconfig under the key `aidevhelpersErrors`. Unverified: Visual Studio may reject unknown .vsconfig keys; check on a real install.
- D24 Build output is supplied by the caller through `error_ingest`. ErrorHelper does not run dotnet build itself.
- D25 Workspace recommendations: `.vscode/extensions.json` lists ms-dotnettools.csdevkit and ms-dotnettools.csharp. Existing entries are kept.
- D26 When C# Dev Kit is installed, `dotnet.defaultSolution` is written to `.vscode/settings.json` without a separate approval (user choice). The key is undocumented; verify on a real install.
- D27 Test discovery and launch profiles need no setting; C# Dev Kit reads launchSettings.json and tests itself. Only dotnet.defaultSolution is written.
- D28 Visual Studio extension listing is not implemented; list covers VS Code only.
- D29 Hot-reload failures reach the caller two ways: an error log (MCP log notification) and the test_failures queue. Both, as chosen.
- D30 VS coverage: IDE bridge first, dotnet-coverage CLI as fallback. The bridge coverage action is not built yet, so the CLI runs in practice.
- D31 Coverage lives in MCP.Core TestingTools, not IdeTestRunService. IdeTestRunService is unchanged. This departs from D15.
- D32 The monitor polls the TestResults folder of watched solutions every 5 seconds while hot reload is active. No file watcher (D7).
- D33 Visual Studio exposes no bookmark collection through EnvDTE (ToolWindows.BookmarkWindow does not compile). The user chose the VS Shell bookmark service, but no interface for it has been found in SDK docs. List, get and remove are blocked on that. Set is not built either: without enumeration, a toggle could remove an existing bookmark.
- D34 The VS Code not-supported reply for bookmark actions (D8) is not built. The bridge fixture bookmark_list_refused_by_generic_bridge covers only the generic refusal.

## Open questions (block the phase named)

Answered in this revision:

- D12 (Q-TFM) New helper projects target `net10.0`. Windows-API helpers (e.g. WindowsSession) stay windows-only. Common stays `net10.0-windows` until its consumers are known.
- D13 (Q-EXT) `Add` uses a two-step call: the first call returns a pending id and the exact command; a second call with that id runs it. `GetInfo` and the VS install flow remain to be detailed in phase 4, but approval is settled.
- D14 (Q-COV) With no coverage provider available, the coverage call returns a clear error naming the missing tool. No silent fallback.
- D15 (Q-TEST) TestingHelper extends the existing `IdeTestRunService` in MCP.Core. No new test-run library.

- D16 (Q-HR) When both hot-reload settings are active, TestingHelper uses the IDE Hot Reload setting. It falls back to `dotnet watch` only when the IDE setting is off.
- D17 (Q-BM) Bridge actions: `bookmarkList`, `bookmarkGet`, `bookmarkSet`, `bookmarkRemove`. VS Code replies with not-supported (D8).
- D18 (Q-SUP) Suppression config lives in the solution. It overrides user settings on conflict.
- D19 (Q-NUGET) Each new helper ships as its own NuGet package. The publish job packs and pushes each one. Versioning per helper is added to the release steps (phase 7).

- D20 VS Code Hot Reload setting is read through the extension host settings API (`vscode.workspace.getConfiguration`) and returned on a new bridge action. No settings.json parsing. Phase 5 adds this action and its fixture.

No open questions remain.

## Phases (order per D11)

Each phase lists acceptance criteria, and each phase that adds an MCP tool ends with a `tools/list` count check (baseline: 32 tools; recounted 2026-11-20, the reorg report said 33 because it counted the serverInfo name).

### Phase 1: Common (minimal)

- Adds nothing until a second consumer exists. Severity and snapshot types move into ErrorHelper (phase 3) unless a second user appears.
- New projects target `net10.0` (D12).
- Acceptance: `src/Common/src` and `src/Common/tests` build; existing 1 test passes.

### Phase 2: ConfigHelper (DONE 2026-11-20, branch feature/helpers-config)

- Resolve real paths for the D10 file kinds.
- Redact secrets in any returned content (appsettings and remote-debugger files may hold credentials). Path handling must reject traversal outside the solution or user folders.
- Blocked on Q-SUP for the settings location used by ErrorHelper.
- Project: `src/Helpers/ConfigHelper/src` and `src/Helpers/ConfigHelper/tests`.
- Acceptance: each D10 kind resolves against a fixture folder; a traversal path is refused; a secret value in a fixture is redacted in output.
- Gate: `dotnet test` for ConfigHelper; MCP tools/list count = 34 (32 baseline + config_resolve, config_read).

### Phase 3: ErrorHelper (DONE 2026-11-20)

- API: `GetErrors`, `GetWarnings`, `GetNotices`, `GetChanges(snapshotId)`, `TakeSnapshot()` (records state, returns only the id), filter by `code` and `source`, and `Suppress` config for codes and sources.
- Sources in v1 (D3): MSBuild/compiler output, Roslyn analyzer diagnostics, NuGet restore output.
- Severity and snapshot types are ErrorHelper-owned. Relation to `LogSeverity` in LogHelper is open: reuse it, or duplicate. Decide before coding.
- Snapshot retention: decide in-memory per process or persisted under `.tokensaver` state (open).
- Project: `src/Helpers/ErrorHelper/src` and `src/Helpers/ErrorHelper/tests`. MCP tools wired in this phase.
- Acceptance: parser fixtures per source; `GetChanges` after `TakeSnapshot` returns only new diagnostics; suppression removes listed codes and sources.

### Phase 4: ExtensionsHelper (DONE 2026-11-20, plus workspace setup)

- API: `List`, `GetInfo(id)`, `Add(id)`. `Add` runs the IDE CLI (D6) and requires explicit approval per install.
- Blocked on Q-EXT for the approval mechanism and VS install flow.
- Project: `src/Helpers/ExtensionsHelper/src` and `src/Helpers/ExtensionsHelper/tests`.
- Acceptance: List and GetInfo against recorded CLI output; `Add` in dry-run mode prints the command and does not run it; `Add` without approval is refused.
- Gate: tools/list count updated.

### Phase 5: TestingHelper (DONE 2026-11-20, see D29-D32)

- Hot-reload: D7 with precedence from Q-HR.
- Code coverage: configurable provider (D4). With no provider, return a clear error (D14). Coverage output as structured data (Cobertura for Coverlet; VS coverage export for VS).
- Extends `IdeTestRunService` in MCP.Core (D15).
- Project: `src/Helpers/TestingHelper/src` and `src/Helpers/TestingHelper/tests`.
- Acceptance: Coverlet path runs against a sample test project and returns Cobertura data; provider selection is unit-tested; a hot-reload test failure is pushed to the caller.
- Note: VS coverage may require VS Enterprise and `dotnet-coverage`. Tool dependency is to be stated before coding.

### Phase 6: BookmarkHelper (PARTIAL 2026-11-20: library, fixture and MCP tools done; VS route blocked, see D33-D34)

- API: `Get`, `List`, `Set`, `Remove`, backed by Visual Studio bookmarks through the IDE bridge (D5, D8).
- New bridge actions and protocol changes are required (Q-BM). The existing `conformance/fixtures.json` covers only health, unknown_action and malformed_line; new fixtures are added in this phase.
- The VS side is in the net48 VSIX. The reorg plan says VSIX builds only through VS MSBuild, so `dotnet test` does not exercise it. Add a VS MSBuild step to the gate, or test through the fake host only.
- The VS Code side is TypeScript and cannot hold a C# helper. D8 means VS Code is out of v1, so no `VsCode/Core` placement.
- Project: `src/Helpers/BookmarkHelper/src` and `src/Helpers/BookmarkHelper/tests`, plus VS wiring in `src/Installers/Vs2026/Core`.
- Acceptance: contract tests against new fixtures; library tests with a fake IDE host; the VSIX builds with VS MSBuild.

### Phase 7: docs and solution

- Update `AiDevHelpers.slnx` with every new project in its area folder.
- Update `conformance/protocol.md` and `docs/ide-bridge.md`, and the README component list.
- Check the new tool names do not collide with the 33 existing ones.
- Update the CI pack list if any helper ships as NuGet (Q-NUGET).

## Verification gate per phase

- `dotnet test` for the new projects and the existing projects in the same area.
- `npm test` in `src/Installers/VsCode/Core` and `Installer` when VS Code code changes. CI does not run these (reorg open item), so the gate is local only until that changes.
- Smoke: `AiDevHelpers.MCP` stdio `initialize` and `tools/list` with stdin held open. Expected count per phase, starting from 32 (baseline).

## Breaking changes

None expected. Each helper is additive. New MCP tool names must not collide with existing ones.

## Security notes

- ConfigHelper redacts secrets and refuses paths outside the solution and user folders.
- `Add` in ExtensionsHelper requires per-install approval.
- LogHelper has a Security folder; reuse its approach where it fits.

## Out of scope

- ErrorHelper IDE live diagnostics (later phase after v1).
- Installing extensions without explicit approval.
- Bookmark storage in repo files.
- VS Code bookmarks (D8).
