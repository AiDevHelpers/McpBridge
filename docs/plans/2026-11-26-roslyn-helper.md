# Roslyn helper for the VS Code edit tools

Status: planned, not started. Answers from the user are in "Decisions". Units run in order; each unit merges to `wip` when its checks pass.

Parent plan: `docs/plans/archived/2026-11-26-vscode-parity.md` (answer 1: a separate helper plan). Unit 19 of this plan is the parity sync and live check.

## Findings

- The 20 tool engines (AddTypeEngine, MethodEditEngine, ChangeSignatureEngine, NamespaceEngine, TddEngine, UnusedFinder and the rest) take a Roslyn `Solution`. None imports EnvDTE or Microsoft.VisualStudio.*. Their tests run on AdhocWorkspace. MSBuildWorkspace output should work with them; not yet verified.
- VS coupling is in the wrapper: `CodeModelActions.cs` (RunPlan, RenameSymbol, ListUnused, ResolvePositionAsync), and `FileMover.cs`, which saves open documents through DTE. `FileOperations.cs` is plain file IO.
- Argument parsers (ParseCallerEdits, ParseMemberSpecs, ParseDocUpdates, ParseNewParameters, ParseMemberEdit) live in `IdeActions.cs`, next to EnvDTE code. They must move out first.
- Unsaved editor text is visible to the VS engines. A disk-only workspace cannot see it. Unit 13 closes that gap.
- `BridgeServer` (`src/Bridge/src/BridgeServer.cs`) already provides one-line JSON over a named pipe, same-user ACL (`CreatePipeSecurity`) and `health`.

## Decisions

1. Helper project: `src/Helpers/RoslynHelper`.
2. Engines: move the Solution-only engines into a shared project now (unit 3).
3. Unsaved VS Code buffers: sent as an overlay (unit 13).
4. One helper process per solution.
5. `listUnused` and `renameSymbol` are in this plan (units 12 and 14 parity).
6. A Visual Studio or Build Tools install is a hard requirement. VS Code users without it get a clear error.

## Architecture

- **Process:** `AiDevHelpers.Helpers.RoslynHelper`, an exe targeting net10.0-windows, in `src/Helpers/RoslynHelper/src`, tests in `tests`. Both added to `AiDevHelpers.slnx` under `/Helpers/RoslynHelper/`.
- **Shared engines:** a project under `src/Installers/Vs2026/Engines/src` (net48 and net10.0-windows) holding the Solution-only engines, PlanRunner, PlanHash, UnifiedDiff, LogicalDocuments, EditChecks and NamespaceUtil. Core and the helper both reference it.
- **Start:** the VS Code extension starts `RoslynHelper.exe --solution <path>` for the open solution. It writes `%ProgramData%\AiDevHelpers\roslyn-helpers\<pid>.json` (`{pid, pipeName, solutionPath}`). The extension stops it when the solution closes.
- **Find:** the MCP server reads that folder and matches `solutionPath`. The VS Code bridge routes `roslyn*` actions to the helper.
- **Protocol:** as the bridge. One request line `{"action":"editMethod","args":{...}}`, one reply line `{"ok":true,"result":{...}}` or `{"ok":false,"error":"..."}`. Pipe `AiDevHelpers.Roslyn.<pid>`. Requests are serialized: MSBuildWorkspace is not thread-safe.

## Units

1. **Skeleton and protocol.** Exe, BridgeServer reuse, `health`, refusal of unknown actions, manifest writer. Test first: a pipe round trip returns health, and an error for an unknown action.
2. **Solution load.** MSBuildLocator, `MSBuildWorkspace.OpenSolutionAsync`, cached Solution, per-project diagnostics instead of a failed load. Test first: a fixture solution with an SDK project and a broken project lists both.
3. **Shared engine library.** Move the engines listed above. Test first: the existing Core tests pass unchanged after the move.
4. **Apply path.** Write changed documents to disk; refuse on PlanHash mismatch; no TryApplyChanges, no DTE save. Test first: a plan applied to a temp copy changes disk text; a changed file is refused.
5. **Argument parsing.** Move the Parse* helpers out of IdeActions.cs. Test first: parser fixtures from `conformance/fixtures.json`.
6. **Group A: editMethod, editMember, addMember.** Test first: confirm false returns `planned`; confirm true applies; a stale planHash is refused.
7. **Group B: addMethod, addOverload, deleteMember, changeSignature** (CallerPropagator, CallSiteRewriter). Test first: a caller in a second project is rewritten.
8. **Group C: editType, addType, addInterfaceMember, implementMembers, generateConstructor.** Test first: implementMembers on a fixture interface.
9. **Group D: addEnumMember, editEnumMember, threadParameter.** Test first: an enum value change is blocked without allowValueChange.
10. **Group E: deleteType, moveType, renameNamespace** (file moves through FileOperations, no DTE). Test first: moving a type moves its file in an SDK project.
11. **Group F: tddAddTest, tddUpdateTest.** Test first: a new test goes beside the method's test file.
12. **listUnused.** Test first: an unused private method is listed; a public one is not.
13. **Unsaved buffers.** The extension sends `documents: [{path, text}]`, applied as an overlay before planning. Test first: a plan reads overlay text, not disk.
14. **renameSymbol.** Test first: a rename changes every reference in the fixture solution.
15. **Parity test.** Every edit tool the VS bridge handles is handled by the helper or listed as unsupported. Test first: fails on the current state.
16. **VS Code client.** A TypeScript module routes the edit tool names to the helper and returns a clear error when no helper runs. Test first: a mocked pipe gets the request line.
17. **Lifecycle.** Start on solution open, manifest, stop on close, restart on watchdog. Test first: a start without a solution path is refused.
18. **MCP routing.** Edit tools route to the helper for a VS Code solution. Test first: the router picks the helper manifest for a VS Code solution.
19. **Parity sync and live check.** Update `fixtures.json`, run the full gate, and do one live edit in VS Code on a fixture solution, compared with the VS result. Record it in `docs/dogfood.md`.

## Risks

- Load time and memory: MSBuildWorkspace can take minutes and several GB on large solutions. Cache the Solution, set timeouts, report progress.
- Load failures: net48 projects need the .NET Framework targeting pack. Missing SDK workloads give partial loads; return per-project diagnostics.
- Result parity: source generators, `.editorconfig` and design-time properties can differ from the VS workspace. Compare plan output with VS on one fixture before trusting it.
- Roslyn version drift: pin the helper's Roslyn to what the solution's SDK expects; test one solution per SDK.
- Elevation and pipe access: a non-elevated extension cannot reach an elevated helper. Keep the same-user pipe rule.
- VS dependency: MSBuildLocator needs an installed MSBuild. Missing MSBuild gives a clear error.
