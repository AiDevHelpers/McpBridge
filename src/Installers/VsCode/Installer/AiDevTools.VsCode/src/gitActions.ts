import * as vscode from "vscode";
import { isAbsolute, join } from "node:path";
import { t } from "./strings.js";

// Source control through VS Code's built-in git extension (its exports API, version 1). The MCP side approves every
// write first (git_plan / git_confirm), so these actions only run what was already approved.

type Args = Record<string, unknown>;

// The parts of the git extension's API this file uses. Declared here so the extension compiles without its typings.
interface GitChange { uri: { fsPath: string }; status: number }
interface GitRepository {
    rootUri: { fsPath: string };
    state: { HEAD?: { name?: string; ahead?: number; behind?: number }; workingTreeChanges: GitChange[]; indexChanges: GitChange[] };
    add(paths: string[]): Promise<void>;
    commit(message: string): Promise<void>;
    pull(): Promise<void>;
    push(): Promise<void>;
    fetch(): Promise<void>;
    createBranch(name: string, checkout: boolean): Promise<void>;
    deleteBranch(name: string, force?: boolean): Promise<void>;
    checkout(treeish: string): Promise<void>;
    createStash(message?: string): Promise<void>;
    popStash(index?: number): Promise<void>;
    applyStash(index?: number): Promise<void>;
}
interface GitApi { repositories: GitRepository[] }

export const GIT_ACTIONS = [
    "gitStatus", "gitStage", "gitCommit", "gitPull", "gitPush", "gitFetch",
    "gitCreateBranch", "gitDeleteBranch", "gitSwitch", "gitStashPush", "gitStashPop", "gitStashApply",
] as const;
export type GitAction = (typeof GIT_ACTIONS)[number];

export function isGitAction(action: string): action is GitAction {
    return (GIT_ACTIONS as readonly string[]).includes(action);
}

function requireString(args: Args, key: string): string {
    const value = args[key];
    if (typeof value !== "string" || value.length === 0) {
        throw new Error(t("missingArgument", key));
    }
    return value;
}

function requireStrings(args: Args, key: string): string[] {
    const value = args[key];
    if (!Array.isArray(value) || value.length === 0 || !value.every((v) => typeof v === "string" && v.length > 0)) {
        throw new Error(t("missingArgument", key));
    }
    return value as string[];
}

function repository(repoPath: string | undefined): GitRepository {
    const exports = vscode.extensions.getExtension<{ getAPI(version: number): GitApi }>("vscode.git")?.exports;
    const api = exports?.getAPI(1);
    if (!api) {
        throw new Error(t("gitUnavailable"));
    }
    const match = repoPath === undefined
        ? api.repositories[0]
        : api.repositories.find((r) => r.rootUri.fsPath.toLowerCase() === repoPath.toLowerCase());
    if (!match) {
        throw new Error(t("noGitRepository"));
    }
    return match;
}

const STATUS_LETTERS: Record<number, string> = { 0: "A", 1: "A", 2: "D", 3: "M", 5: "M", 6: "R", 7: "?" };
const changeLetter = (change: GitChange): string => STATUS_LETTERS[change.status] ?? "M";

/** Runs one git action in the open VS Code repository (or the one whose root is repoPath). */
export async function runGitAction(action: GitAction, args: Args): Promise<unknown> {
    const repoPath = typeof args.repoPath === "string" ? args.repoPath : undefined;
    const repo = repository(repoPath);
    switch (action) {
        case "gitStatus":
            return {
                branch: repo.state.HEAD?.name ?? null,
                ahead: repo.state.HEAD?.ahead ?? 0,
                behind: repo.state.HEAD?.behind ?? 0,
                changes: [
                    ...repo.state.indexChanges.map((c) => ({ path: c.uri.fsPath, index: changeLetter(c), worktree: " " })),
                    ...repo.state.workingTreeChanges.map((c) => ({ path: c.uri.fsPath, index: " ", worktree: changeLetter(c) })),
                ],
            };
        case "gitStage":
            // The approved paths are relative to the repository. The git API resolves relative paths from the
            // process folder, so they are joined to the repository root first.
            await repo.add(requireStrings(args, "paths").map((p) => (isAbsolute(p) ? p : join(repo.rootUri.fsPath, p))));
            return { staged: true };
        case "gitCommit":
            await repo.commit(requireString(args, "message"));
            return { committed: true };
        case "gitPull":
            await repo.pull();
            return { pulled: true };
        case "gitPush":
            await repo.push();
            return { pushed: true };
        case "gitFetch":
            await repo.fetch();
            return { fetched: true };
        case "gitCreateBranch":
            await repo.createBranch(requireString(args, "branch"), true);
            return { branch: args.branch, created: true };
        case "gitDeleteBranch":
            await repo.deleteBranch(requireString(args, "branch"), false);
            return { branch: args.branch, deleted: true };
        case "gitSwitch":
            await repo.checkout(requireString(args, "branch"));
            return { branch: args.branch, switched: true };
        case "gitStashPush":
            await repo.createStash(typeof args.message === "string" ? args.message : undefined);
            return { stashed: true };
        case "gitStashPop":
            await repo.popStash();
            return { popped: true };
        case "gitStashApply":
            await repo.applyStash();
            return { applied: true };
    }
}
