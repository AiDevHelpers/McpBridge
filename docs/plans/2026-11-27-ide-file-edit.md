# Plan: file_edit on the IDE paths (replaceInFile bridge action, full engine)

Branch: `feat/modelserver-file-edit`. Worktree: `F:\aidevhelpers\wip-worktree`.
Status: draft, awaiting approval to start. Follows the decision "full bridge engine" (2026-11-27).

## Why
`file_edit` is refused on the VisualStudio and VsCode paths (`ToolsetRouting`). `ide_replace_in_files` is solution-wide with no file filter, so it cannot stand in for one file's edit. The decision is a new single-file action, `replaceInFile`, in both bridges, with the same rules as `FileEditHelper`: byte-exact backup, BOM kept, atomic write, anchor match, batch stops at the first failure.

## Rules to port (from `src/Helpers/FileEditHelper/src/FileEditEngine.cs`)
- Backup before every write: `<root>/.aidevhelpers/backups/yyyyMMdd_HHmmss_fff_<6hex>_<safe path>`, byte-exact.
- Atomic write: temp file in the same folder, then replace, with a retry.
- BOM detection and preservation: UTF-8, UTF-16 LE, UTF-16 BE.
- `oldText` must match (`RequireAnchorMatch` default true); `occurrence` 0 = all, N = the Nth.
- `root` is the repo root; the path must stay under it (`WorkspacePathGuard` rules, including links and junctions).

## Units (each: failing test first, build, test, commit, pull wip, retest, push)

Rule (2026-11-27): at the end of each unit, fetch and merge `origin/wip` into the branch, resolve conflicts, rerun that unit's tests, then push. Push only after the pull.
- **U1. Contract.** `conformance/protocol.md` and `conformance/fixtures.json`: `replaceInFile` arguments (`file`, `edits` of `{oldText, newText, occurrence}`, `confirm`), the plan reply (`planned`, per-file match count, no write), the applied reply (`backupPath`, `linesChanged`), and the refusals (outside root, no match, BOM error). Done when the fixtures cover each refusal.
- **U2. VS Code bridge.** `src/Installers/VsCode/Installer/AiDevTools.VsCode/src/ideActions.ts`: `replaceInFile` in the same `switch` style as `replaceInFiles`, plus the engine in a new `fileEdit.ts`. Tests in `*.test.ts` against the fixtures. Gate: `npm test` for the extension.
- **U3. Visual Studio bridge.** `src/Installers/Vs2026/Core/src/IdeActions.cs`: `replaceInFile` and the engine in a new C# file. Tests in `VsParityTests` style. Gate: needs the VS MSBuild (`dotnet build` cannot load the net48 tasks; see the dogfood log). If that MSBuild is not available here, stop at U3 and report NOT RUN.
- **U4. Server.** `src/MCP/Core/src/IdeTools.cs` (or `FileEditTools.cs`): on an IDE path, `file_edit` calls `replaceInFile` through `IdeInstanceRouter`; on Standalone it keeps the current path. `ToolsetRouting` stops refusing `file_edit` on IDE paths. Tests: routing picks the bridge action, Standalone unchanged.
- **U5. Docs and gates.** `docs/ide-bridge.md`, `src/Common/src/Resources/mcp-tools.md`, `DocsMatchCodeTests`, `conformance` test run, lite gate. Dogfood entry.

## Risks
- The bridges are separate builds. U3 may not build here (see U3).
- Two engines must stay in step. The conformance fixtures are the shared check; a fixture that passes in one bridge and fails in the other blocks the unit.
- `file_edit` on an IDE path must not bypass the path guard. The guard runs in the bridge and is covered by fixtures for a junction and a path outside root.

## Open
- Confirm the VS MSBuild is installed on this machine before U3 starts.
