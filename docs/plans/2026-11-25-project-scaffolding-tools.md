# Project scaffolding tools: plan, 2026-11-25

Status: revised after Sonnet review, not approved. Implementation plan: `2026-11-25-project-scaffolding-impl.md`.

## Goal

MCP tools to add any .NET project to a solution and keep it correct: list project types, audit projects against the repo's
standards, add a project or a test project, and check and install .NET/VS workloads. A project whose framework is not allowed
is refused unless a committed exception allows it.

## Decisions (from the user, final)

- Tools live in the AiDevHelpers MCP server (`src/MCP/Core/src`); logic in a new helper `src/Helpers/ProjectHelper` (`src/` + `tests/`).
- The standards live in the repo's settings folder and are read through the existing repo-settings mechanism as typed,
  validated settings. No other code parses that JSON. Folder name: the code uses `.aidevtools` (`RepoSettingsFile.FolderName`),
  not `.aidevhelpers`; this plan uses `.aidevtools/project-standards.json` unless the user wants a rename (open question, impl plan U1).
- Project add is generic for any .NET project: no fixed layout. The caller gives folder and solution folder; defaults are
  inferred from the solution's existing pattern, never hard-coded.
- Two PRs. PR1: standards, `project_types_list`, `project_audit`, framework guard, `project_add_plan/confirm`,
  `project_add_test_plan/confirm`. PR2: `workload_list`, `workload_check`, `workload_install_plan/confirm`.
- Elevated workload install follows the `ToolInstallService` / `IElevInstaller` pattern (an `elev.exe` verb and a generated
  script in the admin-only tools folder), not `run_elevated_script`. The script is pinned by SHA-256 (CLAUDE.md elevated-script
  rule), a tamper test is required, and the pin file is part of what the full gate reviews.
- Framework exceptions come only from committed config entries `(project, framework, reason)`. Tools never write them.
- The `tdd` skill update is a separate post-merge step through `instruction-placement`, outside this repo's PRs.
- Plan then confirm for every write. Read-only tools run directly.

## Review findings, verified against code

1. No single solution TFM. Today: `net10.0` (helpers), `net10.0-windows` (Common.Tests, Bridge.Tests, TerminalControl, MCP Core,
   MCP Server, ScriptHelper), `net48` (Installers/Vs2026 Core, Installer and their tests) and multi-target `net48;net10.0-windows`
   (Bridge). No `global.json`, no `Directory.Packages.props`; `Directory.Build.props` only sets `RunSettingsFilePath`.
   Standard = a base TFM (`net10.0`) plus a rule that allows a platform suffix (`-windows`) on it. Anything else needs a
   per-project, per-framework exception. The config is seeded with every existing exception so the repo's own projects pass
   `project_audit`.
2. Package versions have no central source. Add to `PackageHelper` a function that reads package versions across all csproj
   files. Conflict rule: the version most projects use wins; a tie goes to the newest version `PackageUpdateRule.NewestAllowed`
   permits for the project's frameworks. The audit reports projects that differ.
3. Two `ICommandRunner` interfaces exist (ExtensionsHelper, LintHelper) and Common has the static `CommandRunner`. Reuse: ProjectHelper
   references Common only and takes a delegate over Common's `CommandRequest`/`CommandResult` (default `CommandRunner.RunAsync`).
   No third interface; tests pass a recording lambda.
4. Plan/confirm tokens are in memory (`ExtensionsService._pending`, `ToolInstallService._pending`). A token proves the plan was
   shown, not that it is still valid: confirm recomputes the guard, the standards, and the on-disk state (target folder still
   empty, solution unchanged) and refuses on drift. Tokens expire (15 minutes, as `ToolInstallService`).
5. Test-project discovery is automatic (`AffectedTests.IsTestProject` matches `Microsoft.NET.Test.Sdk` or `IsTestProject`). The
   generated test project includes the test SDK; there is no other wiring.
6. `.slnx` is XML, and `SolutionProjects` only reads it. The primary path writes the XML directly (add `<Project Path>` inside the
   right `<Folder>`), with a round-trip formatting test. `dotnet sln` is not used.
7. The test project name derives from the source project (`AiDevHelpers.Helpers.X` gives `AiDevHelpers.Helpers.X.Tests`).
8. `dotnet new` adds `Class1.cs`, `UnitTest1.cs` and similar. Confirm deletes template stubs, builds with `-warnaserror`
   (as `GatePlanner`), and on a failed build rolls back: remove the new folder and the solution entry, then report.
9. A new helper must be added to `AiDevHelpers.slnx`, the `AiDevHelpers.MCP.Core.csproj` references, `docs/self_test.md`, its own
   `SelfTest.md`, `SelfTestTools.HelperNames`, `SelfTestCoverageTests.ToolToHelper`, and the pack list that
   `DocsMatchCodeTests` checks. Each csproj carries `PackageReference Update="Nerdbank.GitVersioning"`. The coverage and docs tests are
   run first as red tests.
10. Refusing a VS workload install while VS runs is unverified. It stays in PR2 phase 0, and needs a process-probe seam
    (`ITerminalProbe`-style) so tests can fake it.

## Standards file (typed, validated)

`.aidevtools/project-standards.json`, read by a new `ProjectStandardsFile` in `src/Common/src` (same shape as `ElevationFiles`:
never throws, returns a typed record plus warnings) and surfaced through `RepoSettings.Resolve`. Fields: `baseFramework`,
`allowedPlatformSuffixes`, `langVersion`, `nullable`, `implicitUsings`, `analysisLevel`, `treatWarningsAsErrors`,
`requiredPackages` (id only; version from the cross-project rule), `exceptions[] { project, framework, reason }`. Validation:
TFM syntax, non-empty reason, repo-relative safe project paths, unknown keys warn. A bad file yields no standards plus a
warning, so the guard fails closed (adds are refused with the reason). Hand-parsing elsewhere is a test failure.

## Cross-cutting rules

- Each tool: `[McpServerToolType]` method with `[Description]`, a `Strings.resx` entry per user string, a row in
  `src/Common/src/Resources/mcp-tools.md`, and a self-test entry.
- Edit project files with `System.Xml.Linq`, keeping each file's newline style; new files LF.
- Tests first (`tdd`). Docs: `docs/dogfood.md` tool-use rows, `CHANGELOG.md`, `docs/features.md`, `docs/prereqs.md` if needed.
- Full gate before each PR.

## Out of scope

Writing exception entries, removing projects, upgrading the solution framework, the VS Code bridge, the `tdd` skill edit.

## Risks

- `dotnet new` / `vswhere` / `dotnet workload` output changes: parsers are tested on saved fixtures and fail with a clear error.
- A cloned repo is untrusted input: standards only steer plans, every write still needs a confirm, and project paths are checked
  with `ElevationFiles.IsSafePath`-style rules.
- The elevated install adds a new `elev.exe` verb and allowlist surface: table-driven, name-only input, tamper-tested.
