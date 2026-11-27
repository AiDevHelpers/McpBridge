import { t } from "./strings.js";

// Pure parts of the language actions (listCodeElements, semanticDefinition, semanticReferences, renameSymbol, undoGroup).
// Nothing here imports the vscode module, so these can be tested without the extension host. ideActions.ts calls the
// VS Code commands and passes their results through these functions.

export const CSHARP_EXTENSION_IDS = ["ms-dotnettools.csdevkit", "ms-dotnettools.csharp"] as const;

/** Throws unless the C# Dev Kit or the C# extension is installed. The semantic actions need one of them, so no silent empty result. */
export function requireCSharpExtension(getExtension: (id: string) => unknown): void {
    if (CSHARP_EXTENSION_IDS.some((id) => getExtension(id) !== undefined)) {
        return;
    }
    throw new Error(t("csharpExtensionMissing"));
}

// vscode.SymbolKind values, written as numbers so this file needs no vscode import.
const NAMESPACE = 2;
const CLASS = 4;
const METHOD = 5;
const PROPERTY = 6;
const CONSTRUCTOR = 8;
const ENUM = 9;
const INTERFACE = 10;
const STRUCT = 22;

// Roslyn kind names for the declarations the Visual Studio reply names. Other symbol kinds (fields, events, ...) are skipped.
const ELEMENT_KINDS = new Map<number, string>([
    [NAMESPACE, "NamespaceDeclaration"],
    [CLASS, "ClassDeclaration"],
    [STRUCT, "StructDeclaration"],
    [INTERFACE, "InterfaceDeclaration"],
    [ENUM, "EnumDeclaration"],
    [METHOD, "MethodDeclaration"],
    [CONSTRUCTOR, "ConstructorDeclaration"],
    [PROPERTY, "PropertyDeclaration"],
]);

/** The fields of a vscode.DocumentSymbol that shaping reads. */
export interface SymbolNode {
    name: string;
    kind: number;
    range: { start: { line: number }; end: { line: number } };
    children: SymbolNode[];
}

/** One declaration in the listCodeElements reply, shaped like the C# CodeModelActions.ListCodeElements. */
export interface CodeElement {
    kind: string;
    name: string;
    start_line: number;
    end_line: number;
}

/** Flattens the document symbols depth-first, keeping namespaces, types and members, with 1-based lines. */
export function shapeCodeElements(symbols: readonly SymbolNode[]): CodeElement[] {
    const elements: CodeElement[] = [];
    const visit = (nodes: readonly SymbolNode[]): void => {
        for (const node of nodes) {
            const kind = ELEMENT_KINDS.get(node.kind);
            if (kind !== undefined) {
                elements.push({
                    kind,
                    // C# symbol names carry a parameter list ("Run(int)"); the reply uses the identifier, as the C# side does.
                    name: node.name.replace(/\(.*$/, ""),
                    start_line: node.range.start.line + 1,
                    end_line: node.range.end.line + 1,
                });
            }
            visit(node.children);
        }
    };
    visit(symbols);
    return elements;
}

/** A location in the reply, 1-based like the C# ToLocation. */
export interface SourceLocation {
    file: string;
    line: number;
    column: number;
}

export function shapeLocation(file: string, line0: number, character0: number): SourceLocation {
    return { file, line: line0 + 1, column: character0 + 1 };
}

/** Keeps the first of each file, line and column, as the C# reference search does. */
export function dedupeLocations(locations: readonly SourceLocation[]): SourceLocation[] {
    const seen = new Set<string>();
    return locations.filter((location) => {
        const key = `${location.file}:${location.line}:${location.column}`;
        if (seen.has(key)) {
            return false;
        }
        seen.add(key);
        return true;
    });
}

/** The unique file paths a rename or other WorkspaceEdit touches, in the order the edit lists them. */
export function changedFiles(entries: ReadonlyArray<{ fsPath: string }>): string[] {
    return [...new Set(entries.map((entry) => entry.fsPath))];
}

/**
 * The edit groups this bridge applied, newest last. VS Code's undo is one global stack with no names, so undoGroup
 * can only reverse the last group this bridge recorded.
 */
export interface EditGroup {
    name: string;
    files: string[];
}

const editGroups: EditGroup[] = [];

export function recordEditGroup(group: EditGroup): void {
    editGroups.push(group);
}

export function lastEditGroup(): EditGroup | undefined {
    return editGroups[editGroups.length - 1];
}

export function forgetLastEditGroup(): void {
    editGroups.pop();
}

export function resetEditGroups(): void {
    editGroups.length = 0;
}
