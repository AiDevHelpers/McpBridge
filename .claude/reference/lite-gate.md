# Lite gate

Run before each commit. It is the fast check; the full gate in `full-gate.md` still runs before a PR to `dev` or `main`.

The steps use the aidevtools MCP tools, so they run in an open Visual Studio instance on the solution. This file is
prose on purpose: `GatePlanner` reads only the `gate` block in `full-gate.md`, so these steps are not run by `gate_run`.

## Steps

1. **Find the instance.** Call `mcp__aidevtools__ide_list_instances`. If no instance has `AiDevHelpers.slnx` open, call
   `mcp__aidevtools__ide_open_solution` with the full path of `AiDevHelpers.slnx`, then list again. Stop if still none.
2. **Build.** Call `mcp__aidevtools__ide_build` with `solutionPath` set to `AiDevHelpers.slnx`. If it fails, call
   `mcp__aidevtools__ide_get_build_diagnostics` with `severity` set to `error` and stop.
3. **Test.** Call `mcp__aidevtools__ide_run_tests` with `solutionPath` set to `AiDevHelpers.slnx`, then
   `mcp__aidevtools__ide_get_test_results` with the same `solutionPath`. Stop if any test failed.
4. **TypeScript, only when a `.ts` file or a `package.json` changed.** There is no aidevtools step for npm, so run the
   same npm commands as the full gate for the two projects, in a terminal:
   - `npm --prefix src/Installers/VsCode/Core/AiDevTools.VsCode.Core run lint` and `... run test`
   - `npm --prefix src/Installers/VsCode/Installer/AiDevTools.VsCode run build`, `... run lint` and `... run test`

## Not in the lite gate

`dotnet format --verify-no-changes`, `dotnet list package --outdated`, and the Release-configuration build. The full gate runs them.
