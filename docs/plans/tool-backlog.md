# Tool backlog from dogfooding

Steps done by hand this session that a tool should cover. Add a line each time a manual step repeats.

| # | Manual step | Today | Candidate tool |
|---|---|---|---|
| 1 | Find hardcoded user-facing literals in one folder | grep by hand, then judge each hit | `string_scan` (file or folder): list literals not in a resx, with the matching key if one exists |
| 2 | Check resx keys for duplicates | Only found at build (MSB3568) | `resx_check`: duplicate names, unused keys, missing keys |
| 3 | Find MSBuild and the VS install | vswhere by hand, passes `-MSBuildPath` | `build_env`: VS install, MSBuild path, VSSDK targets present |
| 4 | Read a failed CI step | `gh run view --log-failed` and grep | `ci_failure` (PR number): failing job, first error lines |
| 5 | Check PR checks while waiting | `gh pr checks --watch` | `pr_checks` (PR number): status per job, no watch loop |
| 6 | Pick tests for a change | Not used; ran full runs | `test_affected` (exposed in MCP Core, not loaded in this session) |
| 7 | Git status and commit/push | Bash `git` | `git_status` and `git_plan`/`git_confirm` (in MCP Core, not loaded in this session) |
| 8 | Read failed tests after a Test Explorer run | `ide_search_output` on the `Tests` pane for `[FAIL]`, since `ide_get_test_summary` gives counts only and `ide_get_test_results` finds no TRX | `ide_get_test_summary` returns failing names and messages |
| 9 | Launch a second VS on a scratch `.sln` for the live mutation checks | `ide_launch_instance` waited 120 s and reported no bridge; no new `devenv` started and the open instance kept its solution | `ide_launch_instance` that starts a new instance (or says why it cannot), so `docs/self_test.md` can run its mutation checks |

Open: confirm which MCP Core tools a session can load, so the work above can be dogfooded.
