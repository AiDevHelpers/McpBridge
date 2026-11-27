import { beforeEach, describe, expect, it, vi } from "vitest";
import {
    changedFiles,
    dedupeLocations,
    lastEditGroup,
    recordEditGroup,
    requireCSharpExtension,
    resetEditGroups,
    shapeCodeElements,
    type SymbolNode,
} from "./languageActions.js";

// A stand-in for the VS Code API. Language features are commands in the extension host, so executeCommand routes them.
const executeCommand = vi.fn<(command: string, ...args: unknown[]) => Promise<unknown>>();
const applyEdit = vi.fn<(edit: unknown) => Promise<boolean>>(async () => true);
const getExtension = vi.fn<(id: string) => unknown>(() => undefined);
const createOutputChannel = vi.fn(() => ({ append: () => undefined }));

vi.mock("vscode", () => ({
    window: { createOutputChannel: () => createOutputChannel(), showTextDocument: async () => undefined, activeTextEditor: undefined },
    workspace: {
        workspaceFolders: [{ uri: { fsPath: "/ws" } }],
        getConfiguration: () => ({ get: () => undefined }),
        openTextDocument: async () => ({
            getWordRangeAtPosition: () => ({}),
            getText: () => "OldName",
        }),
        applyEdit: (edit: unknown) => applyEdit(edit),
    },
    debug: { onDidStartDebugSession: () => ({ dispose: () => undefined }), onDidTerminateDebugSession: () => ({ dispose: () => undefined }), activeDebugSession: undefined },
    commands: { executeCommand: (command: string, ...args: unknown[]) => executeCommand(command, ...args) },
    extensions: { getExtension: (id: string) => getExtension(id) },
    Uri: { file: (p: string) => ({ fsPath: p }), joinPath: (_root: unknown, p: string) => ({ fsPath: `/ws/${p}` }) },
    Position: function (line: number, character: number) {
        return { line, character };
    },
    Selection: function (anchor: unknown) {
        return { anchor, active: anchor };
    },
    Range: function () {
        return {};
    },
    TextEditorRevealType: { InCenter: 2 },
}));

const { runIdeAction } = await import("./ideActions.js");

function symbol(name: string, kind: number, startLine: number, endLine: number, children: SymbolNode[] = []): SymbolNode {
    return { name, kind, range: { start: { line: startLine }, end: { line: endLine } }, children };
}

describe("requireCSharpExtension", () => {
    it("throws an error that names both the C# Dev Kit and the C# extension when neither is installed", () => {
        expect(() => requireCSharpExtension(() => undefined)).toThrow(/ms-dotnettools\.csdevkit/);
        expect(() => requireCSharpExtension(() => undefined)).toThrow(/ms-dotnettools\.csharp/);
    });

    it("passes when either extension is installed", () => {
        expect(() => requireCSharpExtension((id) => (id === "ms-dotnettools.csharp" ? {} : undefined))).not.toThrow();
        expect(() => requireCSharpExtension((id) => (id === "ms-dotnettools.csdevkit" ? {} : undefined))).not.toThrow();
    });

    it("is raised by listCodeElements when neither extension is installed", async () => {
        getExtension.mockReturnValue(undefined);
        await expect(runIdeAction("listCodeElements", { file: "/ws/A.cs" })).rejects.toThrow(/ms-dotnettools\.csdevkit.*ms-dotnettools\.csharp/);
    });
});

describe("shapeCodeElements", () => {
    it("lists namespaces, types and members with 1-based line ranges, in the order the C# reply uses", () => {
        const tree = [
            symbol("App", 2, 1, 20, [
                symbol("Widget", 4, 3, 18, [
                    symbol("count", 7, 4, 4),
                    symbol("Run(int)", 5, 5, 8),
                    symbol("Size", 6, 10, 12),
                    symbol("Widget()", 8, 14, 15),
                ]),
            ]),
        ];

        expect(shapeCodeElements(tree)).toEqual([
            { kind: "NamespaceDeclaration", name: "App", start_line: 2, end_line: 21 },
            { kind: "ClassDeclaration", name: "Widget", start_line: 4, end_line: 19 },
            { kind: "MethodDeclaration", name: "Run", start_line: 6, end_line: 9 },
            { kind: "PropertyDeclaration", name: "Size", start_line: 11, end_line: 13 },
            { kind: "ConstructorDeclaration", name: "Widget", start_line: 15, end_line: 16 },
        ]);
    });
});

describe("renameSymbol", () => {
    beforeEach(() => {
        executeCommand.mockReset();
        applyEdit.mockClear();
        getExtension.mockReturnValue({});
        executeCommand.mockImplementation(async (command: string) => {
            if (command === "vscode.executeDocumentRenameProvider") {
                return {
                    entries: () => [
                        [{ fsPath: "/ws/A.cs" }, [{}]],
                        [{ fsPath: "/ws/B.cs" }, [{}, {}]],
                    ],
                };
            }
            return undefined;
        });
    });

    it("plans without confirm: lists the files and never calls applyEdit", async () => {
        const reply = await runIdeAction("renameSymbol", { file: "/ws/A.cs", line: 3, column: 5, newName: "NewName" });

        expect(reply).toEqual({ planned: true, would: { symbol: "OldName", new_name: "NewName", files: ["/ws/A.cs", "/ws/B.cs"] } });
        expect(applyEdit).not.toHaveBeenCalled();
    });

    it("applies with confirm and reports the files as applied", async () => {
        const reply = await runIdeAction("renameSymbol", { file: "/ws/A.cs", line: 3, column: 5, newName: "NewName", confirm: true });

        expect(reply).toEqual({ planned: false, applied: { symbol: "OldName", new_name: "NewName", files: ["/ws/A.cs", "/ws/B.cs"] } });
        expect(applyEdit).toHaveBeenCalledTimes(1);
    });
});

describe("changedFiles", () => {
    it("returns each changed file once, in the order the edit lists them", () => {
        expect(changedFiles([{ fsPath: "/ws/A.cs" }, { fsPath: "/ws/B.cs" }, { fsPath: "/ws/A.cs" }])).toEqual(["/ws/A.cs", "/ws/B.cs"]);
    });
});

describe("dedupeLocations", () => {
    it("drops a location that repeats the same file, line and column", () => {
        const a = { file: "/ws/A.cs", line: 1, column: 2 };
        expect(dedupeLocations([a, { ...a }, { file: "/ws/A.cs", line: 3, column: 1 }])).toEqual([a, { file: "/ws/A.cs", line: 3, column: 1 }]);
    });
});

describe("undoGroup", () => {
    beforeEach(() => {
        resetEditGroups();
        executeCommand.mockReset();
        executeCommand.mockResolvedValue(undefined);
        getExtension.mockReturnValue({});
    });

    it("refuses when this bridge has applied no edit group", async () => {
        await expect(runIdeAction("undoGroup", { name: "Rename" })).rejects.toThrow(/has not applied an edit group/);
        expect(executeCommand).not.toHaveBeenCalledWith("undo");
    });

    it("plans without confirm and does not run undo", async () => {
        recordEditGroup({ name: "renameSymbol", files: ["/ws/A.cs"] });
        const reply = await runIdeAction("undoGroup", { name: "Rename" });

        expect(reply).toMatchObject({ planned: true, would: { name: "Rename", undone: "renameSymbol", files: ["/ws/A.cs"] } });
        expect(executeCommand).not.toHaveBeenCalledWith("undo");
    });

    it("runs undo with confirm and forgets the group", async () => {
        recordEditGroup({ name: "renameSymbol", files: ["/ws/A.cs"] });
        const reply = await runIdeAction("undoGroup", { name: "Rename", confirm: true });

        expect(reply).toMatchObject({ planned: false, applied: { name: "Rename", undone: "renameSymbol", files: ["/ws/A.cs"] } });
        expect(executeCommand).toHaveBeenCalledWith("undo");
        expect(lastEditGroup()).toBeUndefined();
    });
});
