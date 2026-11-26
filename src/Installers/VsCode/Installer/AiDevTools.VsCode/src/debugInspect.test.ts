import { beforeEach, describe, expect, it, vi } from "vitest";
import { attachedSessions, debuggerMode, shapeDetachAll, shapeProcesses, shapeStackFrames, shapeThreads } from "./debugInspect.js";

// Session listeners captured from the mocked debug API, so a test can start fake debug sessions.
const startListeners: Array<(session: unknown) => void> = [];
const endListeners: Array<(session: unknown) => void> = [];
const stopDebugging = vi.fn(async () => undefined);
const state: { activeDebugSession: unknown; activeStackItem: unknown } = { activeDebugSession: undefined, activeStackItem: undefined };

vi.mock("vscode", () => ({
    window: { createOutputChannel: () => ({ append: () => undefined }) },
    workspace: { workspaceFolders: undefined, getConfiguration: () => ({ get: () => undefined }) },
    debug: {
        get activeDebugSession() {
            return state.activeDebugSession;
        },
        get activeStackItem() {
            return state.activeStackItem;
        },
        onDidStartDebugSession: (listener: (session: unknown) => void) => {
            startListeners.push(listener);
            return { dispose: () => undefined };
        },
        onDidTerminateDebugSession: (listener: (session: unknown) => void) => {
            endListeners.push(listener);
            return { dispose: () => undefined };
        },
        registerDebugAdapterTrackerFactory: () => ({ dispose: () => undefined }),
        stopDebugging,
    },
    commands: { executeCommand: async () => undefined },
    Uri: { file: (p: string) => ({ fsPath: p }) },
}));

const { runIdeAction } = await import("./ideActions.js");
const { registerDebugOutputTracker } = await import("./debugOutputTracker.js");

describe("debugInspect shaping", () => {
    it("maps session and stop state to the Visual Studio debugger mode names", () => {
        expect(debuggerMode(false, false)).toBe("dbgDesignMode");
        expect(debuggerMode(true, false)).toBe("dbgRunMode");
        expect(debuggerMode(true, true)).toBe("dbgBreakMode");
    });

    it("shapes a DAP threads reply in break mode with the current thread", () => {
        const reply = { threads: [{ id: 1, name: "Main" }, { id: 7, name: "Worker" }] };

        expect(shapeThreads("dbgBreakMode", 7, reply)).toEqual({
            debugger_mode: "dbgBreakMode",
            current_thread_id: 7,
            threads: [{ id: 1, name: "Main" }, { id: 7, name: "Worker" }],
        });
    });

    it("returns no threads and a null current thread outside break mode", () => {
        expect(shapeThreads("dbgRunMode", 7, { threads: [{ id: 1, name: "Main" }] })).toEqual({
            debugger_mode: "dbgRunMode",
            current_thread_id: null,
            threads: [],
        });
    });

    it("shapes a DAP stackTrace reply into indexed frames with the function name", () => {
        const reply = { stackFrames: [{ id: 100, name: "Program.Main", line: 12 }, { id: 101, name: "Helper.Run", line: 40 }] };

        expect(shapeStackFrames("dbgBreakMode", reply)).toEqual({
            debugger_mode: "dbgBreakMode",
            frames: [
                { index: 0, function: "Program.Main", return_type: null },
                { index: 1, function: "Helper.Run", return_type: null },
            ],
        });
    });

    it("returns no frames outside break mode", () => {
        expect(shapeStackFrames("dbgDesignMode", undefined)).toEqual({ debugger_mode: "dbgDesignMode", frames: [] });
    });

    it("lists sessions as processes, with a null pid when the configuration has none", () => {
        const sessions = [
            { name: "app", request: "launch", processId: 4321 },
            { name: "svc", request: "attach" },
        ];

        expect(shapeProcesses(sessions)).toEqual({
            processes: [
                { pid: 4321, name: "app" },
                { pid: null, name: "svc" },
            ],
        });
    });

    it("selects only attached sessions for detach", () => {
        const sessions = [
            { name: "app", request: "launch", processId: 4321 },
            { name: "svc", request: "attach", processId: 999 },
        ];

        expect(attachedSessions(sessions)).toEqual([{ name: "svc", request: "attach", processId: 999 }]);
    });

    it("plans a detach without confirm and lists the processes that would be detached", () => {
        const attached = [{ name: "svc", request: "attach", processId: 999 }];

        expect(shapeDetachAll(false, attached)).toEqual({ planned: true, would: { detached: [{ pid: 999, name: "svc" }] } });
    });

    it("reports the applied detach with confirm", () => {
        const attached = [{ name: "svc", request: "attach", processId: 999 }];

        expect(shapeDetachAll(true, attached)).toEqual({ planned: false, applied: { detached: [{ pid: 999, name: "svc" }] } });
    });
});

describe("debugInspect actions", () => {
    beforeEach(() => {
        state.activeDebugSession = undefined;
        state.activeStackItem = undefined;
        stopDebugging.mockClear();
    });

    it("listThreads returns an empty list when the debugger is not in break mode", async () => {
        const customRequest = vi.fn(async () => ({ threads: [{ id: 1, name: "Main" }] }));
        state.activeDebugSession = { name: "app", configuration: { request: "launch" }, customRequest };

        const reply = await runIdeAction("listThreads", {});

        expect(reply).toEqual({ debugger_mode: "dbgRunMode", current_thread_id: null, threads: [] });
        expect(customRequest).not.toHaveBeenCalled();
    });

    it("detachAll without confirm changes nothing", async () => {
        registerDebugOutputTracker({ subscriptions: [] } as never);
        const svc = { id: "s1", name: "svc", configuration: { request: "attach", processId: 999 } };
        startListeners.forEach((listener) => listener(svc));

        const reply = await runIdeAction("detachAll", {});

        expect(reply).toEqual({ planned: true, would: { detached: [{ pid: 999, name: "svc" }] } });
        expect(stopDebugging).not.toHaveBeenCalled();
    });
});
