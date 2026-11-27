# Roslyn net48 helper

Status: planned, not started. Open questions are marked **Question**; they are asked before the unit that depends on them.

Parent plan: `docs/plans/archived/2026-11-26-roslyn-helper.md` (decision 7: two helpers, the extension routes each request to the helper that loads the project).

## Findings

- The net10 helper (`src/Helpers/RoslynHelper`) loads solutions with `MSBuildWorkspace` on the .NET SDK. It can load SDK-style projects. It cannot load a .NET Framework project that needs the .NET Framework MSBuild and targeting pack (net48, VSSDK).
- Repo projects that are net48 or VSSDK-bound: `Bridge`, `Vs2026/Core`, `Vs2026/Engines`, `Vs2026/Installer`. Most other projects are SDK-style net10. The repo's own solution therefore has projects the net10 helper cannot fully load.
- The shared Engines project already multi-targets net48 and net10.0-windows, so the engines run in both hosts without a copy.
- Roslyn for net48 is pinned to `5.12.0-1.26573.119` (VS feed, ExcludeAssets runtime); net10 uses `5.12.0`. The net48 host must use the first.
- Hosting: a net48 exe needs the .NET Framework runtime (ships with Windows) and MSBuild from Visual Studio (Framework MSBuild). It finds MSBuild through `MSBuildLocator` or `vswhere`.

## Decisions

1. The net48 helper is a second executable, `RoslynHelper48.exe`, in `src/Helpers/RoslynHelper48`. It reuses the net10 helper's request handler, dispatcher and actions through a shared source or a shared project; no action code is duplicated.
2. The helper writes the same manifest format to `%ProgramData%\AiDevHelpers\roslyn-helpers\<pid>.json`, with a `framework` field (`net10` or `net48`). Pipe name `AiDevHelpers.Roslyn48.<pid>`.
3. Routing: a solution's projects decide the helper. If every project is SDK-style, the net10 helper answers; if any project is net48 or non-SDK, the net48 helper answers. Mixed solutions use net48.
3a. Per action (answer 1): an edit action goes to the helper that loads the project it touches. A solution load still starts both helpers when the solution has both kinds, so per-project diagnostics come from each. An action that touches projects of both kinds is refused until a rule is added.
4. Per-project diagnostics already return `{project, ok, error}` (unit 2), so a load that fails for one framework still reports the rest.
5. The VS Code extension starts the net48 helper when the solution needs it, through the same `RoslynHelperLifecycle`.

**Answer 1 (routing):** per action. Each edit action goes to the helper for the project it touches. Decision 3 is replaced accordingly (see Decisions 3a). An action that spans projects of both kinds is refused with a clear message until a rule is added.

**Answer 2 (publishing):** publish with the extension. `RoslynHelper48` ships in the extension build, like the net10 helper. It still needs Framework MSBuild from Visual Studio, as the net10 helper already needs VS or Build Tools.

## Architecture

- `src/Helpers/RoslynHelper48` (net48, exe): `Program` (`--solution`, `--manifest-folder`), `BridgeServer` reuse, shared handler source.
- Shared code: move `RoslynRequestHandler`, `ActionDispatcher`, `PlanActions`, `ListUnusedActions`, `RenameSymbolActions`, `PlanApplier`, `Strings` into a shared project `src/Helpers/RoslynHelper.Shared` (multi-targeted net48 and net10.0-windows), referenced by both helpers. The net10 helper's tests keep passing unchanged.
- Bridge: `RoslynRouting.Decide` gains a framework choice from the manifest's `framework` field.

## Units

1. **Shared handler project.** Move the shared source into `RoslynHelper.Shared` (net48 and net10.0-windows). Test first: the existing net10 helper tests pass unchanged after the move (62/62 and the Bridge 101). *Done: `src/Helpers/RoslynHelper.Shared` (ActionDispatcher, PlanActions, PlanApplier, RoslynRequestHandler, SolutionLoader, the actions, Strings and its resx) builds for net48 and net10.0-windows. Net48 needed PolySharp (init-only and records), `ISet` instead of `IReadOnlySet`, `Cast<Match>`, a relative-path helper (no `Path.GetRelativePath`), and no `ArgumentNullException.ThrowIfNull`/`TryAdd`. The net10 helper references the shared project; helper tests 62/62; the Engines project lets the shared assembly see its internals. `ManifestWriter` and `Program` stay in the net10 helper until unit 2.*
2. **Skeleton.** `RoslynHelper48` exe, `health`, manifest with `framework: net48`. Test first: a pipe round trip returns health with framework net48.
3. **Solution load on net48.** MSBuildLocator finds Framework MSBuild; load the fixture solution with a net48 SDK-less project and the SDK-style fixture. Test first: a net48 project loads and the SDK project loads in the same solution, each with its own diagnostic.
4. **Edit actions on net48.** The shared actions run on the net48 host. Test first: `editMethod` plan on a net48 fixture without confirm writes nothing; with the hash it applies.
5. **Routing by framework, per action.** Bridge and VS Code choose the helper from the manifest and the project each action touches. Test first: an action on a net48 project routes to the net48 helper; an action on an SDK project routes to net10; an action spanning both kinds is refused with a clear message.
6. **Extension wiring and bundling.** The extension starts the net48 helper when needed; the build publishes `RoslynHelper48` to `dist/roslyn48`. Test first: a solution with a net48 project starts the net48 helper with `--solution`.
7. **Live check.** One net48 edit through the VS Code helper, compared with the VS result on the same fixture. Record in `docs/dogfood.md`. *Done 2026-11-27: VSIX installed, VS Code started the net48 helper for a legacy fixture; a plan and confirmed edit over its pipe matched `ide_edit_method` in VS (row 42). Not exercised: the MCP router with a net48 file.*

*Units 2 to 6 are done too: `RoslynHelper48` (exe, Program, `framework: net48` in the manifest, a process pipe test); the Framework MSBuild load through the real helper process (legacy, SDK net48 and broken projects each reported); edit actions on net48 (plan, then apply with the hash); per-action routing by the project's framework in the Bridge (`RoslynRouting`) and the VS Code client; the extension starts the net48 helper when the folder has a net48 or legacy project, and both helpers are published and bundled.*

## Risks

- Framework MSBuild may be missing on a machine with only .NET SDK; the helper reports it with a clear error (no silent partial load).
- Roslyn for net48 and for net10 must stay on the pins above; a version drift changes the plan output.
- Two helpers per solution share one manifest folder; the manifest's `framework` field must be checked before a request is sent.
