# VS Code parity for the IDE bridge

Status: draft, needs the user's answers to the open questions before work starts.

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
10. **Roslyn edit tools.** A separate plan, depending on question 1.

## Open questions

1. Should the Roslyn edit tools stay VS only, or get a separate plan for an out-of-process MSBuildWorkspace helper?
2. Should `semantic*`, `listCodeElements` and `renameSymbol` depend on C# Dev Kit or C#, with a clear error when neither is installed?
3. Is a reduced exception model (DAP filters only) acceptable for `listExceptions` and `setExceptionBreak`?
4. Should the Task List be approximated with Problems entries, or kept `unsupported`?
5. Should bookmarks use extension storage, or stay `unsupported`?
6. Should the events pipe be added for VS Code? That changes `protocol.md`.
7. Should the `git_*` MCP tools route to the VS Code git actions in this effort?

## Risks

- The conformance fixtures only check the generic handler, which refuses everything except `health`. Unit 0 is needed to catch gaps.
- `ideActions.ts` is one large switch. Each unit should split its actions into modules.
- Units 1 to 7 assume the `vscode` module can be mocked in tests, as `outputWrite.test.ts` does today. Not checked.
