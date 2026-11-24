# McpBridge

## Dogfood the aidevtools MCP tools

This repo builds the aidevtools MCP server and the VS/VS Code bridge. Use them for your own work here.

- Required: use `mcp__aidevtools__*` tools for anything they cover (build, clean, test, publish, lint, format, debugging, reading build or test output). Use `dotnet`, `pwsh`, or the shell only where no tool covers the step.
- Before each commit run the lite gate in `.claude/reference/lite-gate.md` (it uses the aidevtools tools). Before a PR to `dev` or `main` run the full gate in `.claude/reference/full-gate.md`.
- To check a change in the real IDEs, launch or drive the Visual Studio and VS Code instances through the bridge (`ide_launch_instance`, `ide_list_instances`, `terminal_session`), not only unit tests.
- The `Category=Tpm` tests need administrator rights and a TPM. Run them in an elevated `terminal_session` (`action: open`, `elevated: true`): `dotnet test src/ScriptHelper/tests/AiDevHelpers.ScriptHelper.Core.Tests.csproj -c Release --filter "Category=Tpm" -p:RunSettingsFilePath=`. `dotnet test` excludes them through `.runsettings`, and Test Explorer (which ignores that filter) shows the key-creation test as skipped when not elevated; the full gate runs them elevated through `scripts/gate-tpm.ps1`, so only the tests that need it are elevated.
- Test Explorer is the default test path. Run with `ide_run_tests`, then read the outcome with `ide_get_test_results` (it reads the Tests pane: counts, failing names, `failure_details` messages). `ide_get_test_summary` gives the same counts. The `DotnetTest` mode and its TRX file are optional.
- Required: install a changed extension with `scripts/update-vs-extension.ps1` and exactly one installer run (close every VS first). Never start `VsixInstaller` per instance; the one dialog lists each VS 18 instance, tick all. The extension is loaded by Community and Insiders, so check both when it changes. Pin the bridge's `System.Text.Json` to the version VS ships: a newer one stops `BridgePackage` loading (see the csproj comment).
- When a tool is missing, broken, or worse than the raw command, add a row to the table in `docs/dogfood.md` (manual step, today, candidate tool), then use the fallback. Do not fall back silently.
- Close running `AiDevHelpers.MCP` processes by specific PID before a build; they lock the output files.
