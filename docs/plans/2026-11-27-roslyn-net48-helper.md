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
4. Per-project diagnostics already return `{project, ok, error}` (unit 2), so a load that fails for one framework still reports the rest.
5. The VS Code extension starts the net48 helper when the solution needs it, through the same `RoslynHelperLifecycle`.

**Question 1:** For a solution with both SDK-style and net48 projects (the repo's own `AiDevHelpers.slnx` is one), should edit actions route to the net48 helper for the whole solution, or should each action target the project it touches?

**Question 2:** Should the net48 helper be published with the VS Code extension (needs Framework MSBuild on the user's machine, as the net10 helper needs VS or Build Tools), or should it be found on the machine and reported as missing when absent?

## Architecture

- `src/Helpers/RoslynHelper48` (net48, exe): `Program` (`--solution`, `--manifest-folder`), `BridgeServer` reuse, shared handler source.
- Shared code: move `RoslynRequestHandler`, `ActionDispatcher`, `PlanActions`, `ListUnusedActions`, `RenameSymbolActions`, `PlanApplier`, `Strings` into a shared project `src/Helpers/RoslynHelper.Shared` (multi-targeted net48 and net10.0-windows), referenced by both helpers. The net10 helper's tests keep passing unchanged.
- Bridge: `RoslynRouting.Decide` gains a framework choice from the manifest's `framework` field.

## Units

1. **Shared handler project.** Move the shared source into `RoslynHelper.Shared` (net48 and net10.0-windows). Test first: the existing net10 helper tests pass unchanged after the move (62/62 and the Bridge 101).
2. **Skeleton.** `RoslynHelper48` exe, `health`, manifest with `framework: net48`. Test first: a pipe round trip returns health with framework net48.
3. **Solution load on net48.** MSBuildLocator finds Framework MSBuild; load the fixture solution with a net48 SDK-less project and the SDK-style fixture. Test first: a net48 project loads and the SDK project loads in the same solution, each with its own diagnostic.
4. **Edit actions on net48.** The shared actions run on the net48 host. Test first: `editMethod` plan on a net48 fixture without confirm writes nothing; with the hash it applies.
5. **Routing by framework.** Bridge and VS Code choose the helper from the manifest. Test first: a solution with a net48 project routes to the net48 helper; an SDK-only solution routes to net10.
6. **Extension wiring and bundling.** The extension starts the net48 helper when needed; the build publishes `RoslynHelper48` to `dist/roslyn48`. Test first: a solution with a net48 project starts the net48 helper with `--solution`.
7. **Live check.** One net48 edit through the VS Code helper, compared with the VS result on the same fixture. Record in `docs/dogfood.md`.

## Risks

- Framework MSBuild may be missing on a machine with only .NET SDK; the helper reports it with a clear error (no silent partial load).
- Roslyn for net48 and for net10 must stay on the pins above; a version drift changes the plan output.
- Two helpers per solution share one manifest folder; the manifest's `framework` field must be checked before a request is sent.
