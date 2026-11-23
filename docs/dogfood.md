# Tool backlog from dogfooding

Steps done by hand this session that a tool should cover. Add a line each time a manual step repeats.

| # | Manual step | Today | Candidate tool |
|---|---|---|---|
| 1 | Find hardcoded user-facing literals in one folder | grep by hand, then judge each hit | `string_scan` (file or folder): list literals not in a resx, with the matching key if one exists **done** |
| 2 | Check resx keys for duplicates | Only found at build (MSB3568) | `resx_check`: duplicate names, unused keys, missing keys **done** |
| 3 | Find MSBuild and the VS install | vswhere by hand, passes `-MSBuildPath` | `build_env`: VS install, MSBuild path, VSSDK targets present **done** |
| 4 | Read a failed CI step | `gh run view --log-failed` and grep | `ci_failure` (PR number): failing job, first error lines **done** |
| 5 | Check PR checks while waiting | `gh pr checks --watch` | `pr_checks` (PR number): status per job, no watch loop **done** |
| 6 | Pick tests for a change | Not used; ran full runs | `test_affected` (exposed in MCP Core, not loaded in this session) **done** |
| 7 | Git status and commit/push | Bash `git` | `git_status` and `git_plan`/`git_confirm` (in MCP Core, not loaded in this session) **done** |
| 8 | Read failed tests after a Test Explorer run | `ide_search_output` on the `Tests` pane for `[FAIL]`, since `ide_get_test_summary` gives counts only and `ide_get_test_results` finds no TRX | `ide_get_test_summary` returns failing names and messages **done** |
| 9 | Launch a second VS on a scratch `.sln` for the live mutation checks | `ide_launch_instance` waited 120 s and reported no bridge; no new `devenv` started and the open instance kept its solution | `ide_launch_instance` that starts a new instance (or says why it cannot), so `docs/self_test.md` can run its mutation checks. Fixed: `/edit` removed, a new instance starts. |
| 10 | Run Code Cleanup on a document | `ide_code_cleanup` returned `E_FAIL`: the command `Edit.RunCodeCleanup` does not exist in VS 18.0 | `ide_code_cleanup` that runs the default profile. Fixed and checked live: runs `EditorContextMenus.FileHealthIndicator.RunDefaultCodeCleanup`. |
| 11 | Run all tests after a build | `ide_run_tests` returned `E_FAIL` repeatedly after the first Test Explorer run of the session (even after waiting); fell back to `dotnet test` in the shell | `ide_run_tests` that reports why Test Explorer refused (busy, stale run, discovery) and retries once, or cancels a stuck run |
| 12 | Stop a hung test host that holds the test outputs | `ide_kill_process` refuses a PID the bridge did not start; the shell `Stop-Process` needed the user's yes | A tool that lists test hosts left by a finished run and stops them by PID with the user's confirmation |
| 13 | Run the VideoForensics debug script elevated | `run_elevated_script` refused: `elev.exe` not found next to the MCP server (`src/MCP/Server/src/bin/Release/net10.0-windows`). Fell back to `sudo` from `run_in_terminal`. | `run_elevated_script` that works from a fresh build, or a build step that copies `elev.exe`. |
| 14 | Open Visual Studio elevated so a script can attach to a LocalSystem service | `ide_launch_instance` starts a non-elevated instance; the elevated script's DTE lookup then finds no Visual Studio. | `ide_launch_instance` with `elevated: true` (UAC prompt), like `terminal_session` `elevated: true`. |
| 15 | Restore NuGet packages before a build or `package_outdated` | No aidevtools tool restores; ran `dotnet restore`/`dotnet build` in the shell and passed `--no-restore` to `dotnet format`. `package_outdated` even says "Restore first". | `package_restore` in PackageHelper: `dotnet restore` for the solution, returning ok, exit code and the output tail |
| 16 | Build and test while the solution's VS instance is busy | `ide_build` returned "The IDE instance did not accept the connection in time" for the registered McpBridge instance for several minutes, even after waiting; fell back to `dotnet` in the shell | `ide_build`/`ide_run_tests` that report the instance as busy or hung and offer to relaunch it, instead of only a timeout |
| 17 | Test a few projects while a whole-solution Test Explorer run is going | `ide_run_tests` runs every test project, takes minutes, stops the bridge answering, and its testhosts lock the Debug output so `dotnet build` of the same projects fails; ran `dotnet test <project> -c Release` in the shell | `ide_run_tests` with a project or filter argument, and a result call that says "still running" instead of "could not read the pane" |

Rows 1 to 8 are built (plan 2026-11-22-backlog-tools.md). Rows 9 and 10 are fixed.
