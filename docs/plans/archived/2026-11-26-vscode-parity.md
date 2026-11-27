# VS Code parity for the IDE bridge

Status: done. Units 0 to 7, 9, 11 and 12 merged into wip. Unit 8 (Task List) dropped in v1. Unit 10 (Roslyn edit tools) is a separate helper plan, not started. Live checks not run: the VS Code events pipe and git routing need a running VS Code window.

Drafted by a read-only Sonnet agent from `ideActions.ts`, `IdeActions.cs`, `docs/ide-bridge.md`, `conformance/protocol.md` and `conformance/fixtures.json`. Nothing was run. Claims about file sizes and test helpers were not checked.

## Gap summary

- **Done in VS Code:** health, status, build, clean, publish, run tests, start and stop debugging, format, code cleanup, open document and solution, go to definition, find all references, build diagnostics, breakpoints (set, remove, list), step, debug state, evaluate, locals, watches, configuration, startup project (see the table in `docs/ide-bridge.md`), kill process.
- **Partial:** output read, search and clear (only lines the bridge recorded; a permanent API limit). Exception filters (DAP filters only, not VS's grouped list).
- **Missing, doable in TypeScript:** breakpoint enable, disable and condition; threads, stack frames, debugged processes, attach and detach; documents list, save and close; close solution; find and replace in files; project properties and items; get setting; status bar, info bar and activity log; undo group.
- **Missing, needs a language service:** code elements, semantic references and definition, rename symbol. Needs C# Dev Kit or C#.
- **Missing, large:** the Roslyn edit tools (method and member edits, add and delete, rename namespace, move type, TDD). These are C# engines that VS Code cannot host.
- **Not planned:** the events pipe, startup project, bookmarks (keep `unsupported` in v1), and the MCP lifecycle tools (VS only).

## Units

Each unit starts with a failing test and ends with the lite gate steps for the VS Code projects in `.claude/reference/lite-gate.md` (step 6): `npm --prefix src/Installers/VsCode/Core/AiDevTools.VsCode.Core run lint` and `... run test`, then `npm --prefix src/Installers/VsCode/Installer/AiDevTools.VsCode run build`, `... run lint` and `... run test`. Update `docs/ide-bridge.md` and `conformance/protocol.md` in each unit.

0. **Parity test.** A test lists the VS bridge actions. Each must be handled by VS Code or listed as `unsupported`. Files: `ideActions.ts`, a new `parity.test.ts`.
1. **Breakpoint control.** Enable, disable and condition, with a Core mapping from `hitCountType` to `hitCondition`. Test the mapping and that `confirm` false returns `planned`.
2. **Debugger inspection.** Threads, stack frames, debugged processes, detach all. Test reply shaping with a faked `customRequest`, and an empty result outside break mode.
3. **Processes and attach.** List processes, attach process. Test the attach plan without `confirm` and a refused pid. Exceptions only if question 3 allows it.
4. **Documents.** List, save and close documents; close solution. Test that a dirty document is refused without `discardChanges` or `saveFirst`, and that `confirm` false returns `planned`.
5. **Text search.** Find and replace in files, as a Core module. Test folder skips, the 500-match cap, and dirty documents skipped on replace.
6. **Language services.** Code elements, semantic references and definition, rename symbol, undo group. Test result shaping with a mocked `executeCommand`. Depends on question 2.
7. **Project and shell services.** Project properties, add and remove project item, get setting, status bar, info bar, activity log. Test csproj parsing and the item XML edit in Core.
8. **Task List approximation.** Only if question 4 says yes.
9. **Sync.** Update the "VS only" wording in `mcp-tools.md` and `IdeTools.cs`, and `fixtures.json`.
10. **Roslyn edit tools.** A separate helper plan (answer 1). Not part of this plan.
11. **Events pipe.** Add the VS Code side of the events pipe and update `protocol.md` (answer 6). The VS side already exists. Plan the VS Code side in detail before starting this unit.
12. **Git routing.** Route the `git_*` MCP tools to the VS Code git actions (answer 7). Plan the routing and its tests in detail before starting this unit.

## Decisions

Answers from the user:

1. **Roslyn edit tools:** a separate helper plan (out-of-process MSBuildWorkspace). Unit 10 is that plan, not part of this one.
2. **Language services:** `semantic*`, `listCodeElements` and `renameSymbol` need C# Dev Kit or C#. With neither installed, return a clear error.
3. **Exceptions:** reduced model (DAP filters only) for `listExceptions` and `setExceptionBreak`. Document it as partial.
4. **Task List:** `unsupported` in v1. Unit 8 is dropped.
5. **Bookmarks:** `unsupported` in v1.
6. **Events pipe:** in scope now. Unit 11 covers it, and `protocol.md` changes with it.
7. **`git_*` routing:** in scope now. Unit 12 covers it.

## Risks

- The conformance fixtures only check the generic handler, which refuses everything except `health`. Unit 0 is needed to catch gaps.
- `ideActions.ts` is one large switch. Each unit should split its actions into modules.
- Units 1 to 7 assume the `vscode` module can be mocked in tests, as `outputWrite.test.ts` does today. Not checked.
