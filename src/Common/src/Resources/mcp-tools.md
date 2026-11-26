# AiDevHelpers.McpBridge MCP Tools

The stdio MCP server exposes the tools below. Read the live `tools/list` schemas before calling a tool; this guide explains intent, while the schemas define current parameters. IDE tools target Visual Studio instances and accept `solutionPath` where applicable. When multiple instances are open, pass `solutionPath` to select the intended instance.

## IDE (Visual Studio and VS Code)

| Tool | Purpose |
| --- | --- |
| `ide_add_member` | Add one or more fields or properties to the type that contains a line, using Roslyn, with checks. For each member you give its name, type, accessibility (always required: public, internal, protected, private, protected internal or private protected) and how it is backed: auto ({ get; set; }), field (a private backing field and a property over it), readonly-field, field-only (a plain field) or expression (a get-only property computed from initializer). Also setter (set, init, none, or "private set" style), required, initializer, attributes (for example "Required" or "JsonProperty(\"x\")") and summary. They go after the type's last field or property, in its layout, with doc comments in the neighbours' style when they have them. The file is formatted, a new compile error refuses the change, and new warnings and notices are returned. Without confirm it only returns the checks. Show them to the user and get their yes before setting confirm to true. Once applied it runs the tests that call the type's members, if any. Visual Studio only. |
| `ide_delete_member` | Delete one C# member (method, property, field, event, constructor, operator, indexer, enum member or interface member) using Roslyn, with checks. Refused while any reference remains: give a callerEdits entry for each (file, line, column from ide_semantic_references; newText replaces the call, use an empty value only where that leaves valid code). Refused when the member overrides or implements another member, when an interface member has implementers, and when tests call it unless their names are given in deleteTests (those tests are not deleted). Removes the member and its doc comment. A new compile error refuses the change. Without confirm it only returns the checks. Show them to the user and get their yes before setting confirm to true. Visual Studio only. |
| `ide_delete_type` | Delete one C# type (class, struct, interface, record or enum, top-level or nested) using Roslyn, with checks. Refused while any reference outside the type remains: give a callerEdits entry for each (file, line, column from ide_semantic_references; newText replaces the reference). Refused while tests call it unless their names are in deleteTests (those tests are not deleted). With deleteFile true, the file is deleted too when the type was its only declaration (SDK-style projects only). A new compile error refuses the change. Without confirm it only returns the checks. Show them to the user and get their yes before setting confirm to true. Visual Studio only. |
| `ide_list_unused` | List C# members and types with no references, for review before deleting them: private and internal ones by default, public ones when includePublic is true (other assemblies may use public ones). Read only. Each entry gives its kind, name, file and line, and a confidence: members with attributes, interfaces or constructors may be used by reflection, serialization or dependency injection, and are marked low. Feeds ide_delete_member and ide_delete_type. Visual Studio only. |
| `ide_add_method` | Add one C# method to the type that contains a line, using Roslyn, with checks. The new method goes at the end of the type, or after the member at afterLine. If neighbouring methods have /// doc comments, the new one gets a doc comment in the same layout from newSummary and docs (a {tag, name, text} entry for each parameter, type parameter and a non-void return); the tool never invents text and warns when it is missing. The file is formatted, a new compile error refuses the edit, and new warnings and notices are returned. Without confirm it only returns the checks. Show them to the user and get their yes before setting confirm to true. Once applied it runs the tests that call the method (or the affected test projects when none do) with coverage and returns each test's status and the method's line coverage in test_run; pass runTests false to skip. Visual Studio only. |
| `ide_add_overload` | Add an overload of the method at a line, using Roslyn, with checks. The overload has the same name, goes after the method's last overload, and gets a doc comment like the original's (newSummary and docs supply the text; the tool never invents it). It is refused if it changes which method any existing call binds to, if the file then has a new compile error, or if its signature matches an existing method. The file is formatted and new warnings and notices are returned. Without confirm it only returns the checks. Show them to the user and get their yes before setting confirm to true. Once applied it runs the tests that call the method (or the affected test projects when none do) with coverage and returns each test's status and the method's line coverage in test_run; pass runTests false to skip. Visual Studio only. |
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
| `ide_edit_method` | Replace one C# method using Roslyn, with checks. Refused if it adds compile errors, or if the signature changes and any reference has no entry in callerEdits. A signature change on a method that overrides, implements or is overridden is refused. If the method has a /// doc comment, its param, typeparam and returns tags are updated to the new signature in the comment's own layout; a new parameter needs its text in docs (the tool never invents text), and newSummary replaces the summary when the purpose changed. Roslyn cannot see reflection, strings, XAML or projects outside the solution, so also search the name with ide_find_in_files. Without confirm it only returns the checks. Show them to the user and get their yes before setting confirm to true. Once applied it runs the tests that call the method (or the affected test projects when none do) with coverage and returns each test's status and the method's line coverage in test_run; pass runTests false to skip. Visual Studio only. |
| `ide_edit_member` | Change an existing field or property, whatever its accessibility, using Roslyn, with checks. Set only what changes: newType, accessibility, setter (set, init, none, "private set" style), required, backing (auto or field: swap between an auto property and a property over a private field), addAttributes, removeAttributes, newSummary, or newName. Every reference is checked: a change to the type, accessibility, setter or required needs a callerEdits entry (file, line, column from ide_semantic_references, and newText for the invocation or name) for each reference, or it is refused; a rename updates its references itself and must be a call of its own; attribute, backing and summary changes touch no reference. A change on a property that overrides or implements another member is refused. A new compile error refuses the change. The file is formatted and new warnings and notices are returned. Without confirm it only returns the checks. Show them to the user and get their yes before setting confirm to true. Once applied it runs the tests that call the member, with coverage. Visual Studio only. |
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
| `ide_get_preferred` | Read the preferred IDE stored for a solution, or none. |
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
| `ide_list_instances` | List running IDE instances (currently Visual Studio) with their process id, solution, git branch and worktree. Pass solutionPath to add a `check`: whether the solution is open and whether an instance is open on the expected branch and worktree. |
| `ide_list_processes` | List the processes the IDE's debugger can attach to (local processes), with pid and name. Read-only. |
| `ide_list_stack_frames` | List the stack frames of the current thread (index, function, return type). Empty unless the debugger is in break mode. Read-only. |
| `ide_list_tasks` | List the items in the Visual Studio Task List window (category, text, file, line). Read-only. |
| `ide_list_threads` | List the threads of the debugged process with their ids, and the current thread. Empty unless the debugger is in break mode. Read-only. |
| `ide_list_watches` | List an IDE instance's watch expressions with their current values (when stopped at a breakpoint). |
| `ide_move_type` | Move one top-level type to another namespace, using Roslyn, with checks. The type's declaration moves, every qualified use is rewritten, and code that used the type by its simple name gets a using for the new namespace; the moved type gets the usings it used to have implicitly, and usings of the old namespace that nothing needs any more are removed. It is refused if the new name is not valid, the type is nested or in the global namespace, its file also declares other types (or a partial part's file does), a type of that name already exists in the new namespace, or code names the type relative to an enclosing namespace (those need callerEdits entries). A new compile error refuses the change. The same moveFiles rule as ide_rename_namespace applies. Without confirm it only returns the checks. Show them to the user and get their yes before setting confirm to true. Once applied it runs the affected test projects. Visual Studio only. |
| `ide_open_document` | Open a file in an IDE instance and move the caret to an optional 1-based line. Pass solutionPath when several instances are open. |
| `ide_open_solution` | Open a solution in Visual Studio by launching devenv with the .sln path. Works even when the IDE bridge is not answering. |
| `ide_project_properties` | Read named properties of a project (for example TargetFramework, OutputType). Unknown names return null. Read-only. |
| `ide_publish` | Publish the startup project of an IDE instance with the IDE's publish command, using the active configuration. |
| `ide_read_events` | Read events from a Visual Studio instance, after a sequence number (0 for everything kept). Events are kept for the last 500 per instance from the time this server started. Events: solutionOpened, solutionClosing, buildBegin, buildDone, enterBreakMode, enterRunMode, contextChanged, documentSaved, documentOpened, documentClosing. Read-only. |
| `ide_remove_breakpoint` | Remove breakpoints at a file and line in an IDE instance. |
| `ide_remove_project_item` | Remove a file from a project. The file stays on disk. Without confirm it only returns the plan. Show the plan to the user and get their yes before setting confirm to true. |
| `ide_remove_watch` | Remove a watch expression from an IDE instance's watch list. |
| `ide_rename_symbol` | Rename the symbol at a file position everywhere in the solution, using Roslyn. Without confirm it only lists the files that would change. Show the plan to the user and get their yes before setting confirm to true. |
| `ide_rename_namespace` | Rename a namespace across the solution, using Roslyn, with checks. The namespace and every namespace under it change: each declaration, using, qualified name, alias and cref that spells it out. It is refused if the name is not a valid dotted name, if no source file declares the namespace, if it is also declared in a referenced assembly, if a type in it would collide with a type already in the new namespace, if a declaration is a block nested inside another namespace block, or if code names it relative to an enclosing namespace (those references need callerEdits entries with file, line, column and newText). Strings that still mention the old name are listed in string_mentions and not changed. A new compile error refuses the change; new warnings and notices are returned. When the project's folders follow its namespaces, files that no longer match are warned about, or moved when moveFiles is true (SDK-style projects only; ask the user first). Without confirm it only returns the checks. Show them to the user and get their yes before setting confirm to true. Once applied it runs the affected test projects. Visual Studio only. |
| `ide_replace_in_files` | Replace text in files (solution, project, or open documents). Without confirm it only counts matches per file and writes nothing. Open documents with unsaved changes are skipped. Show the plan to the user and get their yes before setting confirm to true. |
| `ide_reset_instance` | Reset a Visual Studio instance whose UI thread has stopped answering (health ui_responsive false, or no answer at all). Without confirm it changes nothing and reports what a reset does: ask the user first, then call again with confirm true. A reset closes the instance without saving, so unsaved edits can be lost. Relaunch afterwards with ide_open_solution. |
| `ide_run_tests` | Run all tests in the solution. The server setting IdeTestRunMode picks the method: 'TestExplorer' (default) triggers Test Explorer in the IDE; 'DotnetTest' runs dotnet test with a TRX file. Pass solutionPath when several instances are open. Use IdeGetTestResults for the outcome. Pass project (a .csproj path, relative to the solution or absolute) to run only that project; it always uses dotnet test, so it can run while a whole-solution Test Explorer run is going. |
| `ide_save_document` | Save an open document. Without confirm it only returns the plan. Show the plan to the user and get their yes before setting confirm to true. |
| `ide_search_output` | Search the lines of an IDE output pane for a regex pattern. Returns the matching lines. |
| `ide_semantic_definition` | Find where the symbol at a file position is declared in source, using Roslyn. Sees unsaved editor edits. Read-only. |
| `ide_semantic_references` | Find every reference to the symbol at a file position, across the solution, using Roslyn. Sees unsaved editor edits. Read-only. |
| `ide_set_breakpoint` | Set a breakpoint at a file and line in an IDE instance. |
| `ide_set_breakpoint_condition` | Set the condition, hit-count type, or hit count of the breakpoint(s) at a file and line. Re-adds the breakpoint, so hit counts reset. Without confirm it only returns the change. Show the plan to the user and get their yes before setting confirm to true. |
| `ide_set_configuration` | Set the active build configuration (e.g. Debug or Release) of an IDE instance's solution. |
| `ide_set_exception_break` | Change the break-when-thrown and/or break-when-user-unhandled setting of one debugger exception. Without confirm it only returns the change. Show the plan to the user and get their yes before setting confirm to true. |
| `ide_set_preferred` | Store the preferred IDE for a solution in .claude/aidevhelpers.json next to the .sln. It is used only when two IDE instances have the same solution open. An explicit ide or instancePid argument still wins. |
| `ide_set_startup_project` | Set the startup project of the solution in an IDE instance. Pass solutionPath to choose the instance when several are open. |
| `ide_set_status` | Set the status bar text of the IDE (empty clears it). Without confirm it only returns the plan. Show the plan to the user and get their yes before setting confirm to true. |
| `ide_show_info_bar` | Show an info bar in the IDE's main window with a message and optional button labels. Buttons do not report clicks. Without confirm it only returns the plan. Show the plan to the user and get their yes before setting confirm to true. |
| `ide_start_debugging` | Start debugging the solution in an IDE instance. Pass solutionPath when several instances are open. |
| `ide_step` | Step the debugger in an IDE instance: 'over', 'into', or 'out'. The debugger must be stopped at a breakpoint. |
| `ide_stop_debugging` | Stop debugging in an IDE instance. Pass solutionPath when several instances are open. |
| `ide_subscribe_events` | Make sure this server holds an events subscription to a Visual Studio instance. Subscriptions are also made automatically for every running instance, so this is only needed if one was closed. Returns the instance pid. Read events with ide_read_events. |
| `ide_tdd_add_test` | TDD step 1: add a test and the method it tests, then run the test to show it red. The method goes into the type at methodFile:methodLine; give it without a body (ending in ;) and it is added as a stub that throws NotImplementedException, so the test compiles and fails. The test goes into the class at testFile:testLine (testMethodText includes its attribute). The two must be in different files. Both are formatted and checked like ide_add_method. Without confirm it only returns the checks. Show them to the user and get their yes before setting confirm to true. Once applied the new test runs with coverage and tdd.status says red (failed, as wanted), green (it passed before the method existed, so it may not test anything) or not_run. Visual Studio only. |
| `ide_tdd_update_test` | TDD step 2: replace the body of a test, then run it and return its result. newBody is a block or the statements of the new body; the test's attribute and signature stay. Without confirm it only returns the checks. Show them to the user and get their yes before setting confirm to true. Once applied the test runs, and tdd.status says red or green. Give targetFile and targetLine (a line inside the method under test) to also get that method's line coverage. Then implement the method with ide_edit_method, which runs the tests that call it. Visual Studio only. |
| `ide_undo_group` | Apply several range edits in one IDE undo group, so they undo as one step. Each edit gives start and end line and column (1-based) and new text. Without confirm it lists the edits. Show the plan to the user and get their yes before setting confirm to true. |
| `ide_unsubscribe_events` | Stop this server's events subscription to one instance. Automatic subscription resumes on the next check unless the instance is closed. |
| `ide_write_activity_log` | Write an entry to the Visual Studio activity log (ActivityLog.xml). Level is Error, Warning, or Information. Without confirm it only returns the plan. Show the plan to the user and get their yes before setting confirm to true. |

## Git

| Tool | Purpose |
| --- | --- |
| `git_confirm` | Run a write planned by git_plan, once. The token is single use. |
| `git_diff` | Diff of the working tree (or the staged changes when staged is true), optionally for one path. |
| `git_plan` | Plan a git write and return its exact command and a token. Nothing runs until git_confirm. action: stage, commit, pull, push, create_branch, delete_branch, switch, stash_push, stash_pop, stash_apply. sequence: several of those actions under one token, e.g. [stage, commit, push]; they run in order at git_confirm and the first failure stops the rest. Give action or sequence, not both. paths, message and branch go to each step that needs them. |
| `git_status` | Branch, upstream, ahead and behind counts, and changed paths for a git repo folder. |

## Gate

| Tool | Purpose |
| --- | --- |
| `gate_plan` | Show the full gate steps for a repo without running them: the commands in .claude/reference/full-gate.md, or the steps detected from the stack. |
| `gate_run` | Run the full gate for a repo, step by step, and stop at the first failure. Runs build and test commands in the repo. |

## Lint, format and resources

| Tool | Purpose |
| --- | --- |
| `lint_check` | Check a folder's lint setup for mismatches: .editorconfig vs csproj NoWarn, tsconfig strict, ESLint flat config for TypeScript, deprecated TSLint, duplicate resource names. Set runDotnetFormat to also run 'dotnet format --verify-no-changes' on the root solution. |
| `resx_check` | Check each Resources/Strings.resx under a folder against the Strings.Get and Strings.Format calls beside it: duplicate names, keys used in code but missing from the resx, and keys never used. |
| `string_scan` | List multi-word string literals in C# source that should be in Resources/Strings.resx. Takes a .cs file or a folder. Comments, attributes, logger calls, Strings.cs and bin or obj folders are skipped. resxKey names the resource when the text is already in one. |

## Tests

| Tool | Purpose |
| --- | --- |
| `test_affected` | List the test projects a change can affect, and the dotnet test command for each. A changed file affects its project and every project that references it. With no changedFiles, the files git reports as changed (working tree and untracked) are used. Nothing is run. |
| `test_coverage` | Run code coverage for a solution. provider is Coverlet or VisualStudio. Default: Coverlet when the repo references it, otherwise VisualStudio (dotnet-coverage CLI). |
| `test_failures` | Return test failures queued since the last call, then clear the queue. |
| `test_hosts_list` | List test host processes (testhost.exe) that are still running, such as ones a run left holding the test outputs. Read only. Returns a token for test_hosts_stop, which kills only these processes. |
| `test_hosts_stop` | Stop the test hosts listed by test_hosts_list, once, with the token it returned. Call it only after the user agreed in chat. Each pid is killed only if it still has the start time the list recorded. |
| `test_unwatch` | Stop watching a solution. |
| `test_watch` | Start watching a solution. While hot reload is active the server polls its TestResults folder and reports new failures (log notification and test_failures). ideHotReload is on, off or unknown; dotnetWatchRunning is true when dotnet watch runs. |

## Bookmarks

| Tool | Purpose |
| --- | --- |
| `bookmark_get` | Get one Visual Studio bookmark by id. |
| `bookmark_list` | List Visual Studio bookmarks. Visual Studio only in v1. |
| `bookmark_remove` | Remove a Visual Studio bookmark by id. |
| `bookmark_set` | Set a Visual Studio bookmark at a line. file must be a full path; line is 1-based; name is optional. |

## Errors and CI

| Tool | Purpose |
| --- | --- |
| `ci_failure` | The first failing CI job of a pull request and the error lines of its log, through gh. Read only. When no line looks like an error, the end of the log is returned. maxLines caps the lines. |
| `error_changes` | List diagnostics that appeared after the given snapshot. Optional codes and sources filters as in error_list. |
| `error_ingest` | Store build output (dotnet build or MSBuild text) so error_list and error_changes can answer from it. Replaces the previous output for the same solution. |
| `error_list` | List diagnostics from the last ingested build. kind is errors, warnings or notices. codes and sources are optional comma-separated filters (sources: Compiler, CodeAnalysis, NuGet, Other). |
| `error_snapshot` | Record the current diagnostics under a new snapshot id. Returns only the id; call error_changes with it later. |

## Extensions and packages

| Tool | Purpose |
| --- | --- |
| `extension_add_confirm` | Run a planned extension add. Use only after the user has approved the command returned by extension_add_plan. |
| `extension_add_plan` | Plan an extension add. Returns the exact command and a token. Nothing is installed until extension_add_confirm is called with the token and the user has approved it. |
| `extension_info` | Describe an extension. VsCode reads the installed package.json; VisualStudio queries the Marketplace. |
| `extension_list` | List installed VS Code extensions (ide = VsCode). Visual Studio listing is not available yet. |
| `extension_workspace_setup` | Recommend the official C# extensions (C# Dev Kit, C#) in the folder's .vscode/extensions.json. When C# Dev Kit is installed, also write dotnet.defaultSolution to .vscode/settings.json. Existing keys are kept. |
| `package_check` | Check NuGet, pip and npm package setup in a folder: nuget.config, Directory.Packages.props, xunit analyzers, pinned requirements, and npm lockfiles. Reads files only; nothing is installed. |
| `tool_install` | Install a missing tool with winget through the elevated script helper. tool is one of pwsh, gitbash, python, node (wsl gets instructions only). action 'plan' returns the exact command and a one-time token and installs nothing; ask the user, and only if they say yes call action 'confirm' with the token. action 'decline' records that the user said no, so the install is not offered again. The elevated helper must have been set up once by the user with 'setup tools'. |

## Config and repo

| Tool | Purpose |
| --- | --- |
| `build_env` | List the Visual Studio installs on this machine (stable and prerelease) with their MSBuild path and whether the VSSDK targets a VSIX build needs are present. vsSdkTargetsPath is null when the extension development workload is missing. |
| `config_read` | Read a config file with secret values redacted. The file must lie under the solution folder, the project folder, the user profile, AppData, or a Remote Debugger search root. |
| `config_resolve` | Resolve the real path of a config file. Kinds: RemoteDebugger, LaunchSettings, AppSettings, UserSettings, SolutionSettings. LaunchSettings and AppSettings need projectPath. SolutionSettings needs solutionPath. |
| `pr_checks` | Status of each CI check of a pull request, read through the GitHub CLI (gh). Read only. bucket is pass, fail, pending, skipping or cancel. Use it instead of waiting in a loop. |
| `repo_setup` | Repo setup, read only. action 'scan': find projects and launch scripts in the repo that need administrator rights (app.manifest requireAdministrator, elevation markers in the scripts a launch or debug configuration runs), the repo's CI branches and checks, instruction files, hooks and CODEOWNERS, and what GitHub says protects the branches (live, through gh; 'checked: false' means gh could not answer, not that nothing is protected). action 'status': the elevated list, this user's mode (listed, always, never), the recorded rules and what is still pending. repoPath is any folder inside the repo. Nothing is changed: ask the user, then have them run 'elev repo-setup <repo>'. |

## Server, live tests and self-test

| Tool | Purpose |
| --- | --- |
| `live_test_start` | Start live testing for a solution with dotnet watch test. Works in Visual Studio Community, which has no Live Unit Testing. Each run writes TestResults/live.trx; failures are reported through test_failures and the error log. |
| `live_test_stop` | Stop live testing for a solution started by live_test_start. |
| `self_test_list` | List the helpers with their versions, the MCP server (Core) version, and whether each helper has a self-test. |
| `self_test_read` | Read a helper's self-test markdown, which is embedded in its library. |
| `server_control_set` | Set the control state for every aidevtools server on this machine. state: paused (tool calls are refused and each chat is told), closing (servers exit so an update can replace their files), or running (resume). The update order is paused, closing, update, running. |
| `server_control_status` | Read the control state shared by every aidevtools server on this machine. |

## Terminal and scripts

| Tool | Purpose |
| --- | --- |
| `dotnet_diag` | Wrap the .NET diagnostic tools. action 'status' lists each tool with its path and version, or that it is missing (with the install command). 'processes' lists .NET processes (dotnet-dump ps). 'stack' prints managed stacks for pid. 'dump' writes a memory dump of pid. 'gcdump' writes a GC heap snapshot of pid. 'counters' and 'trace' collect for 'seconds' (1 to 300) from pid. 'symbols' downloads symbols for the absolute 'path'. path is an optional output file for dump, gcdump, counters and trace (default: a file under %TEMP%\aidev-diag). Output files are written, never deleted. Nothing is installed by this tool. |
| `output_channel_set` | Store which IDE a named output channel writes to, in .claude/aidevhelpers.json next to the .sln. |
| `output_write` | Write text to a named output channel. The IDE is the ide argument, or the one stored for the channel. Visual Studio and VS Code channels can be written; they cannot be read back through this tool. |
| `run_elevated_script` | Run an approved debug script elevated through the Script Helper broker. projectRoot is the folder with the .elevated.json manifest; script is the path relative to it; args are passed to the script in order. Returns the exit code and output, or a refusal reason. |
| `script_run` | Run a script from a repo (.ps1, .cmd, .bat, .sh, .py, .js) with the interpreter that fits it: PowerShell 7 or 5.1 from #Requires, cmd, Git Bash, Python or Node. Output is captured, the run is stopped after the timeout or after a stretch with no output, and arguments are passed as data with no shell in between. repoPath is a folder inside the repo; script is a path inside it. The result says which interpreter ran and why. When 'advice' is present the script looks stuck (it keeps failing or hangs): ask the user, and only if they agree call again with interactive true, which runs it in a visible terminal tab on their desktop so they see prompts and errors the script does not report (not elevated). With elevated true as well, an approved PowerShell script from the repo's .elevated.json runs elevated in a visible window through the Script Helper broker, and the transcript comes back; an unapproved script is refused. If the interpreter is missing, the result says how to get it; nothing is installed. |
| `terminal_session` | Run commands in a terminal session. action: open (create a session with launchMode InteractiveUser (visible on the user's desktop, default) or ServiceAccount (headless, under the service account) and a first tab; elevated=true opens the session with UAC so its tabs run as administrator; tabName optional), tab (new tab in sessionId; tabName optional), exec (run command in sessionId + tabId; timeoutSeconds optional, default 60), list (open sessions), close (end sessionId), types (read only: the terminal types installed on this machine, which the helper and Windows Terminal know, and install hints; a type with missing=true is installed but not registered, so ask the user to run 'elev terminals setup'). |

## Projects

Standards come from `.aidevhelpers/project-standards.json` (base framework, allowed suffixes, properties, analyzers, exceptions).

| Tool | Purpose |
| --- | --- |
| `project_types_list` | List `dotnet new` project templates, with optional language and tag filters. Read only. |
| `project_audit` | Check projects against the standards: framework (exceptions are info), properties, analyzers, test SDK, differing package versions. Read only; `ok` is false on any error. |
| `project_fix_plan` / `project_fix_confirm` | Plan, then apply, the edits that bring projects to the standards. Confirm re-audits and builds; any failure restores every file. |
| `project_add_plan` / `project_add_confirm` | Plan, then add a project from a template to a `.slnx`, set up to the standards. A failed build removes it and restores the solution. |
| `project_add_test_plan` / `project_add_test_confirm` | Plan, then add `<Source>.Tests` beside a source project, referencing it, with the repo's test runner. `seedTest` adds one failing test. The Test SDK and runner versions come from existing projects in the repo; if none pins one, the plan stops and asks the user in chat for the version. |

Tokens from the plan tools are single use and expire after 15 minutes. Plan tools write nothing.

## Safe use

- Start with `ide_list_instances` and read-only inspection tools. Select a target explicitly when several solutions are open.
- Tools that build, clean, publish, run tests, edit documents, change debugger state, launch or close IDEs, or run terminal commands can change user state. Use them only when the requested task authorizes that action and target.
- Never use `ide_kill_instance` or `ide_kill_process` against an unrelated user process. `ide_kill_process` is limited to processes previously started for clean or publish.
- `ide_get_locals`, `ide_evaluate`, stepping, and watch values require the debugger to be stopped as described by their schemas.

The full test procedure is available as `docs://ide-bridge/mcp-self-test`.

The bridge's own diagnostics are written to the Visual Studio Output pane named `AiDevHelpers IDE Bridge`. The MCP log tools expose that pane as the `vsout:AiDevHelpers IDE Bridge` channel while the IDE instance is connected; use `LogChannels` to confirm availability before querying it.
