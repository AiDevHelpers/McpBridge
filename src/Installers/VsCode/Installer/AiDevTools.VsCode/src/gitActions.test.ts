import { join } from "node:path";

// Windows drive paths are only absolute on Windows; on other hosts (CI runs Linux) use a rooted path.
const DRIVE = process.platform === "win32" ? "F:" : "";
import { beforeEach, describe, expect, it, vi } from "vitest";

// A fake VS Code git extension, recording every call the actions make.
const calls: Array<[string, unknown[]]> = [];
let repos: unknown[] = [];

vi.mock("vscode", () => ({
    extensions: {
        getExtension: () => ({ exports: { getAPI: () => ({ repositories: repos }) } }),
    },
    window: { createOutputChannel: () => ({ append: () => undefined }) },
    workspace: { workspaceFolders: undefined, getConfiguration: () => ({ get: () => undefined }) },
    debug: { onDidStartDebugSession: () => ({ dispose: () => undefined }), onDidTerminateDebugSession: () => ({ dispose: () => undefined }), activeDebugSession: undefined },
    commands: { executeCommand: async () => undefined },
    Uri: { file: (p: string) => ({ fsPath: p }) },
}));

function fakeRepo(root = DRIVE + "/work/app") {
    const record = (name: string) => async (...args: unknown[]) => { calls.push([name, args]); };
    return {
        rootUri: { fsPath: root },
        state: {
            HEAD: { name: "main", ahead: 1, behind: 0 },
            indexChanges: [{ uri: { fsPath: DRIVE + "/work/app/a.cs" }, status: 0 }],
            workingTreeChanges: [{ uri: { fsPath: DRIVE + "/work/app/b.cs" }, status: 3 }],
        },
        add: record("add"),
        commit: record("commit"),
        pull: record("pull"),
        push: record("push"),
        fetch: record("fetch"),
        createBranch: record("createBranch"),
        deleteBranch: record("deleteBranch"),
        checkout: record("checkout"),
        createStash: record("createStash"),
        popStash: record("popStash"),
        applyStash: record("applyStash"),
    };
}

const { runIdeAction } = await import("./ideActions.js");

describe("git actions", () => {
    beforeEach(() => {
        calls.length = 0;
        repos = [fakeRepo()];
    });

    it("reports the branch and changes of the open repository", async () => {
        const status = (await runIdeAction("gitStatus", {})) as { branch: string; changes: Array<{ path: string; index: string; worktree: string }> };

        expect(status.branch).toBe("main");
        expect(status.changes).toEqual([
            { path: DRIVE + "/work/app/a.cs", index: "A", worktree: " " },
            { path: DRIVE + "/work/app/b.cs", index: " ", worktree: "M" },
        ]);
    });

    it("stages the given paths", async () => {
        await runIdeAction("gitStage", { paths: ["a.cs", "b.cs"] });

        expect(calls).toEqual([["add", [[join(DRIVE + "/work/app", "a.cs"), join(DRIVE + "/work/app", "b.cs")]]]]);
    });

    it("commits with the message", async () => {
        await runIdeAction("gitCommit", { message: "fix" });

        expect(calls).toEqual([["commit", ["fix"]]]);
    });

    it("runs the remote and branch operations", async () => {
        await runIdeAction("gitPull", {});
        await runIdeAction("gitPush", {});
        await runIdeAction("gitFetch", {});
        await runIdeAction("gitCreateBranch", { branch: "feature/x" });
        await runIdeAction("gitDeleteBranch", { branch: "old" });
        await runIdeAction("gitSwitch", { branch: "main" });

        expect(calls.map(([name]) => name)).toEqual(["pull", "push", "fetch", "createBranch", "deleteBranch", "checkout"]);
        expect(calls[3]).toEqual(["createBranch", ["feature/x", true]]);
        expect(calls[4]).toEqual(["deleteBranch", ["old", false]]);
    });

    it("runs the stash operations", async () => {
        await runIdeAction("gitStashPush", { message: "wip" });
        await runIdeAction("gitStashPop", {});
        await runIdeAction("gitStashApply", {});

        expect(calls.map(([name]) => name)).toEqual(["createStash", "popStash", "applyStash"]);
        expect(calls[0]).toEqual(["createStash", ["wip"]]);
    });

    // Windows paths compare case-insensitively, so this only holds on Windows (CI runs Linux too).
    it.skipIf(!DRIVE)("picks the repository whose root matches repoPath", async () => {
        repos = [fakeRepo(DRIVE + "/other"), fakeRepo(DRIVE + "/work/app")];
        await runIdeAction("gitCommit", { message: "m", repoPath: "f:/WORK/app" });

        expect(calls).toEqual([["commit", ["m"]]]);
    });

    it("refuses when no repository is open", async () => {
        repos = [];

        await expect(runIdeAction("gitStatus", {})).rejects.toThrow("No git repository is open in VS Code.");
    });

    it("refuses a missing argument before touching git", async () => {
        await expect(runIdeAction("gitCommit", {})).rejects.toThrow("Missing required argument 'message'.");
        await expect(runIdeAction("gitStage", { paths: [] })).rejects.toThrow("Missing required argument 'paths'.");
        expect(calls).toEqual([]);
    });

    it("stages the approved paths, resolved against the repository named by repoPath", async () => {
        repos = [fakeRepo(DRIVE + "/other"), fakeRepo(DRIVE + "/work/app")];

        await runIdeAction("gitStage", { repoPath: DRIVE + "/work/app", paths: ["src/a.cs", "b.txt"] });

        expect(calls).toEqual([["add", [[join(DRIVE + "/work/app", "src/a.cs"), join(DRIVE + "/work/app", "b.txt")]]]]);
    });

    it("keeps an absolute path as given when staging", async () => {
        repos = [fakeRepo(DRIVE + "/work/app")];
        const absolute = join(DRIVE + "/work/app", "c.txt");

        await runIdeAction("gitStage", { repoPath: DRIVE + "/work/app", paths: [absolute] });

        expect(calls).toEqual([["add", [[absolute]]]]);
    });

    it("commits the approved message exactly, multi-line included", async () => {
        const message = "fix: parse\n\nBody line";

        await runIdeAction("gitCommit", { repoPath: DRIVE + "/work/app", message });

        expect(calls).toEqual([["commit", [message]]]);
    });

    it("runs a routed stash push with no message, as git stash push does", async () => {
        await runIdeAction("gitStashPush", {});

        expect(calls).toEqual([["createStash", [undefined]]]);
    });

    it("routes a switch and a branch delete to the same git operations as the approved command", async () => {
        await runIdeAction("gitSwitch", { branch: "feature/y" });
        await runIdeAction("gitDeleteBranch", { branch: "old" });

        expect(calls).toEqual([["checkout", ["feature/y"]], ["deleteBranch", ["old", false]]]);
    });
});
