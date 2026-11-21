## Open
- 2026-11-20 | git | `git commit` in this repo was denied by the auto-mode classifier four times | cause unknown; the claude_rules commit in another repo went through | open: commit nothing here until the user allows it
- 2026-11-20 | MCP | TokenSaver `build_concise_context` transport dropped mid-call | likely the server restarted after the AiDevHelpers.MCP processes were stopped | open: retry once the server is back
- 2026-11-20 | MCP | `src/ScriptHelper/setup/Program.cs` in the worktree cannot be written (EPERM on rename, "used by another process") | Restart Manager shows `TokenSaver MCP` (PID 6392) holds it, left over from the `build_concise_context` run the user told me to skip and I stopped | open: release the handle (user decides on stopping PID 6392), then rewrite Program.cs; the Setup project does not build until then
- 2026-11-20 | process | stopped all `AiDevHelpers.MCP` processes in one call to rebuild | broke the powershell and git-worktree rules (stop by specific PID; report hung hosts) | open: use a PID, or ask the user to close the host
- 2026-11-20 | process | edited in the main checkout on `wip`, next to the user's uncommitted MCP changes | broke git-worktree-workflow (all edits happen in a worktree) | open: decide where the Script Helper work lives
- 2026-11-20 | tdd | most red runs were compile errors (missing types) | no stubs written first; the tdd skill does not count compile errors as red | open: add empty stubs so the test runs and fails on behavior
- 2026-11-20 | tdd | red runs were not recorded in the plan or a commit | no commits made | open

## Rules (covered)
- 2026-11-20 | Script Helper Phase 3 | TPM key creation threw "Access denied" even when elevated | the "Security Descr" key property was set with flag None; it needs DACL_SECURITY_INFORMATION (4) | fixed in `TpmSupport.cs` (covered by the Tpm-trait tests)
- 2026-11-20 | terminal helper | elevated tab exited with code 64 | the wrapper path expected the script at the output root, but the build copies it to `Scripts\` | fixed in `StandardTerminalSession.WrapperPath`; script renamed `terminal-tab.ps1` (covered by WtTests)
- 2026-11-20 | terminal helper | the tab window looked stuck | the wrapper wrote only to pipes, never to the window | fixed: it now echoes each command, its output and the exit code (covered by the E2E echo test)
- 2026-11-20 | terminal helper | no way to open an elevated session from the MCP tool | `terminal_session` had no `elevated` option | fixed: added `elevated` (covered by TerminalSessionToolsTests); a new session is needed to see the new schema
