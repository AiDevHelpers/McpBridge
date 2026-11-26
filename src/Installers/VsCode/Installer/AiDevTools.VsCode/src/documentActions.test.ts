import * as path from "node:path";
import { beforeEach, describe, expect, it, vi, type Mock } from "vitest";
import { planCloseReply, planCloseSolutionReply, planSaveReply, refuseDirtyClose, refuseDirtySolution, sameFile } from "./documentActions.js";

// A stand-in for the VS Code API: what ideActions.ts touches for the document actions.
interface FakeDoc {
    uri: { fsPath: string; scheme: string };
    isDirty: boolean;
    save: Mock<() => Promise<boolean>>;
}

interface FakeTab {
    input: unknown;
}

const state = {
    docs: [] as FakeDoc[],
    tabs: [] as FakeTab[],
};
const executeCommand = vi.fn<(command: string) => Promise<undefined>>(async () => undefined);
const showTextDocument = vi.fn<(doc: unknown) => Promise<undefined>>(async () => undefined);
const tabClose = vi.fn<(tab: unknown) => Promise<boolean>>(async () => true);

class TabInputText {
    uri: { fsPath: string; scheme: string };
    constructor(uri: { fsPath: string; scheme: string }) {
        this.uri = uri;
    }
}

vi.mock("vscode", () => ({
    window: {
        createOutputChannel: () => ({ append: () => undefined }),
        showTextDocument: (doc: unknown) => showTextDocument(doc),
        tabGroups: {
            get all() {
                return [{ tabs: state.tabs }];
            },
            close: (tab: unknown) => tabClose(tab),
        },
    },
    workspace: {
        workspaceFolders: undefined,
        get textDocuments() {
            return state.docs;
        },
        getConfiguration: () => ({ get: () => undefined }),
    },
    debug: { onDidStartDebugSession: () => ({ dispose: () => undefined }), onDidTerminateDebugSession: () => ({ dispose: () => undefined }), activeDebugSession: undefined },
    commands: { executeCommand: (command: string) => executeCommand(command) },
    Uri: { file: (p: string) => ({ fsPath: p, scheme: "file" }) },
    TabInputText,
}));

const { runIdeAction } = await import("./ideActions.js");

const A = path.resolve("work", "a.cs");
const B = path.resolve("work", "b.cs");

function doc(fsPath: string, dirty: boolean): FakeDoc {
    const d: FakeDoc = {
        uri: { fsPath, scheme: "file" },
        isDirty: dirty,
        save: vi.fn(async () => {
            d.isDirty = false;
            return true;
        }),
    };
    return d;
}

function tabFor(fsPath: string): FakeTab {
    return { input: new TabInputText({ fsPath, scheme: "file" }) };
}

beforeEach(() => {
    state.docs = [];
    state.tabs = [];
    executeCommand.mockClear();
    showTextDocument.mockClear();
    tabClose.mockClear();
});

describe("pure document rules", () => {
    it("refuses a dirty close unless discardChanges is set, and names the file", () => {
        expect(() => refuseDirtyClose(A, true, false)).toThrow(A);
        expect(() => refuseDirtyClose(A, true, true)).not.toThrow();
        expect(() => refuseDirtyClose(A, false, false)).not.toThrow();
    });

    it("refuses a dirty solution close only when saveFirst is not set", () => {
        expect(() => refuseDirtySolution([A], false)).toThrow(A);
        expect(() => refuseDirtySolution([A], true)).not.toThrow();
        expect(() => refuseDirtySolution([], false)).not.toThrow();
    });

    it("shapes the planned replies", () => {
        expect(planSaveReply(A, true)).toEqual({ planned: true, would: { file: A, dirty: true } });
        expect(planCloseReply(A, true, true)).toEqual({ planned: true, would: { file: A, dirty: true, discard: true } });
        expect(planCloseSolutionReply([A], false)).toEqual({ planned: true, would: { dirtyDocuments: [A], saveFirst: false } });
    });

    it("compares paths the same way on this platform", () => {
        expect(sameFile(A, A)).toBe(true);
        expect(sameFile(A, B)).toBe(false);
    });
});

describe("listDocuments", () => {
    it("lists open text documents with saved and readOnly, matching the Visual Studio fields", async () => {
        state.docs = [doc(A, false), doc(B, true)];

        const reply = await runIdeAction("listDocuments", {});

        expect(reply).toEqual({
            documents: [
                { fullName: A, saved: true, readOnly: false },
                { fullName: B, saved: false, readOnly: false },
            ],
        });
        expect(executeCommand).not.toHaveBeenCalled();
    });
});

describe("saveDocument", () => {
    it("plans without confirm and saves nothing", async () => {
        const d = doc(A, true);
        state.docs = [d];

        const reply = await runIdeAction("saveDocument", { file: A });

        expect(reply).toEqual({ planned: true, would: { file: A, dirty: true } });
        expect(d.save).not.toHaveBeenCalled();
    });

    it("saves the document with confirm", async () => {
        const d = doc(A, true);
        state.docs = [d];

        const reply = await runIdeAction("saveDocument", { file: A, confirm: true });

        expect(reply).toEqual({ planned: false, applied: { file: A, saved: true } });
        expect(d.save).toHaveBeenCalledTimes(1);
    });

    it("throws for a file that is not open", async () => {
        state.docs = [doc(A, false)];

        await expect(runIdeAction("saveDocument", { file: B, confirm: true })).rejects.toThrow(B);
    });
});

describe("closeDocument", () => {
    it("refuses a dirty document without discardChanges, even without confirm", async () => {
        const d = doc(A, true);
        state.docs = [d];
        state.tabs = [tabFor(A)];

        await expect(runIdeAction("closeDocument", { file: A })).rejects.toThrow(A);
        await expect(runIdeAction("closeDocument", { file: A, confirm: true })).rejects.toThrow(A);
        expect(tabClose).not.toHaveBeenCalled();
        expect(d.save).not.toHaveBeenCalled();
    });

    it("plans without confirm and closes nothing", async () => {
        state.docs = [doc(A, false)];
        state.tabs = [tabFor(A)];

        const reply = await runIdeAction("closeDocument", { file: A });

        expect(reply).toEqual({ planned: true, would: { file: A, dirty: false, discard: false } });
        expect(tabClose).not.toHaveBeenCalled();
    });

    it("plans a discard without confirm and reverts nothing", async () => {
        state.docs = [doc(A, true)];
        state.tabs = [tabFor(A)];

        const reply = await runIdeAction("closeDocument", { file: A, discardChanges: true });

        expect(reply).toEqual({ planned: true, would: { file: A, dirty: true, discard: true } });
        expect(executeCommand).not.toHaveBeenCalled();
        expect(tabClose).not.toHaveBeenCalled();
    });

    it("closes the tab of a clean document with confirm", async () => {
        const d = doc(A, false);
        state.docs = [d];
        state.tabs = [tabFor(A), tabFor(B)];

        const reply = await runIdeAction("closeDocument", { file: A, confirm: true });

        expect(reply).toEqual({ planned: false, applied: { file: A, closed: true } });
        expect(tabClose).toHaveBeenCalledTimes(1);
        expect(tabClose).toHaveBeenCalledWith(state.tabs[0]);
        expect(executeCommand).not.toHaveBeenCalled();
        expect(d.save).not.toHaveBeenCalled();
    });

    it("reverts a dirty document, then closes it, when discardChanges and confirm are set", async () => {
        const d = doc(A, true);
        state.docs = [d];
        state.tabs = [tabFor(A)];

        const reply = await runIdeAction("closeDocument", { file: A, discardChanges: true, confirm: true });

        expect(reply).toEqual({ planned: false, applied: { file: A, closed: true } });
        expect(showTextDocument).toHaveBeenCalledWith(d);
        expect(executeCommand).toHaveBeenCalledWith("workbench.action.files.revert");
        expect(tabClose).toHaveBeenCalledTimes(1);
        expect(d.save).not.toHaveBeenCalled();
    });

    it("throws for a file that is not open", async () => {
        state.docs = [doc(A, false)];

        await expect(runIdeAction("closeDocument", { file: B, confirm: true })).rejects.toThrow(B);
    });
});

describe("closeSolution", () => {
    it("lists the dirty documents without confirm and changes nothing", async () => {
        const dirty = doc(A, true);
        state.docs = [dirty, doc(B, false)];

        const reply = await runIdeAction("closeSolution", {});

        expect(reply).toEqual({ planned: true, would: { dirtyDocuments: [A], saveFirst: false } });
        expect(dirty.save).not.toHaveBeenCalled();
        expect(executeCommand).not.toHaveBeenCalled();
    });

    it("refuses with confirm when a document is dirty and saveFirst is not set", async () => {
        const dirty = doc(A, true);
        state.docs = [dirty];

        await expect(runIdeAction("closeSolution", { confirm: true })).rejects.toThrow(A);
        expect(dirty.save).not.toHaveBeenCalled();
        expect(executeCommand).not.toHaveBeenCalled();
    });

    it("saves the dirty documents with saveFirst, then closes the folder", async () => {
        const dirty = doc(A, true);
        const clean = doc(B, false);
        state.docs = [dirty, clean];

        const reply = await runIdeAction("closeSolution", { saveFirst: true, confirm: true });

        expect(reply).toEqual({ planned: false, applied: { closed: true, savedDocuments: [A] } });
        expect(dirty.save).toHaveBeenCalledTimes(1);
        expect(clean.save).not.toHaveBeenCalled();
        expect(executeCommand).toHaveBeenCalledWith("workbench.action.closeFolder");
    });

    it("closes with confirm when nothing is dirty", async () => {
        state.docs = [doc(A, false)];

        const reply = await runIdeAction("closeSolution", { confirm: true });

        expect(reply).toEqual({ planned: false, applied: { closed: true, savedDocuments: [] } });
        expect(executeCommand).toHaveBeenCalledWith("workbench.action.closeFolder");
    });
});
