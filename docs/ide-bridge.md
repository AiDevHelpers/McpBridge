# IDE bridge (Visual Studio and VS Code)

The MCP server drives open IDE windows through a common set of `Ide*` tools. The same call works in
Visual Studio and VS Code. The reply's `ide` field says which IDE handled it.

## How it works

- Each IDE window runs a bridge on a named pipe, one pipe per window process:
  - Visual Studio: `InstanceRegistry.PipeNameFor(pid)` in the `AiDevHelpers.Bridge` package (source: AiDevHelpers/McpBridge). The VS extension hosts it.
  - VS Code: `\\.\pipe\AiDevHelpers.VSCode.<pid>` (`src/Installers/VsCode/Installer/AiDevTools.VsCode/src/ideBridge.ts`).
- Each window writes a manifest to `%ProgramData%\AiDevHelpers\vs-instances\<pid>.json`:
  `{ pid, pipeName, solutionPath, updatedUtc, ide }`. `ide` is `VisualStudio` or `VsCode`.
- The MCP server reads the manifests (`VsInstanceRegistry`, in the package) and routes each call (`IdeInstanceRouter`, in the package) to the window whose
  `solutionPath` matches. With one instance open, the solution is inferred.
- Routing arguments on every `ide_` tool: `ide` (`VisualStudio` or `VsCode`) and `instancePid` (the process id).
  When the same solution is open in two Visual Studio instances (for example Community and Insiders), a call
  with only `solutionPath` fails with "Several instances have this solution open". Pass `instancePid` to pick one,
  or store `preferredIde` for the solution (`ide_set_preferred`), which breaks that tie only.
  Full rules are in [protocol.md](../conformance/protocol.md#routing-mcp-side).
- Replies that don't arrive within 60 seconds are errors. An instance whose pipe refuses the client (for
  example, one running at a different elevation) is reported as "refused this client".
- Protocol: one JSON line per request, `{"action":"...","args":{...}}`. The reply is one JSON line,
  `{"ok":true,"pid":N,"result":...}` or `{"ok":false,"pid":N,"error":"..."}`. The VS Code bridge adds
  `"unsupported":true` for actions it can't do. The VS bridge never sends that field.
- Watchdog (Visual Studio): `BridgeHost` checks every 10 seconds and restarts its pipe and manifest if either is missing.
  It also pings the UI thread with a no-op. An answer within 5 seconds keeps `ui_responsive` true. No answer for 30 seconds
  sets it false and logs a warning. A ping that never returns stays in flight, so a hung UI does not queue more pings.
- Watchdog (VS Code): the VS Code bridge checks every 10 seconds and restarts its pipe and manifest if either is missing.
- `health` is answered by each bridge directly. It reports the listener state, request count, pipe name, and `ui_responsive`
  (Visual Studio only). A hung UI thread shows as `ui_responsive: false`. If the whole process is frozen, nothing answers.
  `ide_reset_instance` uses that: it refuses a responsive instance, and kills an unresponsive one only with `confirm` true.

## Tools

| Tool | Action | Notes |
|---|---|---|
| `IdeListInstances` | (MCP only) | Lists manifests and their solutions. |
| `IdeBridgeHealth` | `health` | Listener state and request counters. |
| `IdeStartDebugging` / `IdeStopDebugging` | `startDebugging` / `stopDebugging` | VS Code uses the selected or first launch configuration. In Visual Studio, `IdeStartDebugging` is refused, naming the reason, when the one matching instance is not elevated and its solution needs administrator rights (see `IdeLaunchInstance`); an ambiguous target is left to the router. |
| `IdeSetConfiguration` / `IdeListConfigurations` | `setConfiguration` / `listConfigurations` | VS Code keeps the selected name for `startDebugging`. |
| `IdeSetStartupProject` | `setStartupProject` | VS only. VS Code returns `unsupported`. |
| `IdeRunTests` | `runTests` | VS: `dotnet test` with TRX when `IdeTestRunMode` is `DotnetTest`, otherwise Test Explorer. VS Code: `testing.runAll`. |
| `IdeGetTestResults` | `searchOutput` (TestExplorer mode) | In TestExplorer mode (default) reads the Tests output pane: counts, failing test names and `failure_details` with each message. In DotnetTest mode reads the newest TRX file in the solution's `TestResults` folder. |
| `IdeGetTestSummary` | (MCP only) | Counts from the newest finished Test Explorer run, read from the Visual Studio Tests output pane. Works in TestExplorer mode, where no TRX is written. `failures` lists the tests the runner marked `[FAIL]` in that run (between the previous summary line and this one); it is empty when the pane has no such lines. |
| `IdeBuild`, `IdeClean`, `IdePublish` | `build`, `clean`, `publish` | VS: the IDE build. VS Code: `build` runs the build task; `clean` and `publish` run `dotnet` in the workspace folder. |
| `IdeGetBuildDiagnostics` | `getBuildDiagnostics` | Errors and warnings from the Error List (VS) or diagnostics (VS Code). |
| `IdeCodeCleanup` / `IdeFormatDocument` | `codeCleanup` / `formatDocument` | |
| `IdeFormatSolution` | `formatSolution` | Formats every C# file of the solution with the IDE's Format Document command and saves each one. Opens each file. |
| `IdeGoToDefinition` | `goToDefinition` | Places the caret on file:line:column and runs `Edit.GoToDefinition`. Moves the IDE's active document. Returns the IDE state, not the target location. |
| `IdeFindAllReferences` | `findAllReferences` | Places the caret and runs `Edit.FindAllReferences`. Results open in the IDE window; this tool does not read them. |
| `IdeGetOutput`, `IdeSearchOutput`, `IdeClearOutput` | `getOutput`, `searchOutput`, `clearOutput` | VS: the named Output pane. VS Code: only the lines this bridge recorded (see Limits). |
| `IdeSetBreakpoint`, `IdeRemoveBreakpoint`, `IdeListBreakpoints` | `setBreakpoint`, `removeBreakpoint`, `listBreakpoints` | |
| `IdeStep`, `IdeGetDebugState`, `IdeEvaluate`, `IdeGetLocals` | `step`, `debugState`, `evaluate`, `getLocals` | Locals and evaluate need a paused debugger. |
| `IdeAddWatch`, `IdeRemoveWatch`, `IdeListWatches` | `addWatch`, `removeWatch`, `listWatches` | Watches are kept by the bridge. Values show only while paused. |
| `IdeKillProcess` | `killProcess` | Kills only processes this bridge started. VS starts none, so it refuses every pid. VS Code kills its clean/publish processes. |
| `IdeLaunchInstance` / `IdeKillInstance` | (MCP lifecycle) | Launch or save-and-kill a VS instance. Kill refuses if any document is unsaved. Launch is elevated (UAC prompt, `runas`) only when the solution has a project listed in the repo's `.aidevtools/elevation.json`, or this user's mode for the repo is `always` (`user.json`); a user mode of `never` always launches normally. A running non-elevated instance of such a solution is not reused: the result says `needs_elevated_relaunch`, and only with `relaunchElevated: true` (after the user agrees) is it saved, closed and started elevated. An elevated instance cannot be closed from the non-elevated server; kill says so. |
| `TerminalSession` | `open`, `tab`, `exec`, `list`, `close` | Runs commands in visible Windows Terminal sessions or headless service-account sessions. See [terminal-session-contract.md](terminal-session-contract.md). |
| `ide_open_document` | `openDocument` | Opens a file and moves the caret to an optional line. |
| `ide_open_solution` | (MCP only) | Launches Visual Studio's devenv with the `.sln`. Does not use the bridge. Elevated under the same rule as `IdeLaunchInstance`. |
| `bookmark_list`, `bookmark_get`, `bookmark_set`, `bookmark_remove` | `bookmarkList`, `bookmarkGet`, `bookmarkSet`, `bookmarkRemove` | Visual Studio bookmarks. VS Code returns not-supported. See Limits. |
| `output_channel_set`, `output_write` | `outputWrite` | Writes text to a named output channel: an Output window pane in Visual Studio, an output channel in VS Code. The IDE for each channel is stored per repo. Reading back is not offered. |
| `ide_set_preferred`, `ide_get_preferred` | (MCP only) | Stores or reads `preferredIde` in `.claude/aidevhelpers.json` next to the solution. See Settings. |
| `repo_setup` | (MCP only) | Read only. `action: scan` finds projects and launch scripts in the repo that need administrator rights and says which are not yet listed or declined; `action: status` returns the elevated list, this user's mode (`listed`, `always`, `never`) and what is pending. `repoPath` is any folder inside the repo. It changes nothing and never creates `.aidevtools`: the user decides with `elev repo-setup <repo>` in a console. A repo opened with no `.aidevtools` folder is scanned once per server session and a notification names the projects found. |
| `script_run` | (MCP only) | Runs a script from a repo (`.ps1`, `.cmd`, `.bat`, `.sh`, `.py`, `.js`) with the interpreter that fits it: PowerShell 7 or 5.1 from `#Requires`, cmd, Git Bash, Python or Node (override, `#!` name, `#Requires`, then the extension; paths come from PATH and the usual install folders, never from the repo). Output is captured, arguments are passed as data, the run stops after the timeout or after a stretch with no output, and the result names the interpreter and why. A script that keeps failing (default 3 runs in 10 minutes) or hangs gets `advice` that offers a visible terminal. With `interactive: true` the script runs in a visible non-elevated tab; with `interactive: true` and `elevated: true` an approved PowerShell script from the repo's `.elevated.json` runs elevated in a visible window through the Script Helper broker (same allowlist, fingerprint and approval checks) and the transcript comes back. A missing interpreter returns an install hint; nothing is installed. Limits come from the server's `ScriptWatch` section and a per-user `scriptWatch` block in `.aidevtools/user.json`, never from a committed file. `run_elevated_script` and `terminal_session` `exec` feed the same tracker. |
| `tool_install` | (MCP only) | Installs a missing tool with winget through the elevated script helper. `tool` is one of `pwsh`, `gitbash`, `python`, `node` (a fixed table; `wsl` gets instructions only, it needs a restart). `action: plan` returns the exact command and a one-time token (15 minutes) and installs nothing; the AI must ask the user, and the user's yes in chat is the permission, after which `action: confirm` with the token runs it. `action: decline` records a no so the offer (shown by `script_run` when an interpreter is missing) is not repeated. The install runs `install-tool.ps1` through the broker: the user runs `elev tools` once (elevated, one UAC prompt), which writes the script and its allowlist into the admin-only install folder, registers it and asks to approve it. The script takes a tool name, not a package id, and looks the id up in its own table. |
| `run_elevated_script` | (MCP only) | Runs an approved script elevated through the Script Helper broker (`elev.exe`, next to the server). Needs `projectRoot`, `script` and optional `args`. Returns the exit code and output, or a refusal. The script must be in the project's `.elevated.json` and approved by a person. |

### Bridge actions used by other tools

These actions are called by the MCP server's own services, not by a single tool of the same name.

| Action | Used by | Notes |
|---|---|---|
| `status` | routing and health checks | Returns `solution_path`. |
| `listOutputPanes` | the Visual Studio output log source | Lists the Output window pane names. |
| `listDebugSessions`, `getDebugOutput` | the VS Code debug log source | VS Code only. Lists debug sessions and reads a session's output. |
| `prepareKill` | `IdeKillInstance` | Asks the instance to save every document, and reports any that stay unsaved. |

### Helper tools (MCP only, no bridge action)

| Tools | Helper | Notes |
|---|---|---|
| `live_test_start`, `live_test_stop` | TestingHelper | Live testing with `dotnet watch test`. Works in Visual Studio Community, which has no Live Unit Testing. Failures reach `test_failures`, the error log and MCP log notifications. |
| `lint_check` | LintHelper | Checks a folder's lint setup: `.editorconfig` against csproj `NoWarn`, tsconfig strict mode, an ESLint flat config for TypeScript projects, deprecated TSLint, duplicate names in `Strings.resx` files, and optionally `dotnet format --verify-no-changes`. |
| `string_scan` | LintHelper | Lists multi-word string literals in a C# file or folder that should be in `Resources/Strings.resx`, with the resource key when the text is already there. Skips comments, attributes, logger calls, `Strings.cs`, and `bin` or `obj` folders. |
| `resx_check` | LintHelper | Checks each `Resources/Strings.resx` under a folder: duplicate names, keys used in code but missing, and keys never used. |
| `git_status`, `git_diff`, `git_plan`, `git_confirm` | SourceControlHelper | Runs `git.exe`. Status and diff run at once. Every write (stage, commit, pull, push, branch, switch, stash) is two-step: `git_plan` returns the exact command and a single-use token, and `git_confirm` runs it. When the repo has `.aidevtools/repo-rules.json` (recorded by `elev repo-setup`) or a cached live GitHub policy, `git_plan` refuses a commit or push on a protected branch, deleting one, and a new branch name that breaks the pattern, and `git_confirm` checks again. A refusal gives a request id; the user can allow that one write with `elev rules override <repo> <id>` in a console. Rules only restrict. |
| `pr_checks`, `ci_failure` | SourceControlHelper | Read only, through the GitHub CLI (`gh`). `pr_checks` lists each CI check of a pull request with its state and bucket, and `missingRequired` (required checks from the repo rules that the pull request lacks). `ci_failure` returns the first failing job and the error lines of its log (the end of the log when no line looks like an error). |
| `package_check` | PackageHelper | NuGet (`nuget.config`, `Directory.Packages.props`, xunit analyzers, .NET analyzers), pip (pinned requirements) and npm (a lockfile). Reads files only. |
| `package_restore` | PackageHelper | Restores the NuGet packages of a solution (`dotnet restore`) and returns ok, the exit code and the tail of the output. Edits no project file. Run it before a build or `package_outdated` when packages may be missing. |
| `package_outdated` | PackageHelper | Lists the outdated NuGet packages of a solution (`dotnet list package --outdated`) with the version to move to. A package that versions with .NET (`System.*`, `Microsoft.Extensions.*` and the like) is never moved past the .NET major of its project, so a net10.0 project does not get an 11.x package. Edits nothing. |
| `gate_plan`, `gate_run` | GateHelper | The full gate. Steps come from the `gate` block in `.claude/reference/full-gate.md`, or from the detected stack. A run stops at the first failure. `gate_plan` also returns `missingRequiredCommands` (gate commands in `repo-rules.json` that no step runs). |
| `server_control_set`, `server_control_status` | (server) | Pause, close and resume every aidevtools server on this machine for an update. `paused` makes each server refuse tool calls with a message and pushes a log notification (`AiDevHelpers.Control`) to its chat. `closing` makes each server exit after a short pause, which releases its files. `running` resumes. These two tools work in every state. State lives in `%ProgramData%\AiDevHelpers\control\state.json`. `scripts/update-aidevtools.ps1` runs pause, close, update, resume and always resumes at the end. |
| `self_test_list`, `self_test_read` | each helper | Lists helpers with their versions and the server version, and reads a helper's embedded self-test markdown. |
| `config_resolve`, `config_read` | ConfigHelper | Real paths for remote debugger, launchSettings, appsettings, user and solution settings. Reads are limited to allowed roots and redact secrets. |
| `error_ingest`, `error_list`, `error_snapshot`, `error_changes` | ErrorHelper | Caller supplies build output. Filters by code and source. Suppression comes from the solution's `.vsconfig` key `aidevhelpersErrors`. |
| `test_watch`, `test_unwatch`, `test_failures`, `test_coverage` | TestingHelper | Watches TestResults while hot reload is active. Coverage uses Coverlet, or the `dotnet-coverage` CLI for Visual Studio. |
| `test_affected` | TestingHelper | Lists the test projects a change can affect, with a `dotnet test` command for each. A changed file affects its project and every project that references it; a build-wide file (`Directory.Build.props`, `.editorconfig`, a solution file) affects all test projects. Uses git's changed files when none are given. Runs nothing. |
| `extension_list`, `extension_info`, `extension_add_plan`, `extension_add_confirm` | ExtensionsHelper | VS Code list and info are local. Visual Studio info queries the Marketplace. Adds are two-step and need approval. |
| `extension_workspace_setup` | ExtensionsHelper | Recommends C# Dev Kit and C# in `.vscode/extensions.json`, and sets `dotnet.defaultSolution` when C# Dev Kit is installed. |
| `build_env` | ExtensionsHelper | Lists the Visual Studio installs (stable and prerelease) with their MSBuild path and whether the VSSDK targets a VSIX build needs are present. |
| `test_hosts_list`, `test_hosts_stop` | MCP Core (TestHostTools) | Lists running `testhost` processes and returns a token. `test_hosts_stop` kills only the listed pids, once, if each still has the start time the list recorded. Call the stop only after the user agrees. |
| `ide_reset_instance` | `IdeResetTools` | Resets a Visual Studio instance whose UI thread stopped answering (health `ui_responsive` false, or no answer). Without `confirm` it changes nothing; with `confirm` true it kills the devenv process, which closes it without saving. |

## Limits

- **VS Code output:** VS Code's API can't read other output channels. `getOutput`, `searchOutput`, and
  `clearOutput` cover only the lines this bridge recorded from its own clean and publish runs.
- **VS Code watches:** no API lists them. The bridge evaluates its own list in the active session.
- **VS Code startup project:** no equivalent. Use the launch configuration (`setConfiguration`).
- **VS Test Explorer:** its results aren't exposed to extensions. Use `dotnet test` with TRX mode.
- **VS killProcess:** refused for every pid. Use `IdeKillInstance` to close a VS instance.
- **VS bookmarks:** only bookmarks in open documents are visible. Names are not stored by VS, so `name` comes back null.
- **VS Code bookmarks:** not supported in v1 (`unsupported: true`).
- **VS coverage through the bridge:** not built. Visual Studio coverage uses the `dotnet-coverage` CLI.
- **Hot reload:** `hotReloadState` (VS Code) and `testHotReloadState` (Visual Studio) read the settings. TestingHelper uses them when the caller passes them, and otherwise falls back to `dotnet watch`.
- **Continuous testing:** VS Code only, through Test Explorer's run commands. Visual Studio has no continuous testing setting, so it uses `live_test_start`.
- **Output channels:** `outputWrite` creates a channel on first use and appends. VS Code can't read a channel back, and Visual Studio reads panes through `IdeGetOutput` only for the panes it lists.
- **Source control:** Visual Studio has no public Git operation API, so `git_*` runs `git.exe` for both IDEs. VS Code has a git API (its built-in git extension), used by the `git*` bridge actions in the VS Code extension. The MCP `git_*` tools do not route to it yet, so they still run `git.exe`.
- **Gate:** `gate_run` runs the repo's build and test commands. It does not read `package.json` in subfolders, so list those steps in `.claude/reference/full-gate.md`. The lite gate is not defined.
- **Live checks not run:** `outputWrite` in both IDEs, the `git_*` tools in an IDE, and `ide_set_preferred` routing with two live instances.
- **Unverified on a real install:** the `.vsconfig` key `aidevhelpersErrors`, and the
  Marketplace query shape used by ExtensionsHelper.

## Settings

- **Layers.** Global (the MCP `appsettings.json`), then the repo's committed `.aidevtools/settings.json` (repo root is the nearest folder with a
  `.git` folder or file above the solution), then the per-user `.aidevtools/user.json` (gitignored; Script Helper's pointer to this user's
  signed allow-all approval). The repo overrides global per key, but only for keys a repo may set (today `IdeTools.IdeTestRunMode`); other keys,
  and a malformed file, fall back to global with a warning. `allowAll` is read only from the repo file, never from the global settings.

- `IdeTestRunMode` in the MCP `appsettings.json` (`IdeTools` section): `DotnetTest` or `TestExplorer`
  (default). The VS options page's "IDE test runs use dotnet test with TRX" checkbox writes it. `DotnetTest` passes
  `--logger trx` to VSTest projects and `--report-xunit-trx` when a project or props file references Microsoft.Testing.Platform
  or xunit.v3. This repo's `Directory.Build.props` points `dotnet test` at `.runsettings`, which excludes `Category=Tpm`.
- The VS options page also edits the MCP advanced settings: verbose logging, context helper and token
  economy switches, context size limit, endpoint strategy, and endpoints. Endpoints are one per line as
  `name|url|primary|models`, and at least one must have `primary` set to `true` when the list isn't empty.
- `aidevhelpersErrors` in the solution's `.vsconfig`: `{ "suppressCodes": [...], "suppressSources": [...] }`.
  ErrorHelper hides those codes and sources. The solution wins over user settings. Visual Studio may reject
  the extra key, which is not yet verified.
- `csharp.experimental.debug.hotReload` (VS Code) and `test.hotReload` (Visual Studio): hot reload state, read by
  `hotReloadState` and `testHotReloadState`. Both are set by the IDE, not by this project.
- `.claude/aidevhelpers.json` next to the solution: `preferredIde` (`VisualStudio` or `VsCode`) breaks a routing tie
  when two IDE instances have the same solution open and the call has no `ide` or `instancePid`. `outputChannels`
  maps a channel name to the IDE that `output_write` uses. Other keys in the file are kept.
- `.claude/reference/full-gate.md`: the repo's own gate. Its `gate` code block lists one command per line; a line
  starting with `!` may fail without stopping the gate.
- `dotnet.defaultSolution` (C# Dev Kit): set on install by `extension_workspace_setup` when C# Dev Kit is installed.
  Verified: with two solutions in a folder and this key set, C# Dev Kit restores and loads the chosen one.
- Extension recommendations go to `.vscode/extensions.json` in the folder: `ms-dotnettools.csdevkit` and
  `ms-dotnettools.csharp`.

## Build and install

Visual Studio (from the repo root):

```bash
pwsh -File scripts/update-vs-extension.ps1
```

This rebuilds the VSIX and opens VsixInstaller for each VS 2026 instance. Expect a UAC prompt and a
dialog per instance. Commit first: the version is 0.1.<commit count>, which the build script writes into the
manifest for that build only. The VS Installer dialog installs one VSIX into both Community and Insiders
when both are ticked. The command-line form with more than one instance ID fails with exit code 2005.

VS Code bridge (the Core library is vendored into the extension at build time, so the package is self-contained):

```bash
cd src/Installers/VsCode/Installer/AiDevTools.VsCode
npm install
npm run build
npm test
npm run package
code --install-extension aidevhelpers-vscode.vsix --force
```

`npm run build` runs `prebuild` (copies Core into `src/vendor`) and `postbuild` (copies it into `dist`). Restart
VS Code after installing. The extension activates on startup.

CI builds both packages on pull requests and `v*` tags. See the README for the required checks.

## Live checks

1. Stop the installed `TokenSaverMCP` service and start the dev server:
   `pwsh -File scripts/run-dev-server.ps1` (needs UAC).
2. VS Code: open a folder with a `.sln` or `.csproj`. The manifest appears in `vs-instances` once the
   extension activates. Send `{"action":"health"}` to its pipe and check `listener_running`.
3. Send `clean`, `searchOutput`, `killProcess` (with an untracked pid, which must be refused), and
   `addWatch` plus `listWatches`.
4. Visual Studio: install the VSIX, reload, and check the manifest, `health`, `runTests`, and
   `killProcess` (refused).
