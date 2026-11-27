import { spawn } from "child_process";
import * as fs from "fs";
import * as path from "path";
import { RoslynHelperLifecycle, type HelperProcess, type HelperSpawner } from "./vendor/core/index.js";

// Starts the Roslyn helper for the workspace's solution file when this window opens, and stops it when the window closes.
// The helper ships beside the extension in dist/roslyn (copied at build time).

export interface RoslynWiringOptions {
    extensionPath: string;
    workspaceFolder: string | undefined;
    spawner: HelperSpawner;
    findSolution: (folder: string) => string | undefined;
}

export interface RoslynWiring {
    started: boolean;
    /** Why no helper started, when started is false. */
    reason?: string;
    stop(): void;
}

export function helperExecutablePath(extensionPath: string): string {
    return path.join(extensionPath, "dist", "roslyn", "RoslynHelper.exe");
}

/** The solution file at the top of the folder: .slnx first, then .sln. Undefined when there is none. */
export function findSolutionFile(folder: string): string | undefined {
    const names = fs.readdirSync(folder);
    const slnx = names.find((name) => name.toLowerCase().endsWith(".slnx"));
    const sln = names.find((name) => name.toLowerCase().endsWith(".sln"));
    const found = slnx ?? sln;
    return found === undefined ? undefined : path.join(folder, found);
}

export function startRoslynHelper(options: RoslynWiringOptions): RoslynWiring {
    const folder = options.workspaceFolder;
    if (folder === undefined || folder === "") {
        return { started: false, reason: "No workspace folder is open, so no Roslyn helper starts.", stop: () => undefined };
    }

    const solution = options.findSolution(folder);
    if (solution === undefined) {
        return {
            started: false,
            reason: "No .slnx or .sln file in the workspace folder, so no Roslyn helper starts.",
            stop: () => undefined,
        };
    }

    const lifecycle = new RoslynHelperLifecycle(helperExecutablePath(options.extensionPath), options.spawner);
    lifecycle.start(solution);
    return { started: true, stop: () => lifecycle.stop() };
}

/** The real spawner: a child process with no console window and no attached stdio. */
export const childProcessSpawner: HelperSpawner = {
    spawn(exe: string, args: string[]): HelperProcess {
        const child = spawn(exe, args, { windowsHide: true, stdio: "ignore" });
        return {
            pid: child.pid ?? -1,
            kill: () => {
                child.kill();
            },
            onExit: (handler) => {
                child.on("exit", (code) => handler(code));
            },
        };
    },
};
