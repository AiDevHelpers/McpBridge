import * as path from "path";
import { describe, expect, it } from "vitest";
import type { HelperProcess, HelperSpawner } from "./vendor/core/index.js";
import { classifyProject, helperExecutablePath, startRoslynHelper } from "./roslynHelper.js";

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

    it("a folder with a net48 project also starts the net48 helper from dist/roslyn48", () => {
        const spawner = fakeSpawner();

        const wiring = startRoslynHelper({ extensionPath, workspaceFolder: folder, spawner, findSolution: () => solution, needsNet48: () => true });

        expect(wiring.started).toBe(true);
        expect(wiring.net48Started).toBe(true);
        expect(spawner.spawned).toHaveLength(2);
        expect(spawner.spawned[1].exe).toBe(helperExecutablePath(extensionPath, "net48"));
        expect(spawner.spawned[1].exe).toBe(path.join(extensionPath, "dist", "roslyn48", "RoslynHelper48.exe"));
        expect(spawner.spawned[1].args).toEqual(["--solution", solution]);
    });

    it("an SDK-only folder starts only the net10 helper", () => {
        const spawner = fakeSpawner();

        const wiring = startRoslynHelper({ extensionPath, workspaceFolder: folder, spawner, findSolution: () => solution, needsNet48: () => false });

        expect(wiring.net48Started).toBe(false);
        expect(spawner.spawned).toHaveLength(1);
    });

    it("classifies project files: SDK net10, SDK net48 and legacy", () => {
        expect(classifyProject("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>")).toBe("net10");
        expect(classifyProject("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net48</TargetFramework></PropertyGroup></Project>")).toBe("net48");
        expect(classifyProject("<Project ToolsVersion=\"15.0\"><PropertyGroup><TargetFrameworkVersion>v4.8</TargetFrameworkVersion></PropertyGroup></Project>")).toBe("net48");
    });

    it("stop kills the helper it started", () => {
        const spawner = fakeSpawner();
        const wiring = startRoslynHelper({ extensionPath, workspaceFolder: folder, spawner, findSolution: () => solution });

        wiring.stop();

        expect(spawner.spawned[0].process.killed).toBe(true);
    });
});
