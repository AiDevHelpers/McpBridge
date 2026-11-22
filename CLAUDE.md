# McpBridge

## Dogfood the aidevtools MCP tools

This repo builds the aidevtools MCP server and the VS/VS Code bridge. Use them for your own work here.

- Build, clean, test, publish, and read build or test output with `mcp__aidevtools__ide_*` and `test_*` tools. Fall back to `dotnet` or the shell only where no tool covers the step.
- Before each commit run the lite gate in `.claude/reference/lite-gate.md` (it uses the aidevtools tools). Before a PR to `dev` or `main` run the full gate in `.claude/reference/full-gate.md`.
- To check a change in the real IDEs, launch or drive the Visual Studio and VS Code instances through the bridge (`ide_launch_instance`, `ide_list_instances`, `terminal_session`), not only unit tests.
- The `Category=Tpm` tests need administrator rights and a TPM. Run them in an elevated `terminal_session` (`action: open`, `elevated: true`): `dotnet test src/ScriptHelper/tests/AiDevHelpers.ScriptHelper.Core.Tests.csproj -c Release --filter "Category=Tpm"`. The gates exclude them.
- When a tool is missing, broken, or worse than the raw command, add a row to the table in `docs/plans/tool-backlog.md` (manual step, today, candidate tool), then use the fallback. Do not fall back silently.
- Close running `AiDevHelpers.MCP` processes by specific PID before a build; they lock the output files.
