import * as net from "node:net";

/** One helper's manifest, as the helper writes it to %ProgramData%\AiDevHelpers\roslyn-helpers\<pid>.json. */
export interface HelperManifest {
    pid: number;
    pipeName: string;
    solutionPath: string;
}

/** The framework a helper loads: net10 for SDK-style projects, net48 for legacy and net48 projects. */
export type RoslynFramework = "net10" | "net48";

/** Sends one request line to a named pipe and returns the one reply line. */
export interface PipeTransport {
    send(pipeName: string, line: string): Promise<string>;
}

/**
 * The edit actions the Roslyn helper answers. A VS Code edit tool with one of these names goes to the helper; the rest stay on
 * the VS Code bridge. Keep in step with the helper's dispatcher (the helper's VsParityTests check the VS side).
 */
export const ROSLYN_ACTIONS: ReadonlySet<string> = new Set([
    "addEnumMember", "addInterfaceMember", "addMember", "addMethod", "addOverload", "addType", "changeSignature",
    "deleteMember", "deleteType", "editEnumMember", "editMember", "editMethod", "editType", "generateConstructor",
    "implementMembers", "listUnused", "moveType", "renameNamespace", "renameSymbol", "tddAddTest", "tddUpdateTest",
    "threadParameter",
]);

/** Routes an edit action to the helper that loads the solution. Without a running helper, the error says so and nothing is sent. */
export class RoslynHelperClient {
    constructor(
        private readonly findHelper: (framework: RoslynFramework) => HelperManifest | undefined,
        private readonly transport: PipeTransport,
    ) {}

    /** framework is the framework of the project the action touches: net10 (default) or net48. */
    async runAction(action: string, args: Record<string, unknown>, framework: RoslynFramework = "net10"): Promise<unknown> {
        const helper = this.findHelper(framework);
        if (helper === undefined) {
            throw new Error(`No ${framework} Roslyn helper is running for this solution, so '${action}' cannot run. Open the solution in VS Code to start it.`);
        }

        const line = await this.transport.send(helper.pipeName, JSON.stringify({ action, args }));
        const reply = JSON.parse(line) as { ok: boolean; result?: unknown; error?: string };
        if (!reply.ok) {
            throw new Error(reply.error ?? `The Roslyn helper failed '${action}'.`);
        }

        return reply.result;
    }
}

/** The real transport: one JSON line over the helper's named pipe, one reply line back. */
export const namedPipeTransport: PipeTransport = {
    send(pipeName, line) {
        return new Promise<string>((resolve, reject) => {
            const socket = net.createConnection(`\\\\.\\pipe\\${pipeName}`);
            let buffered = "";
            socket.on("connect", () => socket.write(`${line}\n`));
            socket.on("data", (chunk: Buffer) => {
                buffered += chunk.toString("utf8");
                const end = buffered.indexOf("\n");
                if (end >= 0) {
                    socket.end();
                    resolve(buffered.slice(0, end));
                }
            });
            socket.on("error", reject);
            socket.on("close", () => reject(new Error(`The Roslyn helper closed pipe ${pipeName} before it replied.`)));
        });
    },
};
