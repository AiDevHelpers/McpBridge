import { beforeEach, describe, expect, it, vi } from "vitest";
import { parsePsOutput, parseTasklistCsv } from "./processList.js";
import { recordDapMessage, forgetSession } from "./exceptionFilters.js";

// The local process list comes from execFile (tasklist on Windows, ps elsewhere). The fake returns a fixed listing.
const fake = vi.hoisted(() => ({ stdout: "", commands: [] as string[] }));

vi.mock("child_process", () => ({
    spawn: () => {
        throw new Error("spawn is not used by these tests");
    },
    execFile: (command: string, _args: string[], _options: unknown, callback: (err: Error | null, stdout: string, stderr: string) => void) => {
        fake.commands.push(command);
        callback(null, fake.stdout, "");
    },
}));

// A stand-in for the VS Code debug API: the active session, start and request calls are recorded.
const state = { activeDebugSession: undefined as unknown };
const startDebugging = vi.fn(async () => true);

vi.mock("vscode", () => ({
    window: { createOutputChannel: () => ({ append: () => undefined }) },
    workspace: { workspaceFolders: undefined, getConfiguration: () => ({ get: () => undefined }) },
    debug: {
        get activeDebugSession() {
            return state.activeDebugSession;
        },
        onDidStartDebugSession: () => ({ dispose: () => undefined }),
        onDidTerminateDebugSession: () => ({ dispose: () => undefined }),
        registerDebugAdapterTrackerFactory: () => ({ dispose: () => undefined }),
        startDebugging,
    },
    commands: { executeCommand: async () => undefined },
    Uri: { file: (p: string) => ({ fsPath: p }) },
}));

const { runIdeAction } = await import("./ideActions.js");

const TASKLIST = [
    '"devenv.exe","4120","Console","1","512,000 K"',
    '"MyApp.exe","9001","Console","1","80,000 K"',
    "",
    "INFO: No tasks are running which match the specified criteria.",
].join("\r\n");

// The `ps` listing for the same two processes, used where the code lists processes with ps (not Windows).
const PS_LISTING = "  4120 /usr/bin/devenv.exe\n  9001 /usr/bin/MyApp.exe\n";

// A session as the debugger reports it, with the DAP exception filters the initialize reply carried.
function fakeSession(id = "s1") {
    return {
        id,
        name: "Attach MyApp",
        configuration: { request: "attach", processId: 9001 },
        customRequest: vi.fn(async () => ({})),
    };
}

const INITIALIZE_REPLY = {
    type: "response",
    command: "initialize",
    success: true,
    body: {
        exceptionBreakpointFilters: [
            { filter: "raised", label: "Raised exceptions", default: false },
            { filter: "uncaught", label: "Uncaught exceptions", default: true },
        ],
    },
};

describe("process list shaping", () => {
    it("reads the image name and pid from tasklist CSV and skips other lines", () => {
        expect(parseTasklistCsv(TASKLIST)).toEqual([
            { pid: 4120, name: "devenv.exe" },
            { pid: 9001, name: "MyApp.exe" },
        ]);
    });

    it("reads the pid and command name from ps output and keeps only the file name", () => {
        const ps = "    1 /sbin/init\n  812 /usr/lib/Google Chrome Helper\n\n";
        expect(parsePsOutput(ps)).toEqual([
            { pid: 1, name: "init" },
            { pid: 812, name: "Google Chrome Helper" },
        ]);
    });
});

describe("processActions", () => {
    beforeEach(() => {
        fake.stdout = process.platform === "win32" ? TASKLIST : PS_LISTING;
        fake.commands.length = 0;
        state.activeDebugSession = undefined;
        startDebugging.mockClear();
        forgetSession("s1");
    });

    it("listProcesses returns pid and name for each local process", async () => {
        const reply = await runIdeAction("listProcesses", {});

        expect(reply).toEqual({
            processes: [
                { pid: 4120, name: "devenv.exe" },
                { pid: 9001, name: "MyApp.exe" },
            ],
        });
    });

    it("attachProcess without confirm reports the target and attaches nothing", async () => {
        const reply = await runIdeAction("attachProcess", { pid: 9001 });

        expect(reply).toEqual({ planned: true, would: { pid: 9001, name: "MyApp.exe" } });
        expect(startDebugging).not.toHaveBeenCalled();
    });

    it("attachProcess refuses a pid that is not in the process list", async () => {
        await expect(runIdeAction("attachProcess", { pid: 999, confirm: true })).rejects.toThrow(
            "No local process with pid 999 to attach to.",
        );
        expect(startDebugging).not.toHaveBeenCalled();
    });

    it("attachProcess with confirm starts a coreclr attach to the pid", async () => {
        const reply = await runIdeAction("attachProcess", { pid: 9001, confirm: true });

        expect(reply).toEqual({ planned: false, applied: { pid: 9001, name: "MyApp.exe" } });
        expect(startDebugging).toHaveBeenCalledTimes(1);
        const config = (startDebugging.mock.calls[0] as unknown[])[1] as Record<string, unknown>;
        expect(config).toMatchObject({ type: "coreclr", request: "attach", processId: 9001 });
    });

    it("listExceptions with no active debug session returns an empty list and says so", async () => {
        const reply = await runIdeAction("listExceptions", {});

        expect(reply).toEqual({ exceptions: [], note: "No active debug session." });
    });

    it("listExceptions shapes the DAP filters of the active session", async () => {
        const session = fakeSession();
        state.activeDebugSession = session;
        recordDapMessage("s1", INITIALIZE_REPLY);

        const reply = (await runIdeAction("listExceptions", {})) as { exceptions: Array<Record<string, unknown>> };

        expect(reply.exceptions).toEqual([
            expect.objectContaining({ group: "Debug adapter", name: "raised", break_when_thrown: false, break_when_user_unhandled: null }),
            expect.objectContaining({ group: "Debug adapter", name: "uncaught", break_when_thrown: true }),
        ]);
        expect(reply.exceptions.length).toBe(2);
    });

    it("setExceptionBreak without confirm reports the change and sends nothing", async () => {
        const session = fakeSession();
        state.activeDebugSession = session;
        recordDapMessage("s1", INITIALIZE_REPLY);

        const reply = await runIdeAction("setExceptionBreak", { name: "raised", breakWhenThrown: true });

        expect(reply).toEqual({
            planned: true,
            would: {
                group: "Debug adapter",
                name: "raised",
                break_when_thrown: { current: false, proposed: true },
                break_when_user_unhandled: { current: null, proposed: null },
            },
            partial: true,
            note: expect.any(String),
        });
        expect(session.customRequest).not.toHaveBeenCalled();
    });

    it("setExceptionBreak with confirm sends the enabled filter set and reports partial", async () => {
        const session = fakeSession();
        state.activeDebugSession = session;
        recordDapMessage("s1", INITIALIZE_REPLY);

        const reply = (await runIdeAction("setExceptionBreak", { name: "raised", breakWhenThrown: true, confirm: true })) as Record<string, unknown>;

        expect(session.customRequest).toHaveBeenCalledWith("setExceptionBreakpoints", { filters: ["raised", "uncaught"] });
        expect(reply.planned).toBe(false);
        expect(reply.partial).toBe(true);
        expect(reply.applied).toMatchObject({ name: "raised", break_when_thrown: { current: false, proposed: true } });
    });

    it("setExceptionBreak refuses a call with no flag", async () => {
        state.activeDebugSession = fakeSession();
        recordDapMessage("s1", INITIALIZE_REPLY);

        await expect(runIdeAction("setExceptionBreak", { name: "raised", confirm: true })).rejects.toThrow(
            "setExceptionBreak needs breakWhenThrown or breakWhenUserUnhandled.",
        );
    });
});
