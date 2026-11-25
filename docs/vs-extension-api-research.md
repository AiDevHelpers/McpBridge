# Visual Studio extension API: what can help editing (research, 2026-11-25)

Sources: Microsoft Learn search results for Roslyn `SymbolFinder`, `Renamer`, EnvDTE `EditPoint`/`TextDocument`,
EnvDTE80 `CodeElement2.RenameSymbol`, Quick Actions docs, and the VS extensibility migration guide. Not verified by running code.

## Options by feature

| Feature | API | Notes |
|---|---|---|
| Find all references | Roslyn `SymbolFinder.FindReferencesAsync(symbol, solution, ct)` | Needs the symbol (`ISymbol`) and a `Solution`. EnvDTE has no find-all-references call. |
| Find declarations | Roslyn `SymbolFinder.FindDeclarationsAsync(project, name, ignoreCase, ct)` | Name lookup across a project. |
| Rename symbol | Roslyn `Renamer.RenameSymbolAsync(solution, symbol, options, newName, ct)` | Returns a new `Solution`; the caller applies it. The `OptionSet` overload is obsolete. |
| Rename (EnvDTE) | `EnvDTE80.CodeElement2.RenameSymbol(newName)` | Limited to the current project. Must be cast to `CodeElement2` (late binding fails). |
| Replace text | `EditPoint.ReplaceText`, `TextDocument.ReplacePattern` | Deprecated in VS 2022+. The docs point to modern find/replace APIs. Do not use. |
| Quick actions / code fixes | Roslyn code fix providers via the editor's light bulb (`Ctrl+.`) | Applies analyzer fixes. Needs the editor/workspace. |

## Where to get the Roslyn workspace from an extension

- EnvDTE alone does not give a Roslyn `Solution`. The VS Roslyn workspace (`VisualStudioWorkspace`, from
  `Microsoft.VisualStudio.LanguageServices`) is obtained through MEF (`IComponentModel`) inside the VS process.
  Not verified here: the exact type and assembly names, and whether the bridge's net48 package can reference them.
- The bridge already runs inside VS (`BridgePackage`, `IdeActions`), so this is the right host.

## Recommended tool set (not built yet)

1. `ide_find_references(file, line, column)`: resolve the symbol at the position, run `FindReferencesAsync`, return file and line for each reference.
2. `ide_rename_symbol(file, line, column, newName, confirm)`: plan the rename (list affected files), then apply only with `confirm`, same pattern as the git and test-host tools.
3. `ide_code_fixes(file, line, column)`: list the Roslyn fixes at a position. Apply one by id.
4. `ide_go_to_definition(file, line, column)`: `FindDeclarationsAsync` or `ISymbol.Locations`.

Each tool is read-only except rename and fix application, which need confirmation.

## Open questions

- The VS Extensibility model (out-of-process, `VisualStudio.Extensibility`) is the modern path, but the bridge is VSSDK. Staying on VSSDK is simplest.
- Whether the rename and fix tools should work on unsaved buffers (the Roslyn workspace sees the editor buffer).
