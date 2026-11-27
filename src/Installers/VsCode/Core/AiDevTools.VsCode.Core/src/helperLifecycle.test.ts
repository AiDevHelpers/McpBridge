import { describe, expect, test } from "vitest";
import { RoslynHelperLifecycle, type HelperProcess, type HelperSpawner } from "./helperLifecycle.js";

class FakeProcess implements HelperProcess {
    killed = false;
    private exitHandlers: ((code: number | null) => void)[] = [];
    constructor(readonly pid: number) {}
    kill(): void {
        this.killed = true;
    }
    onExit(handler: (code: number | null) => void): void {
        this.exitHandlers.push(handler);
    }
    exit(code: number | null = 1): void {
        for (const handler of this.exitHandlers) {
            handler(code);
        }
    }
}

function fakeSpawner(): HelperSpawner & { spawned: { exe: string; args: string[]; process: FakeProcess }[] } {
    const spawned: { exe: string; args: string[]; process: FakeProcess }[] = [];
    let nextPid = 100;
    return {
        spawned,
        spawn(exe, args) {
            const process = new FakeProcess(nextPid++);
            spawned.push({ exe, args, process });
            return process;
        },
    };
}

const exe = "C:\\Program Files\\AiDevHelpers\\RoslynHelper.exe";
const solution = "F:\\repo\\App.slnx";

describe("Roslyn helper lifecycle", () => {
    test("a start without a solution path is refused and starts nothing", () => {
        const spawner = fakeSpawner();
        const lifecycle = new RoslynHelperLifecycle(exe, spawner);

        expect(() => lifecycle.start(undefined)).toThrow(/solution path is needed/);
        expect(() => lifecycle.start("")).toThrow(/solution path is needed/);
        expect(spawner.spawned).toHaveLength(0);
        expect(lifecycle.running).toBe(false);
    });

    test("a start on solution open spawns the helper with --solution", () => {
        const spawner = fakeSpawner();
        const lifecycle = new RoslynHelperLifecycle(exe, spawner);

        lifecycle.start(solution);

        expect(spawner.spawned).toHaveLength(1);
        expect(spawner.spawned[0].exe).toBe(exe);
        expect(spawner.spawned[0].args).toEqual(["--solution", solution]);
        expect(lifecycle.running).toBe(true);
    });

    test("a stop on solution close kills the helper and does not restart it", () => {
        const spawner = fakeSpawner();
        const lifecycle = new RoslynHelperLifecycle(exe, spawner);
        lifecycle.start(solution);
        const first = spawner.spawned[0].process;

        lifecycle.stop();
        first.exit(0);

        expect(first.killed).toBe(true);
        expect(spawner.spawned).toHaveLength(1);
        expect(lifecycle.running).toBe(false);
    });

    test("the watchdog restarts a helper that exits while its solution is open", () => {
        const spawner = fakeSpawner();
        const lifecycle = new RoslynHelperLifecycle(exe, spawner);
        lifecycle.start(solution);

        spawner.spawned[0].process.exit(1);

        expect(spawner.spawned).toHaveLength(2);
        expect(spawner.spawned[1].args).toEqual(["--solution", solution]);
        expect(lifecycle.running).toBe(true);
    });

    test("the watchdog gives up after its restart budget", () => {
        const spawner = fakeSpawner();
        const lifecycle = new RoslynHelperLifecycle(exe, spawner, { maxRestarts: 2 });
        lifecycle.start(solution);

        for (let i = 0; i < 5; i++) {
            spawner.spawned[spawner.spawned.length - 1].process.exit(1);
        }

        expect(spawner.spawned).toHaveLength(3);
        expect(lifecycle.running).toBe(false);
    });
});
