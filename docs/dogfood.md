# Tool backlog from dogfooding

Steps done by hand this session that a tool should cover. Add a line each time a manual step repeats.

| # | Manual step | Today | Candidate tool |
|---|---|---|---|
| 23 | Lite gate on a fresh worktree with no open IDE | `ide_open_solution` on `AiDevHelpers.slnx` returned ok, but `ide_list_instances` stayed at count 0 across two checks, so `ide_build`, `ide_run_tests` and the Tests pane steps had no instance to use. Ran `dotnet test` on the three affected projects instead (all pass). | Make `ide_open_solution` return only once the bridge answers, or report "bridge not ready"; `ide_list_instances` should show an instance that is still starting. **Built in code**: `ide_open_solution` writes a starting marker for the devenv it starts, and `ide_list_instances` lists it under `starting` until its bridge registers (MCP Core 264/264, Bridge 34/34 pass). The solution-wide build was blocked by running `AiDevHelpers.MCP` servers holding their output files; not checked live. |
