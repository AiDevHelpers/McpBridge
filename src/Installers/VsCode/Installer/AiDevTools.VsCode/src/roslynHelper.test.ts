import * as path from "path";
import { describe, expect, it } from "vitest";
import type { HelperProcess, HelperSpawner } from "./vendor/core/index.js";
import { helperExecutablePath, startRoslynHelper } from "./roslynHelper.js";

class FakeProcess implements HelperProcess {
    killed = false;
    constructor(readonly pid: number) {}
    kill(): void {
        this.killed = true;
    }
    onExit(): void {
        // Not needed for these tests.
    }
}

function fakeSpawner(): HelperSpawner & { spawned: { exe: string; args: string[]; process: FakeProcess }[] } {
    const spawned: { exe: string; args: string[]; process: FakeProcess }[] = [];
    return {
        spawned,
        spawn(exe, args) {
            const process = new FakeProcess(900 + spawned.length);
            spawned.push({ exe, args, process });
            return process;
        },
    };
}

const extensionPath = path.join("C:", "Users", "dev", ".vscode", "extensions", "aidevtools");
const folder = path.join("F:", "repo");
const solution = path.join(folder, "App.slnx");

describe("Roslyn helper wiring in the VS Code extension", () => {
    it("a workspace with a solution starts the bundled helper with --solution", () => {
        const spawner = fakeSpawner();

        const wiring = startRoslynHelper({ extensionPath, workspaceFolder: folder, spawner, findSolution: () => solution });

        expect(wiring.started).toBe(true);
        expect(spawner.spawned).toHaveLength(1);
        expect(spawner.spawned[0].exe).toBe(helperExecutablePath(extensionPath));
        expect(spawner.spawned[0].args).toEqual(["--solution", solution]);
    });

    it("the bundled helper sits in dist/roslyn beside the extension", () => {
        expect(helperExecutablePath(extensionPath)).toBe(path.join(extensionPath, "dist", "roslyn", "RoslynHelper.exe"));
    });

    it("a workspace with no solution file starts nothing and says why", () => {
        const spawner = fakeSpawner();

        const wiring = startRoslynHelper({ extensionPath, workspaceFolder: folder, spawner, findSolution: () => undefined });

        expect(wiring.started).toBe(false);
        expect(wiring.reason).toMatch(/No \.slnx or \.sln file/);
        expect(spawner.spawned).toHaveLength(0);
    });

    it("no workspace folder starts nothing and says why", () => {
        const spawner = fakeSpawner();

        const wiring = startRoslynHelper({ extensionPath, workspaceFolder: undefined, spawner, findSolution: () => solution });

        expect(wiring.started).toBe(false);
        expect(wiring.reason).toMatch(/No workspace folder/);
        expect(spawner.spawned).toHaveLength(0);
    });

    it("stop kills the helper it started", () => {
        const spawner = fakeSpawner();
        const wiring = startRoslynHelper({ extensionPath, workspaceFolder: folder, spawner, findSolution: () => solution });

        wiring.stop();

        expect(spawner.spawned[0].process.killed).toBe(true);
    });
});
