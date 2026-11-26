# Lite gate

Run before each commit. It is the fast check; the full gate in `full-gate.md` still runs before a PR to `dev` or `main`.

The steps use the aidevtools MCP tools, so they run in an open Visual Studio instance on the solution. This file is
prose on purpose: `GatePlanner` reads only the `gate` block in `full-gate.md`, so these steps are not run by `gate_run`.

## Steps

1. **Find the instance.** Call `mcp__aidevtools__ide_list_instances`. If no instance has `AiDevHelpers.slnx` open, call
   `mcp__aidevtools__ide_open_solution` with the full path of `AiDevHelpers.slnx`, then list again. Stop if still none.
2. **Changed files and affected tests.** Call `mcp__aidevtools__git_status` with the repo folder, then
   `mcp__aidevtools__test_affected` with the repo root (it reads the changed files from git). Note the test projects it lists.
3. **Lint and resources.** Call `mcp__aidevtools__lint_check` and `mcp__aidevtools__resx_check` with the repo root. Stop on
   any `error` finding or any `duplicate` or `missing` resx key. For each changed `.cs` file, call `mcp__aidevtools__string_scan`
   and move any new user-facing literal to a `Strings.resx`.
   Then call `mcp__aidevtools__localization_sync_check` with the repo root. Stop on any `missing` or `placeholder-mismatch` finding;
   report `untranslated` without blocking.
   Then call `mcp__aidevtools__project_audit` with `root` set to the repo root. Stop on any finding with `severity` `error`
   (framework, property, analyzer, test SDK); `info` and `warning` findings are reported, not blocking.
4. **Build.** Call `mcp__aidevtools__ide_build` with `solutionPath` set to `AiDevHelpers.slnx`. If it fails, call
   `mcp__aidevtools__ide_get_build_diagnostics` with `severity` set to `error` and stop.
   If `ide_build` itself errors, build with Visual Studio's Framework MSBuild instead:
   `"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" AiDevHelpers.slnx -restore -p:Configuration=Release`.
   Do not use `dotnet build` for the solution: it fails on `src/Installers/Vs2026/Installer` (MSB4018, the VSSDK task needs the Framework MSBuild).
5. **Test.** If step 2 listed no test project, skip this step. Otherwise call `mcp__aidevtools__ide_run_tests` with
   `solutionPath` set to `AiDevHelpers.slnx`, then `mcp__aidevtools__ide_get_test_results` with the same `solutionPath`.
   In Test Explorer mode, `mcp__aidevtools__ide_get_test_summary` gives the counts and the failing test names. Stop if any test failed.
6. **TypeScript, only when a `.ts` file or a `package.json` changed.** There is no aidevtools step for npm, so run the
   same npm commands as the full gate for the two projects, in a terminal:
   - `npm --prefix src/Installers/VsCode/Core/AiDevTools.VsCode.Core run lint` and `... run test`
   - `npm --prefix src/Installers/VsCode/Installer/AiDevTools.VsCode run build`, `... run lint` and `... run test`

## Not in the lite gate

`dotnet format --verify-no-changes`, `dotnet list package --outdated`, and the Release-configuration build. The full gate runs them.
