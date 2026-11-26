# Project scaffolding tools: implementation plan, 2026-11-25

Status: PR1 (U1–U10) and PR2 (U11–U14) merged to `wip` (PRs #10–#13). Follow-ups merged in #16 and #17. Source: `2026-11-25-project-scaffolding-tools.md` (revised). Checked against `RepoSettings.cs`,
`ElevationFiles.cs`, `RepoSettingsFile.cs`, `SolutionProjects.cs`, `ToolInstallService.cs`, `ScriptHelperTools.cs`
(`IElevInstaller`), `InstallToolFiles.cs`, `ExtensionTools.cs`, `PackageUpdates.cs`, `AffectedTests.cs`, `GatePlanner.cs`,
`SelfTestCoverageTests.cs`, `DocsMatchCodeTests.cs`, `scripts/gate-tpm.ps1`.

Two PRs, each on its own branch cut from `wip` (PR2 from `wip` after PR1 lands there). PR base follows the repo's `pr-promotion` rules. Every unit: tests first, then code, then `dotnet build -warnaserror` on the touched
projects via the aidevtools tools, then the lite gate, then commit. Strings go to `Strings.resx`; no hard-coded user text
(`HardCodedStringTests`).

## Decision: settings folder is renamed

The user chose `.aidevhelpers` and a hard rename with no fallback shim (no legacy-path code). The standards file is
`.aidevhelpers/project-standards.json`. Every `.aidevtools` reference below is read as `.aidevhelpers` after U0.

# PR1

## U0. Rename the settings folder `.aidevtools` to `.aidevhelpers`

- Goal: one settings folder name, `.aidevhelpers`, no legacy reads.
- Edit: `RepoSettingsFile.FolderName` and every other use (about 63 occurrences in 34 files: `ElevationFiles`, `RepoRules`,
  `RepoSettings`, `RepoSetupChecker`, `RepoSetupTools`, `RepoOpenService`, `IdeTools`, `IdeSolutionOpener`, ScriptHelper
  `AllowAllStore`/`ApprovalCommands`/`RepoSetup`, `Strings.resx`, tests, `.gitignore`, `docs/`, `CHANGELOG.md`). Prefer one
  constant over repeated literals where a literal is repeated.
- `git mv` the repo's own `.aidevtools` folder to `.aidevhelpers`. Other repos must be moved by hand; say so in `CHANGELOG.md`.
- Tests first: change the existing tests' expected paths to `.aidevhelpers` (red), then the code. Add a test that no `src` file
  contains the literal `.aidevtools`.
- Done when: lite gate green; grep for `.aidevtools` outside `CHANGELOG.md` finds nothing.
- Commit: `Rename the repo settings folder to .aidevhelpers`

## U1. Typed standards file

- Goal: a validated, typed read of the standards through the repo-settings mechanism.
- Create: `src/Common/src/ProjectStandardsFile.cs` (records `ProjectStandards`, `FrameworkException`; `Read(repoRoot)` returns
  standards plus warnings, never throws; `FilePath`), tests in `src/Common/tests/ProjectStandardsFileTests.cs`.
- Edit: `src/MCP/Core/src/RepoSettings.cs` (`RepoSettingsResult` gets `Standards`; `Resolve` fills it and merges warnings);
  `src/MCP/Core/tests` RepoSettings tests; `Strings.resx` (Common and Core).
- Tests first: valid file; missing file (no standards, no warning); malformed JSON; bad TFM; empty reason; unsafe project path;
  unknown key warns; comments and trailing commas accepted (as `ElevationFiles`); a grep-style test that no other `src` file
  opens `project-standards.json`.
- Done when: tests green; `RepoSettings.Resolve` returns the typed standards.
- Commit: `Common: typed, validated project standards read through repo settings`

## U2. Seed this repo's standards and exceptions

- Goal: the repo's own projects all pass the audit.
- Create: `.aidevhelpers/project-standards.json` with `baseFramework: net10.0`, `allowedPlatformSuffixes: ["-windows"]`, the
  properties the repo uses (read from the csproj files, not guessed), and one exception per project/framework not covered:
  `net48` for Installers/Vs2026 Core, Installer and both test projects; `net48` for `AiDevHelpers.Bridge`. Reason text cites
  the VS extension host and the `System.Text.Json` pin in `CLAUDE.md`.
- Tests first: `RepoStandardsSeedTests` (in `src/Common/tests`) loads the committed file, enumerates every csproj's
  `TargetFramework(s)`, and asserts each is allowed or excepted. Red until the file exists.
- Done when: seed test green with zero warnings from the reader.
- Commit: `Config: seed project standards and framework exceptions for this repo`

## U3. ProjectHelper skeleton and registration (red tests first)

- Goal: the new helper exists and is wired everywhere the repo checks.
- Create: `src/Helpers/ProjectHelper/src/AiDevHelpers.Helpers.ProjectHelper.csproj` (copy `PackageHelper`'s: Common reference,
  `Nerdbank.GitVersioning` `Update`, embedded `Resources/Strings.resx`, `SelfTest.md`), `Strings.cs`, `SelfTest.md`;
  `src/Helpers/ProjectHelper/tests/AiDevHelpers.Helpers.ProjectHelper.Tests.csproj`.
- Edit: `AiDevHelpers.slnx` (folder `/Helpers/ProjectHelper/`), `src/MCP/Core/src/AiDevHelpers.MCP.Core.csproj`
  (ProjectReference), `SelfTestTools.HelperNames`, `SelfTestCoverageTests.ToolToHelper` (`ProjectTools` to `ProjectHelper`),
  `docs/self_test.md`, `DocsMatchCodeTests` pack list source (tag build), `docs/features.md`.
- Tests first: run `SelfTestCoverageTests` and `DocsMatchCodeTests` with the new entries added; they fail until the project,
  solution entry, self-test and docs exist.
- Done when: both test classes green; the solution builds.
- Commit: `ProjectHelper: add the helper project and register it`

## U4. Command seam and solution reading

- Goal: run `dotnet` without a third interface, and read the solution's layout.
- Create: `src/Helpers/ProjectHelper/src/CommandSeam.cs` (a delegate `RunCommand(CommandRequest, CancellationToken)` returning
  Common `CommandResult`; `Default` calls `CommandRunner.RunAsync`), `SolutionLayout.cs` (project paths and solution folders via
  `SolutionProjects.Read` plus an XML read of the `.slnx` folders; infers the dominant pattern: the folder shape, the test
  folder name, the solution-folder naming).
- Edit: `src/Common/src/SolutionProjects.cs` only if folder names are needed (add `ReadFolders`, with tests in
  `src/Common/tests`).
- Tests first: layout inference on temp-dir `.slnx` fixtures: this repo's `src/Helpers/X/{src,tests}` shape, a flat
  `src/X` shape, and a solution with no pattern (no default; the caller must supply folder and solution folder).
- Done when: inference returns a default only when two or more projects agree.
- Commit: `ProjectHelper: infer project layout from the solution`

## U5. Package versions across projects

- Goal: a pinned-version source from the repo's existing csproj files.
- Edit: `src/Helpers/PackageHelper/src` add `PackageVersions.cs` (`ReadAll(root)` returns package to versions to projects;
  `Choose(package, frameworks)` majority, ties to `PackageUpdateRule.NewestAllowed`); tests in `PackageHelper/tests`.
- Tests first: single version; majority wins; tie picks newest allowed for the frameworks; `Update=` and conditional
  `ItemGroup`s read; floating or missing versions reported, not guessed.
- Done when: green; ProjectHelper gets a reference to PackageHelper (add to its csproj).
- Commit: `PackageHelper: read package versions across projects and choose one`

## U6. Project types

- Goal: `project_types_list`.
- Create: `src/Helpers/ProjectHelper/src/TemplateList.cs` (parse `dotnet new list --type project` output; filters: language,
  tag), `ProjectTools.cs` in `src/MCP/Core/src` (`ProjectTypesList`), fixtures `tests/Fixtures/dotnet-new-list.txt`.
- Edit: `Strings.resx`, `mcp-tools.md`, helper `SelfTest.md`, `docs/dogfood.md`.
- Tests first: parser on saved output (several columns, wrapped tags, no templates, header missing gives a clear error);
  filter by language and tag; the runner failing returns `ok:false`.
- Phase-0 check inside this unit: confirm the `dotnet new list` format against Microsoft Learn before writing the parser.
- Done when: green; a live call lists templates.
- Commit: `ProjectHelper: add project_types_list`

## U7. Framework guard and project_audit

- Goal: the guard (pure) and the read-only audit.
- Create: `src/Helpers/ProjectHelper/src/FrameworkGuard.cs` (`Check(project, frameworks, standards)` allowed, excepted,
  refused with the config path and the fields to add), `ProjectAudit.cs` (per project or whole solution: framework vs
  standard, `LangVersion`, `Nullable`, analyzers, differing package versions from U5, test SDK present on test projects; each
  finding says whether an exception covers it); `ProjectAudit` tool in `ProjectTools.cs`.
- Tests first: guard matrix (equal, base + allowed suffix, different with exception, different without, multi-target where one
  framework is excepted, exception for another project does not apply, no standards file fails closed); audit on temp-dir
  fixtures; audit of this repo returns no errors (uses U2).
- Done when: green; guard never writes a file (assert the config file bytes unchanged).
- Commit: `ProjectHelper: framework guard and project_audit`

## U7b. project_fix plan and confirm

- Goal: correct an existing project to the standards, from the audit's findings.
- Create: `src/Helpers/ProjectHelper/src/ProjectFixer.cs` (`Plan`, `ConfirmAsync`, same token map as U8), tools `ProjectFixPlan`
  and `ProjectFixConfirm`.
- Fixes (each listed in the plan as an item the caller can drop with `skip`): set `LangVersion`, `Nullable`, `ImplicitUsings`,
  `AnalysisLevel`, `TreatWarningsAsErrors` to the standards; add missing analyzer packages at the U5 version; align a differing
  package version to the chosen one; add the test SDK to a test project that lacks it; retarget the framework to the standard.
- Framework rule: a retarget is planned only when no exception covers the project. A project with an exception keeps its
  framework and the plan says why. The fixer never writes an exception and never changes the standards file.
- Confirm recomputes the audit and the project file hash (refuses on drift), edits through `XDocument` preserving newline style,
  runs `dotnet build -warnaserror` on the project and its dependents, and restores the original bytes on failure.
- Tests first: each fix on a temp-dir fixture; excepted project not retargeted; skip list honored; drift refusal; rollback leaves
  the tree byte-identical; a second fix after a clean fix plans nothing (idempotent); standards file bytes unchanged.
- Done when: green; one live fix on a scratch repo; audit clean afterwards.
- Commit: `ProjectHelper: project_fix_plan and project_fix_confirm`

## U8. project_add plan and confirm

- Goal: add a project of any type, anywhere in the solution.
- Create: `src/Helpers/ProjectHelper/src/ProjectAdder.cs` (`Plan`, `ConfirmAsync`; pending token map with 15-minute expiry),
  `SlnxWriter.cs` (insert `<Project Path>` into the named `<Folder>`, creating it when missing, preserving the file's newline
  style and indentation), tools `ProjectAddPlan` and `ProjectAddConfirm`.
- Inputs: template, name, folder, solution folder (both optional only when U4 inferred a default; otherwise required),
  language, framework (default from standards), `packages`.
- Plan: resolves framework, runs the guard, lists properties and required packages with versions from U5, lists the files it will
  create and the stubs it will delete. Writes nothing.
- Confirm: recomputes the guard, the standards and the state (folder still absent or empty, solution file unchanged since the
  plan, hash compared); refuses on drift. Then `dotnet new`, delete template stubs (`Class1.cs`, `UnitTest1.cs` and similar, only
  the files the template produced), write the `.slnx` entry, apply properties through `XDocument`, add packages via
  `dotnet add package` at the chosen versions, then `dotnet build -warnaserror` on the project. On a failed build: delete the new
  folder, remove the solution entry, restore the solution bytes, return `ok:false` with diagnostics.
- Tests first: plan output per template; guard refusal; token single-use and expiry; drift refusal (solution edited, folder
  created); `.slnx` round trip (read then write unchanged gives identical bytes; add into existing and new folder; CRLF file stays
  CRLF); rollback on build failure leaves the tree byte-identical; stubs removed; default-less solution requires explicit folder.
- Done when: green; one live add and rollback in a scratch repo through the tools.
- Commit: `ProjectHelper: project_add_plan and project_add_confirm`

## U9. project_add_test plan and confirm

- Goal: add a test project beside any source project.
- Create: `src/Helpers/ProjectHelper/src/TestProjectAdder.cs` (reuses U8 machinery), tools `ProjectAddTestPlan` and
  `ProjectAddTestConfirm`.
- Behavior: name = `<source name>.Tests`; folder and solution folder follow the source project's pattern (U4) or the caller's
  arguments; the repo's test framework and runner packages come from the repo's existing test projects through U5; a
  `ProjectReference` to the source; standards applied as in U8; `seed_test: true` adds one failing test. The test SDK package is
  included, so `AffectedTests.IsTestProject` finds it with no other wiring.
- Tests first: name derivation; reference path; test SDK present; `AffectedTests` discovers the generated project (temp
  solution fixture); `seed_test` file content; source in a flat layout; refusal when the `.Tests` project already exists.
- Done when: green; `test_affected` lists the new project in the scratch repo.
- Commit: `ProjectHelper: project_add_test_plan and project_add_test_confirm`

## U10. PR1 docs and gate

- Edit: `src/Common/src/Resources/mcp-tools.md`, `docs/dogfood.md` (one tool-use row per new tool), `CHANGELOG.md`,
  `docs/features.md`, helper `SelfTest.md` (every tool name).
- Run: full gate (`.claude/reference/full-gate.md`); self-test list shows ProjectHelper.
- Done when: gate green; open the PR per `pr-promotion`.
- Commit: `Docs: project scaffolding tools, changelog and dogfood`

# PR2 (branch from `wip` after PR1 lands)

## U11. Phase 0 verification (no code)

Findings (verified 2026-11-25 on this machine, SDK 11.0.100, VS 18.12 Community; Learn pages cited):

- `dotnet workload list` prints a table: `Installed Workload Id`, `Manifest Version`, `Installation Source`. Parse the table, or use `--machine-readable` (JSON body only, no boundary lines; Learn "dotnet workload output change"). Use JSON.
- `dotnet workload search [text]` prints `Workload ID` and `Description` columns. Learn: `dotnet workload search`.
- `dotnet workload install <id>` installs a workload; Learn "dotnet workload install" applies. Manifests download first unless `--skip-manifest-update`.
- `vswhere -products * -format json` lists instances with `installationPath`, `installationVersion`, `productId`, `isComplete`, `displayName`.
- VS install and workload add: `vs_installer modify --installPath <path> --add <workload-id> --passive --norestart` (Learn "Command-line parameter examples": `setup.exe modify --installPath ... --config`). Elevation is required; standard users may not use `--passive` or `--quiet` (Learn "Modify Visual Studio workloads").
- VS running during modify: not documented in Learn. Plan keeps the refusal: probe for `devenv.exe` and refuse while it runs.
- `kind` argument fixed: `dotnet` and `vs`.


- Check on Microsoft Learn: `dotnet workload list` and `search` output; `vswhere -products * -requires`; `vs_installer.exe
  modify --add <id>` flags and exit codes; whether the installer refuses or fails while VS runs. Record the answers in
  `docs/dogfood.md`. If the VS-running behavior cannot be confirmed, the plan refuses by probing for `devenv.exe` processes.
- Done when: findings recorded; the `kind` argument (`dotnet` or `vs`) fixed.
- Commit: `Docs: record workload tool verification`

## U12. Parsers and read tools

- Create: `src/Helpers/ProjectHelper/src/Workloads/` (`DotnetWorkloadParser`, `VswhereParser`, `WorkloadCheck`), fixtures, tools
  `WorkloadList` and `WorkloadCheck`. The project-type to workload map is a small table in the helper, tested.
- Tests first: parsers on saved output (none installed, several, vswhere with two instances, malformed); `workload_check`
  reports missing for a project type; runner failure gives `ok:false`.
- Done when: green.
- Commit: `ProjectHelper: workload_list and workload_check`

## U13. Workload install plan and confirm, elevated

- Goal: install a workload through the `IElevInstaller` pattern.
- Create: `src/Common/src/InstallableWorkloads.cs` (the only workloads installable: kind, id, display; shared by MCP and script),
  `src/ScriptHelper/src/InstallWorkloadFiles.cs` (script text generated from the table; takes a name, not an id),
  `IWorkloadInstaller` in `ScriptHelperTools.cs` with `ProcessElevRunner.InstallWorkloadAsync` (`elev.exe install-workload`),
  an `install-workload` verb in `src/ScriptHelper/client/Program.cs` that refuses names not in the table, and
  `WorkloadInstallService` in `src/MCP/Core/src` modeled on `ToolInstallService` (token, expiry, decline). Tools
  `WorkloadInstallPlan` and `WorkloadInstallConfirm`.
- Pin: `scripts/workload-install.sha256` holds the SHA-256 of `InstallWorkloadFiles.Script()` (LF-normalized, as
  `RepoSettingsFile.Hash`); `scripts/workload-install.ps1 -WritePin` regenerates it, modeled on `scripts/gate-tpm.ps1`. The
  installed script checks its own hash against the pin embedded at generation time before it does anything.
- Edit: `.claude/reference/full-gate.md` (add a pin-verify line: the pin test plus `git diff --exit-code` on the pin file), and
  `CLAUDE.md` elevated-scripts section naming the new pin.
- Plan refuses when a VS instance is running (process probe seam, `ITerminalProbe`-style, fake in tests), when the workload is
  not in the table, or already installed. Confirm recomputes those checks, then installs.
- Tests first: plan/confirm token (single use, expiry, unknown); refusal when VS runs; refusal for a name outside the table;
  already installed; elev failure and "refused:" output mapped as in `ToolInstallService`; pin test: current script hash equals
  the committed pin; tamper test: a changed script text or a changed pin makes the verifier refuse; the script contains no
  caller-supplied package id.
- Done when: green; one live install of a harmless workload in an elevated session, accepted at the UAC prompt.
- Commit: `ProjectHelper: workload_install_plan and workload_install_confirm with pinned elevated script`

## U14. PR2 docs and gate

- Edit: `mcp-tools.md`, `docs/dogfood.md`, `CHANGELOG.md`, helper `SelfTest.md`, `docs/prereqs.md` (VS Installer, elevation).
- Run: full gate including the new pin check; open the PR per `pr-promotion`.
- Commit: `Docs: workload tools, changelog and dogfood`

## Follow-ups (open)

- Live workload install (U13 "done when"): needs `setup tools` run elevated with the chat yes, the UAC prompt, then `workload_install_confirm`.
- Approval status of the elevated scripts: `elev verify` needs the broker running, which needs the elevated `setup tools` run.
- `scripts/workload-install.ps1 -WritePin`: not written. The pin matches the table and is checked by `InstallableWorkloadsTests`; add the script only when the table changes.
- VS-running refusal for `vs_installer modify`: not needed while `vs` installs are not offered.
- `ServerControlTests.ResumeClearsThePause`: failed once in 14 runs, not reproduced in 3. Suspect a live MCP server rewriting the shared state file.
- Chat-first prompts in `repo-setup`, terminals and rules: built, not run end to end. The client has no tests.
- Pre-existing, not from this work: 56 `-warnaserror` errors (CS1591 in `src/Bridge`) and a VSSDK crash in `Vs2026/Installer`.

## After merge (outside this repo)

- Update the `tdd` skill with one line pointing at `project_add_test_plan`, through `instruction-placement`.
