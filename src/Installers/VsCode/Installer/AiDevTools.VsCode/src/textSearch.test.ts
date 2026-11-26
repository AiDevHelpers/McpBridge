import { mkdirSync, mkdtempSync, readFileSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import * as path from "node:path";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { buildSearchPattern, findInFiles, MAX_SEARCH_MATCHES, planReplace, walkFiles } from "./textSearch.js";

// The VS Code API stand-in: workspace folders and open documents, which replaceInFiles reads through ideActions.ts.
const vscodeState = vi.hoisted(() => ({
    workspaceFolders: [] as Array<{ uri: { fsPath: string } }>,
    textDocuments: [] as Array<{ uri: { fsPath: string; scheme: string }; isDirty: boolean }>,
}));

vi.mock("vscode", () => ({
    window: { createOutputChannel: () => ({ append: () => undefined }), tabGroups: { all: [] } },
    workspace: {
        get workspaceFolders() {
            return vscodeState.workspaceFolders;
        },
        get textDocuments() {
            return vscodeState.textDocuments;
        },
        getConfiguration: () => ({ get: () => undefined }),
    },
    debug: { onDidStartDebugSession: () => ({ dispose: () => undefined }), onDidTerminateDebugSession: () => ({ dispose: () => undefined }), activeDebugSession: undefined },
    commands: { executeCommand: async () => undefined },
    Uri: { file: (p: string) => ({ fsPath: p, scheme: "file" }) },
}));

const { runIdeAction } = await import("./ideActions.js");

function makeFolder(): string {
    return mkdtempSync(path.join(tmpdir(), "textsearch-"));
}

function writeAt(root: string, relative: string, text: string): string {
    const full = path.join(root, relative);
    mkdirSync(path.dirname(full), { recursive: true });
    writeFileSync(full, text, "utf8");
    return full;
}

beforeEach(() => {
    vscodeState.workspaceFolders = [];
    vscodeState.textDocuments = [];
});

describe("walkFiles", () => {
    it("skips bin, obj, .git and node_modules", () => {
        const root = makeFolder();
        const kept = writeAt(root, "src/Program.cs", "hit");
        writeAt(root, "bin/Debug/app.cs", "hit");
        writeAt(root, "obj/gen.cs", "hit");
        writeAt(root, ".git/config", "hit");
        writeAt(root, "node_modules/pkg/index.js", "hit");

        expect(walkFiles([root])).toEqual([kept]);
    });
});

describe("findInFiles", () => {
    it("matches literally by default and ignores case unless matchCase is set", () => {
        const root = makeFolder();
        const file = writeAt(root, "a.txt", "Alpha.beta\naxbeta\nALPHA.BETA\n");

        const insensitive = findInFiles([file], buildSearchPattern("alpha.beta", false, false));
        expect(insensitive.matches.map((m) => m.line)).toEqual([1, 3]);

        const sensitive = findInFiles([file], buildSearchPattern("alpha.beta", true, false));
        expect(sensitive.matches.map((m) => m.line)).toEqual([]);
    });

    it("caps the result at 500 matches and flags truncation only when a 501st match exists", () => {
        const root = makeFolder();
        const many = writeAt(root, "many.txt", Array.from({ length: MAX_SEARCH_MATCHES + 100 }, () => "hit").join("\n"));
        const exact = writeAt(root, "exact.txt", Array.from({ length: MAX_SEARCH_MATCHES }, () => "hit").join("\n"));

        const capped = findInFiles([many], buildSearchPattern("hit", false, false));
        expect(capped.matches).toHaveLength(500);
        expect(capped.truncated).toBe(true);

        const full = findInFiles([exact], buildSearchPattern("hit", false, false));
        expect(full.matches).toHaveLength(500);
        expect(full.truncated).toBe(false);
    });

    it("reports the file, line number and line text of each match", () => {
        const root = makeFolder();
        const file = writeAt(root, "b.txt", "one\nneedle here\nthree\n");

        const result = findInFiles([file], buildSearchPattern("needle", false, false));
        expect(result).toEqual({ matches: [{ file, line: 2, text: "needle here" }], truncated: false });
    });
});

describe("planReplace", () => {
    it("counts matches per file and skips a file open with unsaved changes", () => {
        const root = makeFolder();
        const a = writeAt(root, "a.txt", "x x\nx\n");
        const b = writeAt(root, "b.txt", "x\n");
        const none = writeAt(root, "c.txt", "nothing\n");

        const plan = planReplace([a, b, none], buildSearchPattern("x", false, false), [a]);

        expect(plan.planned).toEqual([{ file: b, matches: 1 }]);
        expect(plan.skipped).toEqual([a]);
    });
});

describe("replaceInFiles", () => {
    it("without confirm, plans per-file counts and writes nothing", async () => {
        const root = makeFolder();
        const file = writeAt(root, "src/a.txt", "old old\nold\n");
        vscodeState.workspaceFolders = [{ uri: { fsPath: root } }];

        const reply = await runIdeAction("replaceInFiles", { pattern: "old", replacement: "new" });

        // Counts matching lines, as the C# version does: "old old" is one line.
        expect(reply).toEqual({ planned: true, would: { files: [{ file, matches: 2 }], skipped: [] } });
        expect(readFileSync(file, "utf8")).toBe("old old\nold\n");
    });

    it("with confirm, writes the replacement and skips a dirty open document", async () => {
        const root = makeFolder();
        const dirty = writeAt(root, "dirty.txt", "old\n");
        const clean = writeAt(root, "clean.txt", "old\n");
        vscodeState.workspaceFolders = [{ uri: { fsPath: root } }];
        vscodeState.textDocuments = [{ uri: { fsPath: dirty, scheme: "file" }, isDirty: true }];

        const reply = await runIdeAction("replaceInFiles", { pattern: "old", replacement: "new", confirm: true });

        expect(readFileSync(dirty, "utf8")).toBe("old\n");
        expect(readFileSync(clean, "utf8")).toBe("new\n");
        expect(reply).toEqual({
            planned: false,
            applied: { files: [{ file: clean, matches: 1 }], skipped: [{ file: dirty, reason: "open with unsaved changes" }] },
            failed: [],
        });
    });

    it("treats a literal replacement's dollar signs as text", async () => {
        const root = makeFolder();
        const file = writeAt(root, "price.txt", "cost X\n");
        vscodeState.workspaceFolders = [{ uri: { fsPath: root } }];

        await runIdeAction("replaceInFiles", { pattern: "X", replacement: "$& $$", confirm: true });

        expect(readFileSync(file, "utf8")).toBe("cost $& $$\n");
    });
});
