import { spawn } from "child_process";
import * as fs from "fs";
import * as path from "path";
import { RoslynHelperLifecycle, type HelperProcess, type HelperSpawner } from "./vendor/core/index.js";

// Starts the Roslyn helpers for the workspace's solution when this window opens, and stops them when the window closes.
// The net10 helper always starts for a solution; the net48 helper starts too when the folder has a net48 or legacy project.
// Both ship beside the extension (dist/roslyn and dist/roslyn48, copied at build time).

export type HelperFramework = "net10" | "net48";

export interface RoslynWiringOptions {
    extensionPath: string;
    workspaceFolder: string | undefined;
    spawner: HelperSpawner;
    findSolution: (folder: string) => string | undefined;
    /** Whether the folder holds a net48 or legacy project. Defaults to a scan of the folder's project files. */
    needsNet48?: (folder: string) => boolean;
}

export interface RoslynWiring {
    started: boolean;
    /** Whether the net48 helper started too. */
    net48Started: boolean;
    /** Why no helper started, when started is false. */
    reason?: string;
    stop(): void;
}

export function helperExecutablePath(extensionPath: string, framework: HelperFramework = "net10"): string {
    return framework === "net48"
        ? path.join(extensionPath, "dist", "roslyn48", "RoslynHelper48.exe")
        : path.join(extensionPath, "dist", "roslyn", "RoslynHelper.exe");
}

/** The framework of a project file: net48 for a legacy project or one that targets net48, else net10. */
export function classifyProject(projectText: string): HelperFramework {
    const legacy = !/Sdk=/i.test(projectText) || /<TargetFrameworkVersion>/i.test(projectText);
    const net48 = /<TargetFramework>net48<\/TargetFramework>/i.test(projectText);
    return legacy || net48 ? "net48" : "net10";
}

/** True when a project file under the folder is net48 or legacy. Build output and packages are not scanned. */
export function folderNeedsNet48(folder: string): boolean {
    const skip = new Set(["bin", "obj", "node_modules", ".git"]);
    const stack = [folder];
    while (stack.length > 0) {
        const dir = stack.pop()!;
        let entries: fs.Dirent[];
        try {
            entries = fs.readdirSync(dir, { withFileTypes: true });
        } catch {
            continue;
        }

        for (const entry of entries) {
            if (entry.isDirectory() && !skip.has(entry.name)) {
                stack.push(path.join(dir, entry.name));
            } else if (entry.isFile() && entry.name.toLowerCase().endsWith(".csproj")) {
                if (classifyProject(fs.readFileSync(path.join(dir, entry.name), "utf8")) === "net48") {
                    return true;
                }
            }
        }
    }

    return false;
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
        return { started: false, net48Started: false, reason: "No workspace folder is open, so no Roslyn helper starts.", stop: () => undefined };
    }

    const solution = options.findSolution(folder);
    if (solution === undefined) {
        return {
            started: false,
            net48Started: false,
            reason: "No .slnx or .sln file in the workspace folder, so no Roslyn helper starts.",
            stop: () => undefined,
        };
    }

    const lifecycles = [new RoslynHelperLifecycle(helperExecutablePath(options.extensionPath, "net10"), options.spawner)];
    lifecycles[0].start(solution);

    const needsNet48 = (options.needsNet48 ?? folderNeedsNet48)(folder);
    if (needsNet48) {
        const net48 = new RoslynHelperLifecycle(helperExecutablePath(options.extensionPath, "net48"), options.spawner);
        net48.start(solution);
        lifecycles.push(net48);
    }

    return {
        started: true,
        net48Started: needsNet48,
        stop: () => {
            for (const lifecycle of lifecycles) {
                lifecycle.stop();
            }
        },
    };
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
