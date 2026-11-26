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
| `ide_edit_method` (`IdeEditMethod`) | `editMethod` | VS only. Replaces the method (or constructor, destructor, operator or conversion operator, or a local function when the line is inside one) at `file:line` with `newMethodText` through the Roslyn workspace. Checks first: the new text must be one method; the old and new method symbols are compared (name, type parameters and constraints, parameter types, `ref`/`out`/`in`/`params`, defaults, parameter names, return type, accessibility, `static`/`virtual`/`abstract`/`override`/`sealed`); a signature change needs a `callerEdits` entry (`file`, `line`, `column`, `newText`) for every reference that `ide_semantic_references` returns, and is refused on a method that overrides, implements or is overridden; any new compile error in the edited or referencing documents refuses the edit. The method's file is formatted with Roslyn (the project's editorconfig), and diagnostics from the compiler and the project's analyzers that are new since the edit come back in the reply as `new_errors` (refuse), `new_warnings` and `new_notices` (reported only). A `///` doc comment on the method is updated to match: `param`, `typeparam` and `returns` tags follow the new signature (a renamed parameter keeps its text, a removed one loses its tag, `paramref` names follow a rename) in the comment's own layout (prefix, indent, single or multi-line tags, tag order). The tool never invents text: each new parameter or type parameter needs an entry in `docs` (`tag`, `name`, `text`), or the edit is refused. `newSummary` replaces the summary when the method's purpose changed; without it a changed signature only warns that the summary may be stale. A method with no doc comment, or a `/** */` one, is left alone. Without `confirm` it returns the checks and changes nothing. Roslyn cannot see reflection, strings, XAML or projects outside the solution. |
| `ide_add_method` (`IdeAddMethod`) | `addMethod` | VS only. Adds one method (or constructor, destructor, operator or conversion operator) to the type that contains `line`, at the end of the type or after the member at `afterLine`. If neighbouring methods have `///` docs the new method gets a doc comment in their layout; `newSummary` and `docs` (`tag`, `name`, `text`) supply the text, which the tool never invents (missing text is a warning and no doc comment is written). The file is formatted, then compile errors new since the edit refuse it. Without `confirm` it returns the checks. |
| `ide_add_type` (`IdeAddType`) | `addType` | VS only. Adds a new type (class, struct, record, interface, enum or delegate) given as `declaration` in its own file beside `anchorFile`, in the anchor's project and namespace unless `namespace` is given. `usings`, `summary` and `attributes` are written around the declaration in the file's layout. Refused when a type of that name exists or a file is already at the path. `withTests: true` also writes `<Name>Tests.cs` into the test project that references the anchor's project, with a test that asserts failure until written; the framework (xUnit, NUnit or MSTest) comes from the test project's references. Without `confirm` it returns the checks. |
| `ide_add_interface_member` (`IdeAddInterfaceMember`) | `addInterfaceMember` | VS only. Adds `declaration` (one method, property, event or indexer, as full C# text) to the interface that contains `line`. Every type implementing the interface would lack the member, so the reply lists each implementer and the change is refused while there are any. Duplicate names are refused. Without `confirm` it returns the checks. |
| `ide_add_enum_member` (`IdeAddEnumMember`) | `addEnumMember` | VS only. Adds `name` (with optional constant `value`) after the last member of the enum that contains `line`, in the enum's own comma layout. Without `confirm` it returns the checks. |
| `ide_edit_enum_member` (`IdeEditEnumMember`) | `editEnumMember` | VS only. Changes the value of the enum member at `line` to `value`. Refused for a `[Flags]` enum and for an enum that looks persisted (it has attributes, or is public), unless `allowValueChange` is true. Without `confirm` it returns the checks. |
| `ide_thread_parameter` (`IdeThreadParameter`) | `threadParameter` | VS only. Adds `declaration` (for example `CancellationToken ct = default`) as the last parameter of the method at `file:line`, and threads it up its callers to `depth` levels (default 1). A call passes the caller's own parameter of the same type when it has one; a caller within `depth` gets the parameter too; any call above that passes `valueAtTop`. Every changed method is listed in the reply as a warning. Refused for `params` methods, for members of an override, implementation or interface family, for a call that cannot be rewritten, and for a negative depth. Doc comments are not updated (warned). Without `confirm` it returns the checks. |
| `ide_change_signature` (`IdeChangeSignature`) | `changeSignature` | VS only. Changes the parameter list of the method at `file:line` to `parameters` (in the new order; each `{declaration, fromOld, valueForCallers}`: `fromOld` is the old index a parameter keeps, omitted for a new one, and `valueForCallers` is the argument existing calls get for a new parameter). Every call site is rewritten through Roslyn: arguments move with their parameters, named arguments are matched by name, a removed parameter's argument is dropped only when it has no side effects. Covered call forms: method calls (an extension call keeps its receiver), `new T(...)`, `base(...)` and `this(...)` initializers, and attributes; an omitted trailing argument for a defaulted parameter stays omitted. Refused for a method group, a delegate, an attribute's named property, a new parameter after a kept `params` parameter, a receiver moved off first place, an omitted default before a passed argument, when a new parameter has no value and no default, `returnType` and `accessibility` are optional and set the return type and access (other modifiers are kept); callers that stop compiling are refused by the checks. `docs` follows as in `ide_edit_method`. The base methods the method overrides or implements, their overrides and implementations, and the interface members involved change together in the same plan: their parameter lists and call sites are rewritten, and their return types, accessibility, modifiers and doc comments are left as they are (the plan warns about the doc comments). A family member outside the solution refuses the change. Without `confirm` it returns the checks and the uncovered call sites. |
| `ide_add_overload` (`IdeAddOverload`) | `addOverload` | VS only. Adds an overload of the method at `line` after its last overload, with a doc comment built from the original's (same rules as `ide_add_method`). Refused if any existing call would bind to the new overload instead of the original, if its signature matches an existing method, or on a new compile error. The file is formatted. Without `confirm` it returns the checks. |
| `ide_tdd_add_test` (`IdeTddAddTest`) | `tddAddTest` | VS only. TDD step 1. Adds the method under test (given without a body it becomes a stub that throws `NotImplementedException`) and a test for it in a different file, formatted and checked like `ide_add_method`. Without `confirm` it returns the checks. Once applied the MCP server runs the new test with coverage and `tdd.status` says `red` (failed, as wanted), `green` (it passed before the method existed, so it may not test anything) or `not_run`. |
| `ide_tdd_update_test` (`IdeTddUpdateTest`) | `tddUpdateTest` | VS only. TDD step 2. Replaces the body of a test (a block or its statements; the attribute and signature stay), then runs that test and returns its result. `targetFile` and `targetLine` name the method under test to also get its line coverage. Without `confirm` it returns the checks. |
| `ide_add_member` (`IdeAddMember`) | `addMember` | VS only. Adds fields and properties to the type that contains `line`. An indexer, event, event field or delegate is added from its full C# text in `declaration` (exactly one member; its summary and attributes are used as for a property). Each entry of `members` gives `name`, `type`, `accessibility` (always required; `public`, `internal`, `protected`, `private`, `protected internal`, `private protected`) and `backing`: `auto` (`{ get; set; }`), `field` (a private `_name` field and a property over it), `readonly-field`, `field-only` (a plain field) or `expression` (a get-only property computed from `initializer`). Also `setter` (`set`, `init`, `none`, or `private set` style), `required`, `initializer`, `attributes` (`Required` or `JsonProperty("x")`) and `summary`. They go after the type's last field or property, in the type's layout, with doc comments in the neighbours' style; missing summary text is a warning. The file is formatted and a new compile error refuses the change. |
| `ide_edit_member` (`IdeEditMember`) | `editMember` | VS only. For an indexer, event, event field or delegate, `declaration` is its full new text and must be the same kind of member; its body or accessors may change freely, but a change to its kind, access, type or parameters is refused while any reference uses it. Other changes: Changes an existing field or property of any accessibility; only what is given changes: `newType`, `accessibility`, `setter`, `required`, `backing` (`auto` or `field`, swapping an auto property and a property over one private field), `addAttributes`, `removeAttributes`, `newSummary`, or `newName`. References are always checked. A change to the type, accessibility, setter or `required` is refused unless `callerEdits` (`file`, `line`, `column`, `newText`) covers every reference `ide_semantic_references` returns, and is refused on a property that overrides or implements another member. A rename goes through Roslyn, which updates every reference, and must be a call of its own. Attribute, backing and summary changes touch no reference. A new compile error refuses the change; the file is formatted. |
| `ide_rename_namespace` (`IdeRenameNamespace`) | `renameNamespace` | VS only. Renames a namespace and every namespace under it across the solution: each declaration (block or file-scoped), using, qualified name (also after `global::`), alias and cref that spells it out. Refused for an invalid or unchanged name; when no source declares the namespace; when it is also declared in a referenced assembly; when a type in it would collide with one already in the new namespace; when a declaration is a block nested inside another namespace block; and when code names it relative to an enclosing namespace (each such reference needs a `callerEdits` entry with `file`, `line`, `column`, `newText`). Strings that still mention the old name are listed in `string_mentions` and left alone. A new compile error refuses the change. If the project's folders follow its namespaces, files that no longer match are warned about; `moveFiles: true` moves them on disk after the text changes are applied (SDK-style projects only). |
| `ide_move_type` (`IdeMoveType`) | `moveType` | VS only. Moves one top-level type to another namespace: its declaration (every part of a partial type), each qualified use, a using of the new namespace in code that used the type by its simple name, the usings the moved type used to get for free from its old namespace and its parents, and removal of an old-namespace using nothing needs any more. Refused when the type is nested or in the global namespace, when its file also declares other types, when the target already has a type of that name, and for relative qualified uses without a `callerEdits` entry. `moveFiles` works as for `ide_rename_namespace`. |
| `ide_delete_member` (`IdeDeleteMember`) | `deleteMember` | VS only. Deletes a method, property, field, event, constructor, operator, indexer, enum member or interface member with its doc comment. Refused while any reference remains (each needs a `callerEdits` entry; an empty `newText` removes the call only where the code stays valid), while it overrides or implements something, while an interface member has implementers, and while tests call it unless their names are in `deleteTests` (those tests are not deleted). A new compile error refuses the change. Without `confirm` it returns the checks. |
| `ide_delete_type` (`IdeDeleteType`) | `deleteType` | VS only. Deletes a top-level or nested class, struct, interface, record or enum. Refused while any reference outside the type remains (each needs a `callerEdits` entry), and while tests call it unless their names are in `deleteTests`. `deleteFile: true` also deletes the file when the type was its only declaration (SDK-style projects only). Without `confirm` it returns the checks. |
| `ide_list_unused` (`IdeListUnused`) | `listUnused` | VS only. Read only. Lists private and internal members and types with no references (public ones with `includePublic`), with kind, name, file, line and confidence. Members with attributes, interfaces or constructors are marked low confidence: reflection, serialization or DI may use them. Feeds `ide_delete_member` and `ide_delete_type`. |
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
| `dotnet_diag` | (MCP only) | Wraps the .NET diagnostic global tools (dotnet-dump, dotnet-stack, dotnet-counters, dotnet-gcdump, dotnet-trace, dotnet-symbol). `action`: `status` (each tool's path, version, or install command), `processes`, `stack`, `dump`, `gcdump`, `counters`, `trace`, `symbols`. Takes `pid`, `seconds` (1 to 300) or an absolute `path`; never a command line. Output files go to `%TEMP%\aidev-diag` unless `path` is given. Installs nothing; see `docs/prereqs.md`. |
| `run_elevated_script` | (MCP only) | Runs an approved script elevated through the Script Helper broker (`elev.exe`, next to the server). Needs `projectRoot`, `script` and optional `args`. Returns the exit code and output, or a refusal. The script must be in the project's `.elevated.json` and approved by a person. |

**Members with a method.** `ide_edit_method`, `ide_add_method`, `ide_add_overload` and `ide_tdd_add_test` take an optional `members` list (the same entries as `ide_add_member`). The members are added to the method's type first and the method is planned on top, so one plan, one apply and one undo cover both. Diagnostics are compared against the solution before either change, so the members' own warnings are reported too. The reply lists them as `members_added`.

**Tests after a method change.** When `confirm` is true and `ide_edit_method`, `ide_add_method` or `ide_add_overload` applies the change (and `runTests` is not false), the MCP server runs the tests for it and adds `test_run` to the reply. The tests are the test methods (xUnit, NUnit or MSTest attributes) that call the method, found before the edit with Roslyn; when none do, every test in the test projects the changed file affects runs, and `test_run.warnings` says so. It runs `dotnet test` with a trx logger and a name filter, with coverage from Coverlet when the test project references it and otherwise from `dotnet-coverage` (no coverage, with a warning, when neither is there). `test_run` lists each test's name, outcome, duration and failure message, the pass, fail and skip counts, and `coverage.method`: lines counted, lines covered, the rate, and the uncovered line numbers of the method itself. Projects on Microsoft.Testing.Platform have no name filter here, so the whole project runs. The three TDD steps use the same runner: `tdd.status` is `red` or `green` for the test just added or updated.

### Bridge actions used by other tools

These actions are called by the MCP server's own services, not by a single tool of the same name.

| Action | Used by | Notes |
|---|---|---|
| `status` | routing and health checks | Returns `solution_path`. |
| `listOutputPanes` | the Visual Studio output log source | Visual Studio only. Lists the Output window pane names. |
| `listDebugSessions`, `getDebugOutput` | the VS Code debug log source | VS Code only. Lists debug sessions and reads a session's output. |
| `hotReloadState`, `continuousTestingStart`, `continuousTestingStop`, `continuousTestingState` | the VS Code settings and testing commands | VS Code only. Reads hot reload and runs or stops continuous testing. VS Code has no API to read continuous mode, so `continuousTestingState` reports the bridge's own record. |
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

## Tools added in phases 1 to 6

| Tool | Purpose |
| --- | --- |
| `ide_add_project_item` | Add a file to a project: mode fromFile adds an existing file; mode fromNew creates one from a template path. Without confirm it only returns the plan. Show the plan to the user and get their yes before setting confirm to true. |
| `ide_add_task` | Add a task to the Visual Studio Task List under the bridge's own provider. Category is Error, Warning, or Message. Without confirm it only returns the plan. Show the plan to the user and get their yes before setting confirm to true. |
| `ide_add_watch` | Add a watch expression to an IDE instance's watch list. Watches are kept by the extension, not the Watch window. |
| `ide_attach_process` | Attach the debugger to one local process by pid. Without confirm it only returns the target. Show the plan to the user and get their yes before setting confirm to true. |
| `ide_bridge_health` | Check the bridge health of an IDE instance: whether its listener is running, when it started, when it last handled a request, how many requests it has handled, and its solution. Answered even while the IDE is busy. |
| `ide_build` | Build the solution in an IDE instance and report whether it succeeded. Pass solutionPath when several instances are open. Use IdeGetBuildDiagnostics for the errors. |
| `ide_clean` | Clean the solution in an IDE instance using the active configuration. |
| `ide_clear_output` | Clear an output pane in an IDE instance. |
| `ide_clear_tasks` | Remove every task this bridge added to the Task List. Tasks from other tools are not touched. Without confirm it only reports the count. Show the plan to the user and get their yes before setting confirm to true. |
| `ide_close_document` | Close an open document. A document with unsaved changes is refused unless discardChanges is also true. Without confirm it only returns the plan. Show the plan to the user and get their yes before setting confirm to true. |
| `ide_close_solution` | Close the solution. Refused while documents have unsaved changes unless saveFirst is true. Without confirm it lists the dirty documents and the plan. Show the plan to the user and get their yes before setting confirm to true. |
| `ide_code_cleanup` | Run Visual Studio's Code Cleanup on the active document of an IDE instance. |
| `ide_detach_all` | Detach the debugger from every attached process. Without confirm it only lists what would be detached. Show the plan to the user and get their yes before setting confirm to true. |
| `ide_disable_breakpoint` | Disable the breakpoint(s) at a file and line. Without confirm it only returns the change. Show the plan to the user and get their yes before setting confirm to true. |
| `ide_enable_breakpoint` | Enable the breakpoint(s) at a file and line. Without confirm it only returns the change. Show the plan to the user and get their yes before setting confirm to true. |
| `ide_evaluate` | Evaluate an expression in the current stack frame of an IDE instance. The debugger must be stopped at a breakpoint. |
| `ide_find_all_references` | Find all references to the symbol at file:line:column in an IDE instance. Places the caret, then runs the IDE command. The results open in the IDE's Find All References window, which this tool does not read. |
| `ide_find_in_files` | Search the text of files: the solution, one project, or the open documents. Managed search over files on disk; read-only. Capped at 500 matches. |
| `ide_format_document` | Format the active document in an IDE instance. Pass solutionPath when several instances are open. |
| `ide_format_solution` | Format every C# file of the solution with the IDE's Format Document command, saving each one. Opens each file. |
| `ide_get_build_diagnostics` | Read build errors and warnings from an IDE instance's Error List (the last build's results). |
| `ide_get_debug_state` | Get the debugger mode (design, run, or break) and the current function in an IDE instance. |
| `ide_get_locals` | Read local variables in the current stack frame of an IDE instance. The debugger must be stopped at a breakpoint. |
| `ide_get_output` | Read the last lines of an IDE output pane, e.g. 'Build' or 'Debug'. For a filtered, deduplicated summary, use LogQuery with a vsout: channel. |
| `ide_get_setting` | Read one Visual Studio setting by its key path. Returns found:false when the setting does not exist. Read-only. |
| `ide_get_test_results` | Get the outcome of the newest test run for the solution. In 'TestExplorer' mode (default) it reads the IDE's Tests output pane: counts, failing test names and failure_details with each failure's message. In 'DotnetTest' mode it reads the run's TRX file, which also covers TRX files written by other test runs in the solution's TestResults folder. |
| `ide_get_test_summary` | Get the counts of the newest finished Test Explorer run, read from the Visual Studio Tests output pane. Works in TestExplorer mode, where no TRX is written. failures lists the tests the runner marked [FAIL] in that run; it is empty when the pane has no such lines. |
| `ide_go_to_definition` | Go to the definition of the symbol at file:line:column in an IDE instance. Places the caret, then runs the IDE command. Moves the IDE's active document and caret. Returns the IDE state, not the target location. Visual Studio only. |
| `ide_kill_instance` | Save all documents and then kill a Visual Studio instance. Refuses, killing nothing, if any document is still unsaved. |
| `ide_kill_process` | Kill a process that an IDE instance started for clean or publish. Refuses any other process id. |
| `ide_launch_instance` | Launch a Visual Studio instance with a solution open. Reuses an instance that already has it open. Waits for the instance's bridge to come up. Launches elevated (UAC prompt) only for a solution with a project the repo lists as needing administrator rights, or when this user's mode for the repo is always; a running non-elevated instance of such a solution is not reused unless relaunchElevated is true. |
| `ide_list_breakpoints` | List breakpoints set in an IDE instance. |
| `ide_list_code_elements` | List the code elements (namespaces, types, members) of one file with line ranges. Read-only. |
| `ide_list_configurations` | List the solution build configurations (e.g. Debug, Release) of an IDE instance and the active one. |
| `ide_list_debugged_processes` | List the processes the IDE's debugger is currently attached to. Read-only. |
| `ide_list_documents` | List the open documents with saved state and read-only flag. Read-only. |
| `ide_list_exceptions` | List the debugger's exception settings, grouped as the debugger groups them, with break-when-thrown and break-when-user-unhandled. Read-only. |
| `ide_list_instances` | List running IDE instances (currently Visual Studio) with their process id and the solution each has open. |
| `ide_list_processes` | List the processes the IDE's debugger can attach to (local processes), with pid and name. Read-only. |
| `ide_list_stack_frames` | List the stack frames of the current thread (index, function, return type). Empty unless the debugger is in break mode. Read-only. |
| `ide_list_tasks` | List the items in the Visual Studio Task List window (category, text, file, line). Read-only. |
| `ide_list_threads` | List the threads of the debugged process with their ids, and the current thread. Empty unless the debugger is in break mode. Read-only. |
| `ide_list_watches` | List an IDE instance's watch expressions with their current values (when stopped at a breakpoint). |
| `ide_project_properties` | Read named properties of a project (for example TargetFramework, OutputType). Unknown names return null. Read-only. |
| `ide_publish` | Publish the startup project of an IDE instance with the IDE's publish command, using the active configuration. |
| `ide_read_events` | Read events from a Visual Studio instance, after a sequence number (0 for everything kept). Events are kept for the last 500 per instance from the time this server started. Events: solutionOpened, solutionClosing, buildBegin, buildDone, enterBreakMode, enterRunMode, contextChanged, documentSaved, documentOpened, documentClosing. Read-only. |
| `ide_remove_breakpoint` | Remove breakpoints at a file and line in an IDE instance. |
| `ide_remove_project_item` | Remove a file from a project. The file stays on disk. Without confirm it only returns the plan. Show the plan to the user and get their yes before setting confirm to true. |
| `ide_remove_watch` | Remove a watch expression from an IDE instance's watch list. |
| `ide_rename_symbol` | Rename the symbol at a file position everywhere in the solution, using Roslyn. Without confirm it only lists the files that would change. Show the plan to the user and get their yes before setting confirm to true. |
| `ide_replace_in_files` | Replace text in files (solution, project, or open documents). Without confirm it only counts matches per file and writes nothing. Open documents with unsaved changes are skipped. Show the plan to the user and get their yes before setting confirm to true. |
| `ide_run_tests` | Run all tests in the solution. The server setting IdeTestRunMode picks the method: 'TestExplorer' (default) triggers Test Explorer in the IDE; 'DotnetTest' runs dotnet test with a TRX file. Pass solutionPath when several instances are open. Use IdeGetTestResults for the outcome. Pass project (a .csproj path, relative to the solution or absolute) to run only that project; it always uses dotnet test, so it can run while a whole-solution Test Explorer run is going. |
| `ide_save_document` | Save an open document. Without confirm it only returns the plan. Show the plan to the user and get their yes before setting confirm to true. |
| `ide_search_output` | Search the lines of an IDE output pane for a regex pattern. Returns the matching lines. |
| `ide_semantic_definition` | Find where the symbol at a file position is declared in source, using Roslyn. Sees unsaved editor edits. Read-only. |
| `ide_set_breakpoint` | Set a breakpoint at a file and line in an IDE instance. |
| `ide_set_breakpoint_condition` | Set the condition, hit-count type, or hit count of the breakpoint(s) at a file and line. Re-adds the breakpoint, so hit counts reset. Without confirm it only returns the change. Show the plan to the user and get their yes before setting confirm to true. |
| `ide_set_configuration` | Set the active build configuration (e.g. Debug or Release) of an IDE instance's solution. |
| `ide_set_exception_break` | Change the break-when-thrown and/or break-when-user-unhandled setting of one debugger exception. Without confirm it only returns the change. Show the plan to the user and get their yes before setting confirm to true. |
| `ide_set_startup_project` | Set the startup project of the solution in an IDE instance. Pass solutionPath to choose the instance when several are open. |
| `ide_set_status` | Set the status bar text of the IDE (empty clears it). Without confirm it only returns the plan. Show the plan to the user and get their yes before setting confirm to true. |
| `ide_show_info_bar` | Show an info bar in the IDE's main window with a message and optional button labels. Buttons do not report clicks. Without confirm it only returns the plan. Show the plan to the user and get their yes before setting confirm to true. |
| `ide_start_debugging` | Start debugging the solution in an IDE instance. Pass solutionPath when several instances are open. |
| `ide_step` | Step the debugger in an IDE instance: 'over', 'into', or 'out'. The debugger must be stopped at a breakpoint. |
| `ide_stop_debugging` | Stop debugging in an IDE instance. Pass solutionPath when several instances are open. |
| `ide_subscribe_events` | Make sure this server holds an events subscription to a Visual Studio instance. Subscriptions are also made automatically for every running instance, so this is only needed if one was closed. Returns the instance pid. Read events with ide_read_events. |
| `ide_undo_group` | Apply several range edits in one IDE undo group, so they undo as one step. Each edit gives start and end line and column (1-based) and new text. Without confirm it lists the edits. Show the plan to the user and get their yes before setting confirm to true. |
| `ide_unsubscribe_events` | Stop this server's events subscription to one instance. Automatic subscription resumes on the next check unless the instance is closed. |
| `ide_write_activity_log` | Write an entry to the Visual Studio activity log (ActivityLog.xml). Level is Error, Warning, or Information. Without confirm it only returns the plan. Show the plan to the user and get their yes before setting confirm to true. |

Bridge actions behind these tools are listed in conformance/protocol.md. Hit-count types for `ide_set_breakpoint_condition`: `always`, `equal`, `multiple`, `greaterOrEqual`.
