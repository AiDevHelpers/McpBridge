# Full gate

Run before a PR to `dev` or `main`. One command per line in the `gate` block; a line that starts with `!` is advisory
(it may fail without stopping the gate). The gate stops at the first failing step.

Close running `AiDevHelpers.MCP` processes first: they lock the server's output files and fail the build.

```gate
! dotnet list AiDevHelpers.slnx package --outdated
pwsh -NoProfile -File scripts/gate-build.ps1
dotnet format AiDevHelpers.slnx --verify-no-changes --no-restore
dotnet test AiDevHelpers.slnx -c Release --no-build --filter "Category!=Tpm"
npm --prefix src/Installers/VsCode/Core/AiDevTools.VsCode.Core ci
npm --prefix src/Installers/VsCode/Core/AiDevTools.VsCode.Core run lint
npm --prefix src/Installers/VsCode/Core/AiDevTools.VsCode.Core test
npm --prefix src/Installers/VsCode/Installer/AiDevTools.VsCode ci
npm --prefix src/Installers/VsCode/Installer/AiDevTools.VsCode run build
npm --prefix src/Installers/VsCode/Installer/AiDevTools.VsCode run lint
npm --prefix src/Installers/VsCode/Installer/AiDevTools.VsCode test
```

## Steps through aidevtools

The `gate` block above is what `gate_run` runs. The same gate through the aidevtools MCP tools, in an open Visual Studio
instance on the solution, is below. Stop at the first failing step. Close the running `AiDevHelpers.MCP` servers first.

1. **Instance.** `mcp__aidevtools__ide_list_instances`; if `AiDevHelpers.slnx` is not open, `mcp__aidevtools__ide_open_solution`.
2. **Release build.** `mcp__aidevtools__ide_set_configuration` with `Release`, then `mcp__aidevtools__ide_build`. On failure,
   `mcp__aidevtools__ide_get_build_diagnostics` with severity `error`.
3. **Warnings.** `mcp__aidevtools__ide_get_build_diagnostics` with severity `warning`. Any warning fails the gate.
4. **Tests.** `mcp__aidevtools__ide_run_tests`, then `mcp__aidevtools__ide_get_test_results`. Any failure fails the gate. The
   `Category!=Tpm` filter is set by the CI command; this path runs every test, so run the shell `dotnet test` line when the
   TPM tests would fail on this machine.
5. **Format, outdated packages, npm.** No aidevtools tool covers these. Run the matching lines from the `gate` block:
   `dotnet format ... --verify-no-changes`, `dotnet list ... package --outdated` (advisory), and the npm lines for both TypeScript projects.
