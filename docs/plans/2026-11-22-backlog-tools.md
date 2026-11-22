# Plan: build the dogfooding backlog and close what remains

Source: `docs/plans/tool-backlog.md` (eight manual steps that should be tools) and the remaining items after PR #4.
Status: draft, not approved. Q1 and Q2 answered.

## Open questions

- Q1 Package bumps (answered). The updater must not move a .NET 10 project to a .NET 11 package; every other outdated package is updated.
- Q2 Where `gh` tools live (answered): `SourceControlHelper`, no new project, so the slnx, CI pack list and docs test stay as they are.

## Conventions every unit follows

- Logic in a helper library with xUnit tests; the MCP tool in `src/MCP/Core/src` is a thin `[McpServerToolType]` wrapper.
- User-facing text in `Resources/Strings.resx`; `HardCodedStringTests` must pass.
- Each new tool is listed in `docs/ide-bridge.md` (or the README tool list) so `DocsMatchCodeTests` passes.
- Gate after each unit: build, format check, tests (lite gate; full gate before the PR).
- One commit per unit, pushed to `wip`. One PR to `dev` when the batch is done.

## Units

1. **Fix `git_status` empty error.** Reproduce: `git_status` returned `ok:false`, `error:""` for the repo while git works in a shell. Find why git exits non-zero with no output in the server process (working directory, PATH, `safe.directory`). Make `StatusAsync` report the exit code when output is empty. Test: a fake runner returning exit 128 with empty output yields a non-empty error.
2. **`string_scan` (backlog 1).** Move the scanner in `HardCodedStringTests` into `LintHelper` as a reusable service: given a file or folder, list multi-word literals not in the allowlist, with the matching resx key if the text is already a resource. The test now calls the service. Tool: `string_scan(path)`.
3. **`resx_check` (backlog 2).** In `LintHelper`: duplicate `name` entries, keys used in code but missing, keys never used. Tests with temp resx files. Tool: `resx_check(root)`. Wire the duplicate check into `lint_check`.
4. **`build_env` (backlog 3).** In `ExtensionsHelper` (it already finds Visual Studio): VS installs from vswhere, MSBuild path, whether `Microsoft.VsSDK.targets` exists per install. Tool: `build_env()`. `scripts/build-vs-extension-vsix.ps1` stays as is.
5. **`pr_checks` and `ci_failure` (backlog 4 and 5).** In `SourceControlHelper`, over `gh`: `pr_checks(pr)` returns each job's status; `ci_failure(pr)` returns the failing job and its first error lines from `gh run view --log-failed`. Tests with a fake `gh` runner and captured output. Read-only; no merge tool.
6. **Failing names in `ide_get_test_summary` (backlog 8).** Extend `TestSummaryParser` to read `[FAIL]` lines from the Tests output pane when present. Tests with captured pane text.
7. **Use `test_affected` and `git_*` in the lite gate (backlog 6 and 7).** Add a step to `.claude/reference/lite-gate.md`: run `test_affected` and run only those projects. Mark backlog rows done.
8. **Docs and backlog.** Update `tool-backlog.md` (done rows), `docs/ide-bridge.md`, and the README tool list.
9. **Package update rule.** In `PackageHelper`: given a project's target framework and the outdated list, choose the newest version whose major does not exceed the framework's .NET major (net10.0 allows 10.x, blocks 11.x). Tests: net10.0 with 11.0.0 and 10.0.3 available picks 10.0.3; net48 follows the package's own supported range. Tool: `package_outdated(root)` lists each update with the chosen version and the blocked ones with the reason. Updates only; it does not edit files.
10. **Apply the updates.** Use the rule from unit 9 on this repo: PolySharp to 1.17.1, xunit.runner.visualstudio to 4.0.1, and System.Text.Json and Microsoft.Extensions.Options to the newest 10.x, not 11.0.0. Full gate after.

## Needs you (not in the units)

- Live checks: Visual Studio bridge loads and registers; VS bridge actions inside Visual Studio; `git_status` through VS Code (`SolutionPath` is the workspace folder); `scripts/update-aidevtools.ps1` against real servers.
- Set Insiders `test.hotReload` through its options if the key disappears.
- Approve any merge into `dev` or `main`.

## Done when

All ten units are committed on `wip`, the full gate passes, `DocsMatchCodeTests` and `HardCodedStringTests` pass, and one PR to `dev` has green `build-and-test` and `vscode-extension`.

## Review findings folded in (Sonnet, read-only)

- Every tool unit also updates the owning helper's `src/Helpers/<H>/src/SelfTest.md` (`SelfTestCoverageTests`) and adds its row to `docs/ide-bridge.md` (`DocsMatchCodeTests`) in the same unit. Unit 8 is then only `tool-backlog.md` and the README.
- Unit 1: the fix is in `StatusAsync` (exit code when git prints nothing) and `ProcessGitRunner` (timeout text, `Success`). The server-side cause still needs a reproduction once the `aidevtools` servers are back.
- Unit 2: the service takes the allowlist as a parameter (LintHelper cannot know the test path); its own regex literals get allowlist entries; the resx-key lookup is new.
- Unit 3: move `EveryStringsKeyUsedInCodeExistsInTheHelpersResx` into the service; update the `lint_check` description in `LintTools.cs` and its docs row.
- Unit 4: vswhere through `ICommandRunner` in `ExtensionsHelper`; `BuildEnv` in `ExtensionTools.cs`; targets at `MSBuild\Microsoft\VisualStudio\v*\VSSDK\Microsoft.VsSDK.targets`.
- Unit 5: an `IGhRunner` over `CommandRunner`. `ci_failure`: `gh pr checks --json` for the failing run id, then `gh run view <id> --log-failed`, capped lines. A message for `gh` missing or not signed in. Tools in `SourceControlTools.cs`.
- Unit 6: change the `searchOutput` pattern in `IdeGetTestSummary` to `Test run finished|\[FAIL\]`; update the tool description and docs. The parser stays in MCP Core.
- Unit 7: runs after units 1 and 5.
- Unit 9: a runner interface for `dotnet list package --outdated --format json` (after a restore), so tests fake it. Rule: the cap is the highest `netX.Y` major among the project's target frameworks (net10.0 caps at 10); with no such framework there is no cap. The Bridge project targets `net48` and `net10.0-windows`, so its cap is 10. Tool class `PackageTools`.
- Unit 10: versions are per csproj (no `Directory.Packages.props`). Edit every test csproj for `xunit.runner.visualstudio`, the Bridge csproj for `System.Text.Json` and `PolySharp`, and check whether `Microsoft.Extensions.Options` (already 10.0.9 in Common) is outdated at all.
- Backlog row 9 (`ide_launch_instance` launch failure) is deferred: no unit.

## Progress

- Unit 1: done in code and tests (red then green). Server-side reproduction pending.
