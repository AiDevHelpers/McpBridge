import { execFile } from "child_process";
import * as path from "path";
import type { DebugConfiguration } from "vscode";
import { t } from "./strings.js";

// Local processes for listProcesses and attachProcess. VS Code's API has no process picker, so the list comes from
// tasklist (Windows) or ps (other platforms). The parsers are pure so they can be tested without a process table.

export interface LocalProcess {
    pid: number;
    name: string;
}

/** Parses `tasklist /FO CSV /NH` output: "Image Name","PID",... One process per line. Other lines are skipped. */
export function parseTasklistCsv(text: string): LocalProcess[] {
    const processes: LocalProcess[] = [];
    for (const line of text.split(/\r?\n/)) {
        const match = /^"([^"]+)","(\d+)"/.exec(line);
        if (match) {
            processes.push({ pid: Number(match[2]), name: match[1] });
        }
    }
    return processes;
}

/** Parses `ps -axo pid=,comm=` output. The command is reduced to its file name, as the Visual Studio list shows it. */
export function parsePsOutput(text: string): LocalProcess[] {
    const processes: LocalProcess[] = [];
    for (const line of text.split(/\r?\n/)) {
        const match = /^\s*(\d+)\s+(.+?)\s*$/.exec(line);
        if (match) {
            processes.push({ pid: Number(match[1]), name: path.posix.basename(match[2]) });
        }
    }
    return processes;
}

/** Runs the platform's process listing and returns the parsed processes. Read-only. */
export function listLocalProcesses(): Promise<LocalProcess[]> {
    const isWindows = process.platform === "win32";
    const command = isWindows ? "tasklist" : "ps";
    const args = isWindows ? ["/FO", "CSV", "/NH"] : ["-axo", "pid=,comm="];
    return new Promise((resolve, reject) => {
        execFile(command, args, { windowsHide: true, maxBuffer: 64 * 1024 * 1024 }, (err, stdout) => {
            if (err) {
                reject(err);
                return;
            }
            resolve(isWindows ? parseTasklistCsv(String(stdout)) : parsePsOutput(String(stdout)));
        });
    });
}

/** The process with this pid, or an Error when it is not in the list (attachProcess refuses it). */
export function findLocalProcess(processes: LocalProcess[], pid: number): LocalProcess {
    const target = processes.find((p) => p.pid === pid);
    if (target === undefined) {
        throw new Error(t("noLocalProcess", pid));
    }
    return target;
}

/**
 * The debug configuration that attaches to a process. "coreclr" is the .NET debug type of the C# extension (and C# Dev Kit),
 * which attaches by processId.
 */
export function attachConfiguration(pid: number, name: string): DebugConfiguration {
    return { type: "coreclr", request: "attach", name: `Attach to ${name} (${pid})`, processId: pid };
}
