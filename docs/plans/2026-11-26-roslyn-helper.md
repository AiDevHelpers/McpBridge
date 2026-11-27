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
3. Unsaved VS Code buffers: the helper returns the planned edits; the extension applies them in the editor. Disk is not written while a buffer is dirty. (Replaces the overlay decision.)
4. One helper process per solution.
5. `listUnused` and `renameSymbol` are in this plan (units 12 and 14 parity).
6. A Visual Studio or Build Tools install is a hard requirement. VS Code users without it get a clear error.
7. Two helpers: a net10.0 helper for SDK-style projects, and a net48 helper for net48 and VSSDK projects. The extension routes each request to the helper that loads the project.

## Review changes (Sonnet review, read-only)

The review found the plan not ready. These changes are required before unit 1:
- **Shared project (unit 3):** must carry `Strings.resx` (Strings uses a ResourceManager bound to the Core assembly) and every type the engines use: MethodEditPlan, MemberSpec, EditTarget, CallerEdit, FileMove, MethodDocUpdater, TestDiscovery, CallerPropagator, CallSiteRewriter, ChangeSignatureFamily, ChangeSignatureHeader, CoverageOfReferences, UnusedFinder. Split unit 3 into types, then resx, then references.
- **InternalsVisibleTo:** the shared project grants it to Core, Core.Tests and both helper test projects.
- **New unit before 4: extract RunPlan.** Move the orchestration out of `CodeModelActions.cs` (RunPlan, lines 324-394): refusal on logical documents, diffs, hash check, planned/applied replies, and the moves and deletes after apply. Test first: a plan reply has the same shape as before.
- **New unit: fixture solution.** Before unit 2: an SDK project, a second project referencing it, and a broken project under `src/Helpers/RoslynHelper/tests/Fixtures`. Unit 2's test uses it.
- **Parser unit (5) tests:** real parser fixtures, not the empty `addMember` and `changeSignature` cases in `conformance/fixtures.json`. Move RequireString, OptionalInt, RequireInt and OptionalBool with the parsers. Unit 5 needs only unit 3, so it can run earlier.
- **New unit: action dispatcher.** Map each action name to its engine and reply shape. Units 6 to 11 each use it.
- **Dirty buffers (unit 4 and 13):** apply writes only clean files. Dirty files return edits. The plan hash covers the buffer text.
- **Semantic tools:** listCodeElements, semanticReferences and semanticDefinition stay unsupported in v1 (they use ResolvePositionAsync). Add a unit only if the user asks.
- **Manifests (unit 17):** check the process is alive before using a manifest. Set the folder ACL. Define the manifest format in unit 1.
- **Unit 1:** run the Bridge conformance fixtures against the helper, rather than duplicating its tests.
- **Unit 2:** a broken project returns a per-project diagnostic with `{project, ok, error}`.
- **Roslyn version:** the helper pins the same Roslyn as Core, 5.12.0-1.26573.119 (from `Core.csproj`), unless a unit shows it must differ.

## Architecture

- **Process:** `AiDevHelpers.Helpers.RoslynHelper`, an exe targeting net10.0-windows, in `src/Helpers/RoslynHelper/src`, tests in `tests`. Both added to `AiDevHelpers.slnx` under `/Helpers/RoslynHelper/`.
- **Shared engines:** a project under `src/Installers/Vs2026/Engines/src` (net48 and net10.0-windows) holding the Solution-only engines, PlanRunner, PlanHash, UnifiedDiff, LogicalDocuments, EditChecks and NamespaceUtil. Core and the helper both reference it.
- **Start:** the VS Code extension starts `RoslynHelper.exe --solution <path>` for the open solution. It writes `%ProgramData%\AiDevHelpers\roslyn-helpers\<pid>.json` (`{pid, pipeName, solutionPath}`). The extension stops it when the solution closes.
- **Find:** the MCP server reads that folder and matches `solutionPath`. The VS Code bridge routes `roslyn*` actions to the helper.
- **Protocol:** as the bridge. One request line `{"action":"editMethod","args":{...}}`, one reply line `{"ok":true,"result":{...}}` or `{"ok":false,"error":"..."}`. Pipe `AiDevHelpers.Roslyn.<pid>`. Requests are serialized: MSBuildWorkspace is not thread-safe.

## Units

1. **Skeleton and protocol.** Exe, BridgeServer reuse, `health`, refusal of unknown actions, manifest writer. Test first: a pipe round trip returns health, and an error for an unknown action.
2. **Solution load.** MSBuildLocator, `MSBuildWorkspace.OpenSolutionAsync`, cached Solution, per-project diagnostics instead of a failed load. Test first: a fixture solution with an SDK project and a broken project lists both.
3. **Shared engine library.** Move the engines listed above. Test first: the existing Core tests pass unchanged after the move. *Done: `src/Installers/Vs2026/Engines/src` (net48 and net10.0-windows). Namespaces and the Strings resource name are unchanged. FileOperations moved too (plain IO, needed by unit 10). Core tests 247/247; RoslynHelper tests 11/11; VSIX installer builds with the VS MSBuild. Roslyn 5.12.0-1.26573.119 for net48, 5.12.0 for net10 (as unit 1). ConvertEngine RS1034 fixed with IsKind, no behavior change.*
4. **Apply path.** Write changed documents to disk; refuse on PlanHash mismatch; no TryApplyChanges, no DTE save. Test first: a plan applied to a temp copy changes disk text; a changed file is refused. *Done: `PlanApplier.ApplyAsync` (src/Helpers/RoslynHelper/src/PlanApplier.cs). It refuses, and writes nothing, when a changed file's disk text differs from the snapshot, or when the plan removes a file. The PlanHash check on confirm stays with the action handlers (units 6 to 11). Tests: 13/13 (2 new, red before the implementation).*
5. **Argument parsing.** Move the Parse* helpers out of IdeActions.cs. Test first: parser fixtures from `conformance/fixtures.json`. *Done: the parsers and the JSON readers (RequireString, RequireInt, OptionalString, OptionalInt, OptionalBool, StringList) moved to `ArgumentParsers` in the Engines project, which IdeActions uses through `using static`. Tests: `ArgumentParsersTests.cs` (9), with realistic argument objects; `conformance/fixtures.json` has only empty addMember and changeSignature cases, so the tests do not use them. Red before the move (compile errors). Core 247/247; RoslynHelper 22/22.*
5a. **Extract RunPlan.** Move the orchestration out of `CodeModelActions.cs` (RunPlan): refusal on logical documents, diffs, hash check, planned/applied replies, and the moves and deletes after apply. The VS wrapper keeps only the workspace snapshot and the apply. Test first: a plan reply has the same shape as before. (Added from the Sonnet review; it was missing from this list.) *Done: `PlanFlow.RunAsync` (Engines) holds the shared flow; the host passes the snapshot, plan, apply and post-apply moves. The VS RunPlan wrapper calls it. Tests: `PlanFlowTests.cs` (3: unconfirmed reply, confirmed reply with moves and deletes, refused plan not applied). Helper 25/25; Core 247/247.*
5b. **Action dispatcher.** Map each action name to its engine and reply shape. Units 6 to 11 each use it. (Added from the Sonnet review; it was missing from this list.) *Done: `ActionDispatcher` (src/Helpers/RoslynHelper/src/ActionDispatcher.cs). `RoslynRequestHandler` registers health and loadSolution on it; unknown actions are still refused by name; duplicate registration is refused. Tests: `ActionDispatcherTests.cs` (4). Helper 29/29 (existing handler tests unchanged). Engine mapping per action comes with each group unit (6 to 11).*
6. **Group A: editMethod, editMember, addMember.** Test first: confirm false returns `planned`; confirm true applies; a stale planHash is refused. *Done: `PlanActions` (helper) registers the three on the dispatcher, planning through `PlanFlow`, applying through `PlanApplier`, and keeping the helper's solution current after an apply. `WithMembersAsync` moved to `MemberPlanning` (Engines) so editMethod can use it. Tests: `GroupAActionTests.cs` (5: plan without confirm for editMethod, addMember and editMember; confirmed apply with the shown hash; stale hash refused). Helper 34/34; Core 247/247. Member specs in addMember need `accessibility`, as the VS side does.*
7. **Group B: addMethod, addOverload, deleteMember, changeSignature** (CallerPropagator, CallSiteRewriter). Test first: a caller in a second project is rewritten. *Done: `PlanActions` registers the four on the dispatcher. addMethod and addOverload share one planner through `MemberPlanning` (as Core's AddMethod). deleteMember and changeSignature call DeleteEngine and ChangeSignatureEngine. Tests: `GroupBActionTests.cs` (4: changeSignature rewrites `Add(1)` to `Add(1, 2)` in the second project, confirmed with the shown hash; addMethod and addOverload plan without writing; deleteMember with a caller in another project is refused). Helper 38/38. Core and Engines unchanged in this unit.*
8. **Group C: editType, addType, addInterfaceMember, implementMembers, generateConstructor.** Test first: implementMembers on a fixture interface. *Done: five actions registered in `PlanActions`, planning through the shared engines (EditTypeEngine, AddTypeEngine, InterfaceMemberEngine, ImplementMembersEngine, GenerateConstructorEngine). Tests: `GroupCActionTests.cs` (5: implementMembers confirmed with the shown hash stubs the interface member; four plan-only checks). Red run: 5 failing, then green. Helper 43/43.*
9. **Group D: addEnumMember, editEnumMember, threadParameter.** Test first: an enum value change is blocked without allowValueChange. *Done: three actions registered in `PlanActions`, planning through EnumMemberEngine and ThreadParameterEngine. Tests: `GroupDActionTests.cs` (4: a public enum's value change is refused without allowValueChange; with it, it plans without writing; addEnumMember and threadParameter plan without writing). Red run: 4 failing, then green. Helper 47/47.*
10. **Group E: deleteType, moveType, renameNamespace** (file moves through FileOperations, no DTE). Test first: moving a type moves its file in an SDK project. *Done: three actions registered in `PlanActions`. After the text writes, the helper moves and deletes files on disk through FileOperations, as the VS host does. Two fixes found on the way: (1) `PlanApplier` never saw removed documents (Roslyn reports them apart from changed ones), so it could not refuse a removal; it now allows a removal only for a file the plan lists in FileDeletes, and it writes added documents as new files (refusing an existing path). (2) `DeleteEngine` never set the file delete for a type in a file-scoped namespace; it now counts members under the namespace. Tests: `GroupEActionTests.cs` (3: moveType confirmed moves the file; deleteType confirmed with deleteFile removes the file; renameNamespace plans without writing). Red run: 3 failing, then green. Helper 50/50; Core 247/247.*
11. **Group F: tddAddTest, tddUpdateTest.** Test first: a new test goes beside the method's test file. *Done: two actions registered in `PlanActions`: tddAddTest through MemberPlanning and TddEngine.StartAsync (the test goes into the test file the caller names, which must differ from the method's file), tddUpdateTest through TddEngine.UpdateTestAsync. Tests: `GroupFActionTests.cs` (3: confirmed tddAddTest writes the method and the test; the same file is refused; tddUpdateTest plans without writing). The test attribute in the fixture is a local class named Fact, because TestDiscovery matches attribute names. Red run: 3 failing, then green. Helper 53/53.*
12. **listUnused.** Test first: an unused private method is listed; a public one is not. *Done: `ListUnusedActions` (helper) registers listUnused on the dispatcher through `UnusedFinder`, a read-only action that does not go through the plan flow. `RoslynRequestHandler` registers it. Tests: `ListUnusedActionTests.cs` (2: an unused private method is listed and a used private method and a public one are not; includePublic lists the public one). Red run: compile failure (no action), then green. Helper 55/55.*
13. **Unsaved buffers.** The extension sends `documents: [{path, text}]`, applied as an overlay before planning. Test first: a plan reads overlay text, not disk. *Done: `PlanActions` takes `documents` on every edit action. Each buffer replaces its file's text in the snapshot, so the plan and its hash read editor text (decision 3). On apply, a dirty file is not written: its planned text comes back in the reply as `unsaved: [{path, text}]`, and the helper's own copy keeps the disk text. A plan that moves or deletes a dirty file is refused. `PlanApplier` gained the dirty set and the `UnsavedEdit` result. Tests: `UnsavedBufferActionTests.cs` (2: a deleteMember on a line that exists only in the buffer is planned; a confirmed plan for a dirty file writes nothing to disk and returns its edit). Red run: 2 failing, then green. Helper 57/57.*
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
