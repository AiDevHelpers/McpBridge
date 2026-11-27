/** A running helper process, as the lifecycle sees it. */
export interface HelperProcess {
    readonly pid: number;
    kill(): void;
    onExit(handler: (code: number | null) => void): void;
}

/** Starts a helper executable with arguments. The extension passes the real child-process spawner; tests pass a fake. */
export interface HelperSpawner {
    spawn(exe: string, args: string[]): HelperProcess;
}

export interface LifecycleOptions {
    /** How many times the watchdog restarts a helper that exits while its solution is open. */
    maxRestarts?: number;
}

/**
 * Runs one Roslyn helper per open solution: starts it on solution open, stops it on close, and restarts it when it exits on its
 * own (up to maxRestarts). A start without a solution path is refused.
 */
export class RoslynHelperLifecycle {
    private readonly maxRestarts: number;
    private solutionPath: string | undefined;
    private current: HelperProcess | undefined;
    private restarts = 0;

    constructor(
        private readonly exePath: string,
        private readonly spawner: HelperSpawner,
        options: LifecycleOptions = {},
    ) {
        this.maxRestarts = options.maxRestarts ?? 3;
    }

    get running(): boolean {
        return this.current !== undefined;
    }

    start(solutionPath: string | undefined): void {
        if (solutionPath === undefined || solutionPath.trim() === "") {
            throw new Error("A solution path is needed to start the Roslyn helper.");
        }

        this.solutionPath = solutionPath;
        this.restarts = 0;
        this.launch();
    }

    stop(): void {
        const process = this.current;
        this.solutionPath = undefined;
        this.current = undefined;
        process?.kill();
    }

    private launch(): void {
        const solutionPath = this.solutionPath;
        if (solutionPath === undefined) {
            return;
        }

        const process = this.spawner.spawn(this.exePath, ["--solution", solutionPath]);
        this.current = process;
        process.onExit(() => {
            // Only the helper we are still tracking is restarted; a stop has already cleared it.
            if (this.current !== process) {
                return;
            }

            this.current = undefined;
            if (this.restarts < this.maxRestarts) {
                this.restarts++;
                this.launch();
            } else {
                this.solutionPath = undefined;
            }
        });
    }
}
