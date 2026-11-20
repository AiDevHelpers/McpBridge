# AiDevHelpers.McpBridge MCP Tools

The stdio MCP server exposes the tools below. Read the live `tools/list` schemas before calling a tool; this guide explains intent, while the schemas define current parameters. IDE tools target Visual Studio instances and accept `solutionPath` where applicable. When multiple instances are open, pass `solutionPath` to select the intended instance.

## IDE instances and solution

| Tool | Purpose |
| --- | --- |
| `ide_list_instances` | List running Visual Studio instances, process IDs, and open solution paths. |
| `ide_launch_instance` | Open a solution in Visual Studio, reusing an instance that already has it open. |
| `ide_kill_instance` | Save all documents and then close an instance; refuses to close if documents remain unsaved. |
| `ide_set_startup_project` | Choose the solution's startup project. |
| `ide_list_configurations` | List solution configurations and the active configuration. |
| `ide_set_configuration` | Change the active solution configuration. |

## Build, tests, and output

| Tool | Purpose |
| --- | --- |
| `ide_build` | Build the solution in Visual Studio. Use `ide_get_build_diagnostics` to inspect the Error List. |
| `ide_get_build_diagnostics` | Read build errors and warnings, optionally filtered by severity. |
| `ide_clean` | Clean the solution using its active configuration. |
| `ide_publish` | Publish the startup project using the active configuration. This starts a publish process. |
| `ide_kill_process` | Stop a process started by `ide_clean` or `ide_publish`; only accepts a process ID returned by those operations. |
| `ide_run_tests` | Run the solution tests through Test Explorer by default, or `dotnet test` with TRX when configured. |
| `ide_get_test_results` | Read counts and failure messages from the latest TRX file for a solution. |
| `ide_get_output` | Read recent lines from a Build or Debug output pane. |
| `ide_search_output` | Search an output pane's lines with a regular expression. |
| `ide_clear_output` | Clear an output pane. |

## Editing

| Tool | Purpose |
| --- | --- |
| `ide_format_document` | Format the active document. |
| `ide_code_cleanup` | Run Visual Studio Code Cleanup on the active document. |

## Debugger

| Tool | Purpose |
| --- | --- |
| `ide_bridge_health` | Check bridge listener health, request counters, start/last-request times, and solution. |
| `ide_get_debug_state` | Read debugger mode and current function. |
| `ide_start_debugging` | Start debugging the solution. |
| `ide_stop_debugging` | Stop the current debugging session. |
| `ide_set_breakpoint` | Set a breakpoint at a source file and 1-based line. |
| `ide_remove_breakpoint` | Remove breakpoints at a source file and line. |
| `ide_list_breakpoints` | List the instance's breakpoints. |
| `ide_step` | Step over, into, or out while stopped at a breakpoint. |
| `ide_get_locals` | Read local variables in the current stack frame while stopped at a breakpoint. |
| `ide_evaluate` | Evaluate a C# expression in the current stack frame while stopped. Evaluation may have side effects. |
| `ide_add_watch` | Add a watch expression maintained by the bridge extension. |
| `ide_remove_watch` | Remove a watch expression. |
| `ide_list_watches` | List watch expressions and their values when stopped at a breakpoint. |

## Terminal sessions

| Tool | Purpose |
| --- | --- |
| `terminal_session` | Open, create tabs in, execute commands in, list, and close Windows Terminal sessions. |

`terminal_session` manages Windows Terminal sessions. `action` is `open`, `tab`, `exec`, `list`, or `close`. Open chooses `InteractiveUser` (visible to the logged-on user, default) or `ServiceAccount` (headless). `tab` creates another tab; `exec` runs a command in a session and tab; `list` reports active sessions; `close` ends a session. Load its schema for the required IDs and optional timeout fields. Opening can create a visible terminal window; executing runs the supplied command.

## Safe use

- Start with `ide_list_instances` and read-only inspection tools. Select a target explicitly when several solutions are open.
- Tools that build, clean, publish, run tests, edit documents, change debugger state, launch or close IDEs, or run terminal commands can change user state. Use them only when the requested task authorizes that action and target.
- Never use `ide_kill_instance` or `ide_kill_process` against an unrelated user process. `ide_kill_process` is limited to processes previously started for clean or publish.
- `ide_get_locals`, `ide_evaluate`, stepping, and watch values require the debugger to be stopped as described by their schemas.

The full test procedure is available as `docs://ide-bridge/mcp-self-test`.

The bridge's own diagnostics are written to the Visual Studio Output pane named `AiDevHelpers IDE Bridge`. The MCP log tools expose that pane as the `vsout:AiDevHelpers IDE Bridge` channel while the IDE instance is connected; use `LogChannels` to confirm availability before querying it.
