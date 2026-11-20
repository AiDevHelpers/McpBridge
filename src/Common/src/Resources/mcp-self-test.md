# AiDevHelpers.McpBridge MCP Full Self-Test

Use this checklist to verify the bridge MCP server, tool registration, terminal subsystem, and (when available) a live IDE bridge. Read live MCP tool schemas before calls. Record every check as `PASS`, `FAIL`, `SKIPPED`, or `NOT RUN`, including exact commands or arguments and concise results. Do not convert unavailable live IDE checks into failures.

## 1. Safety and prerequisites

- Run from the repository root with the .NET 10 SDK and required Visual Studio build tools installed.
- Use the repository's test suites for automated checks. They isolate test state; do not use a user's open solution for destructive smoke checks.
- Live checks begin read-only. Do not launch, close, build, publish, run tests, modify documents, manipulate breakpoints, or execute terminal commands unless the operator requested that specific action and supplied an appropriate scratch target.
- If no IDE instance is registered, record live IDE checks as `SKIPPED: no registered IDE instance` and continue with automated and MCP protocol checks.

## 2. Build and automated tests

From the repository root, build the full solution with Visual Studio 18 MSBuild (the VSIX VSSDK tasks fail under `dotnet build` task hosting):

```powershell
$vswhere = "C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe"
$installation = & $vswhere -latest -prerelease -products '*' -version '[18.0,19.0)' -property installationPath
$msbuild = Join-Path $installation "MSBuild\Current\Bin\MSBuild.exe"
& $msbuild AiDevHelpers.slnx /restore /p:Configuration=Release /verbosity:minimal
if ($LASTEXITCODE -ne 0) { throw "Solution build failed: $LASTEXITCODE" }

pwsh -NoProfile -File scripts/build-vs-extension-vsix.ps1 -Configuration Release
dotnet test tests/AiDevHelpers.Helpers.LogHelper.Tests/AiDevHelpers.Helpers.LogHelper.Tests.csproj
dotnet test tests/AiDevHelpers.Bridge.Tests/AiDevHelpers.Bridge.Tests.csproj
dotnet test tests/AiDevHelpers.Helpers.TerminalControl.Tests/AiDevHelpers.Helpers.TerminalControl.Tests.csproj
```

Record exit status and reported test counts for each. The bridge MCP test project includes tool validation and integration coverage; TerminalControl covers session, pipe, and wrapper behavior; LogHelper covers log processing.

## 3. MCP protocol and inventory

Start `src/AiDevHelpers.MCP` using its normal stdio transport and an MCP client. Confirm:

1. The stdio initialize handshake completes and the server responds to `tools/list`.
2. The list includes all currently registered IDE and terminal tools. Compare the names with `docs://ide-bridge/mcp-tools` and use the live schemas as authority.
3. `resources/list` includes `docs://ide-bridge/mcp-tools` and `docs://ide-bridge/mcp-self-test`.
4. Read both resources and confirm they return non-empty Markdown with `text/markdown` content.
5. Call `ide_list_instances`; confirm a valid result, even if its list is empty.

If no MCP client or runnable bridge process is available, record the protocol section as `NOT RUN` and state why. Do not claim a resource/tool protocol check based only on a successful compilation.

## 4. Read-only live IDE checks

When `ide_list_instances` reports an instance, choose one and pass its `solutionPath` when needed. Confirm:

- `ide_bridge_health` responds and returns health/request metadata.
- `ide_get_output` and `ide_search_output` read an existing output pane, preferably Build or Debug. If those panes are unavailable or return a pane-specific COM/automation error, try `AiDevHelpers IDE Bridge` and report both results. If no pane can be read, mark these checks `SKIPPED`.
- `ide_get_build_diagnostics` and `ide_list_configurations` return valid results.
- `ide_get_debug_state`, `ide_list_breakpoints`, and `ide_list_watches` return valid results. Call `ide_get_locals` only if the debugger is already stopped at a breakpoint.
- `ide_get_test_results` reads the specified solution's latest results; record “no TRX found” as a valid empty-state response if applicable.

Do not run `ide_run_tests`, `ide_build`, or other state-changing tools during this read-only section.

## 5. Optional authorized action checks

Only perform a check when the operator explicitly requests it and a scratch solution/session is selected. Agree on the expected change and cleanup first. Possible checks include build/diagnostics, tests/TRX, formatting/cleanup, configuration/startup project, debugger operations, IDE launch/close, or terminal open/tab/exec/close. Capture before/after state and clean up only resources created by this check. Avoid publish, process termination, and closing an existing IDE unless specifically authorized.

### VSIX actions and launching a solution argument

The automated MCP tests verify routing/no-instance behavior, but they do not execute the VSIX `IdeActions` EnvDTE calls. `ide_format_document`, `ide_code_cleanup`, `ide_run_tests`, build/clean, and debugger mutations therefore need live integration checks in Visual Studio; a successful bridge reply alone does not prove the IDE action completed. Use a disposable scratch solution with a deliberately unformatted source file, an available Code Cleanup profile, a small test project, and a deterministic debugger breakpoint. Verify the file contents, Test Explorer result, build/clean outcome, and debugger state before and after each call. Record unsupported Visual Studio commands or missing profiles as failures.

To verify launching Visual Studio with a solution supplied as the tool argument:

1. Create or select a disposable `.sln` file that is not already open in Visual Studio. Record its full path as `$scratchSln`.
2. Call `ide_launch_instance` with exactly `{ "solutionPath": "$scratchSln" }` (replace the placeholder with the absolute path). Expect `ok: true`, `already_running: false`, and a returned `solution_path` matching the requested solution.
3. Call `ide_list_instances`; confirm the returned instance PID matches the launch result and its `solutionPath` is the scratch solution. Then call `ide_bridge_health` with that exact `solutionPath` to confirm the bridge is responding in the instance that loaded it.
4. Call `ide_launch_instance` again with the same path. Expect `already_running: true` and the same instance PID; this verifies reuse rather than duplicate launch.
5. Close only the scratch instance created by this check after confirming it has no unsaved documents. Do not call `ide_kill_instance` on an instance that was already running before the check.

This launch check is a live check: the automated test for a missing `.sln` only verifies rejection of a nonexistent path. It does not prove that Visual Studio opened a real solution or that the supplied argument reached the resulting instance.

## 6. Final report

Summarize build and test counts, MCP handshake/tool/resource inventory, live IDE availability and read-only results, and each skipped or failed check with its reason. Keep automated test success distinct from live IDE validation.
