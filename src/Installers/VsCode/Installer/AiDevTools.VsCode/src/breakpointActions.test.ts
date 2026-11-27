import { beforeEach, describe, expect, it, vi } from "vitest";

// A stand-in for the VS Code breakpoint API: a list that add and remove change, so each test sees the effect.
const state = {
    breakpoints: [] as Array<{ location: { uri: { fsPath: string }; range: { start: { line: number } } }; enabled: boolean; condition?: string; hitCondition?: string; logMessage?: string }>,
};
const addBreakpoints = vi.fn((items: Array<{ enabled: boolean }>) => {
    state.breakpoints.push(...(items as typeof state.breakpoints));
});
const removeBreakpoints = vi.fn((items: unknown[]) => {
    state.breakpoints = state.breakpoints.filter((b) => !items.includes(b));
});

vi.mock("vscode", () => {
    class SourceBreakpoint {
        constructor(
            public location: unknown,
            public enabled = true,
            public condition?: string,
            public hitCondition?: string,
            public logMessage?: string,
        ) {}
    }
    class Location {
        constructor(public uri: unknown, public range: unknown) {}
    }
    return {
        window: { createOutputChannel: () => ({ append: () => undefined }) },
        workspace: { workspaceFolders: undefined, getConfiguration: () => ({ get: () => undefined }) },
        debug: {
            onDidStartDebugSession: () => ({ dispose: () => undefined }),
            onDidTerminateDebugSession: () => ({ dispose: () => undefined }),
            activeDebugSession: undefined,
            get breakpoints() { return state.breakpoints; },
            addBreakpoints,
            removeBreakpoints,
        },
        commands: { executeCommand: async () => undefined },
        Uri: { file: (p: string) => ({ fsPath: p }) },
        SourceBreakpoint,
        Location,
    };
});

const { runIdeAction } = await import("./ideActions.js");
const { hitConditionFor } = await import("./breakpointActions.js");
const vscodeMock = await import("vscode");

// Windows drive paths are only absolute on Windows; on other hosts (CI runs Linux) use a rooted path.
const DRIVE = process.platform === "win32" ? "F:" : "";
const FILE = DRIVE + "/work/app/Program.cs";

// Breakpoints are added as real SourceBreakpoint instances, because the code under test filters with instanceof.
function addBreakpointAt(line: number, extra: { enabled?: boolean; condition?: string; hitCondition?: string } = {}) {
    const location = new vscodeMock.Location({ fsPath: FILE }, { start: { line: line - 1 } } as never);
    state.breakpoints.push(new vscodeMock.SourceBreakpoint(location, extra.enabled ?? true, extra.condition, extra.hitCondition) as never);
}

describe("breakpoint actions", () => {
    beforeEach(() => {
        state.breakpoints = [];
        addBreakpoints.mockClear();
        removeBreakpoints.mockClear();
    });

    it("maps each hit count type to a VS Code hitCondition", () => {
        expect(hitConditionFor("always", 4)).toBeUndefined();
        expect(hitConditionFor("equal", 3)).toBe("== 3");
        expect(hitConditionFor("multiple", 5)).toBe("% 5");
        expect(hitConditionFor("greaterOrEqual", 2)).toBe(">= 2");
        expect(hitConditionFor(undefined, 2)).toBeUndefined();
    });

    it("throws when no breakpoint is at the file and line", async () => {
        await expect(runIdeAction("enableBreakpoint", { file: FILE, line: 7 })).rejects.toThrow(`There is no breakpoint at ${FILE}:7.`);
        await expect(runIdeAction("disableBreakpoint", { file: FILE, line: 7 })).rejects.toThrow(`There is no breakpoint at ${FILE}:7.`);
    });

    it("reports the planned change without confirm and changes nothing", async () => {
        addBreakpointAt(3);

        const reply = await runIdeAction("disableBreakpoint", { file: FILE, line: 3 });

        expect(reply).toEqual({ planned: true, would: [{ file: FILE, line: 3, enabled: false }] });
        expect(state.breakpoints[0].enabled).toBe(true);
        expect(addBreakpoints).not.toHaveBeenCalled();
        expect(removeBreakpoints).not.toHaveBeenCalled();
    });

    it("applies the change with confirm by replacing the breakpoint", async () => {
        addBreakpointAt(3, { condition: "x > 1", hitCondition: "== 2" });

        const reply = await runIdeAction("disableBreakpoint", { file: FILE, line: 3, confirm: true });

        expect(reply).toEqual({ planned: false, applied: [{ file: FILE, line: 3, enabled: false }] });
        expect(state.breakpoints).toHaveLength(1);
        expect(state.breakpoints[0].enabled).toBe(false);
        expect(state.breakpoints[0].condition).toBe("x > 1");
        expect(state.breakpoints[0].hitCondition).toBe("== 2");
    });

    it("throws when setBreakpointCondition gets none of condition, hitCountType or hitCount", async () => {
        addBreakpointAt(3);

        await expect(runIdeAction("setBreakpointCondition", { file: FILE, line: 3, confirm: true })).rejects.toThrow("needs condition, hitCountType or hitCount");
        expect(removeBreakpoints).not.toHaveBeenCalled();
    });

    it("rejects an unknown hitCountType", async () => {
        addBreakpointAt(3);

        await expect(runIdeAction("setBreakpointCondition", { file: FILE, line: 3, hitCountType: "sometimes", hitCount: 2 })).rejects.toThrow();
    });

    it("plans a condition change without confirm, showing current and proposed values", async () => {
        addBreakpointAt(3, { condition: "a == 1", hitCondition: "% 4" });

        const reply = await runIdeAction("setBreakpointCondition", { file: FILE, line: 3, condition: "a == 2" });

        expect(reply).toEqual({
            planned: true,
            would: [{
                file: FILE,
                line: 3,
                condition: { current: "a == 1", proposed: "a == 2" },
                hit_count_type: { current: "multiple", proposed: "multiple" },
                hit_count: { current: 4, proposed: 4 },
            }],
        });
        expect(addBreakpoints).not.toHaveBeenCalled();
    });

    it("applies a hit count change with confirm, keeping the condition", async () => {
        addBreakpointAt(3, { condition: "a == 1" });

        const reply = await runIdeAction("setBreakpointCondition", { file: FILE, line: 3, hitCountType: "greaterOrEqual", hitCount: 2, confirm: true });

        expect(reply).toEqual({
            planned: false,
            applied: [{
                file: FILE,
                line: 3,
                condition: { current: "a == 1", proposed: "a == 1" },
                hit_count_type: { current: "always", proposed: "greaterOrEqual" },
                hit_count: { current: null, proposed: 2 },
            }],
        });
        expect(state.breakpoints).toHaveLength(1);
        expect(state.breakpoints[0].condition).toBe("a == 1");
        expect(state.breakpoints[0].hitCondition).toBe(">= 2");
    });
});
