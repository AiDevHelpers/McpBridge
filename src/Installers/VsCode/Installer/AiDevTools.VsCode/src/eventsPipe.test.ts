import { EventEmitter } from "events";
import type * as net from "net";
import { afterEach, describe, expect, it, vi } from "vitest";

// The events pipe listens through net.createServer. The mock hands the connection handler to the test, so no real
// named pipe is opened and the test drives fake sockets.
const hooks = vi.hoisted(() => ({ onConnection: undefined as ((socket: unknown) => void) | undefined }));

vi.mock("net", () => ({
    createServer: (handler: (socket: unknown) => void) => {
        hooks.onConnection = handler;
        return { on: () => undefined, listen: () => undefined, close: () => undefined, listening: true };
    },
}));

const { EventsPipe, eventsPipeNameFor } = await import("./eventsPipe.js");
const { EventHub } = await import("./vendor/core/index.js");

/** A stand-in for a client connection: records what the pipe writes and lets the test send lines. */
class FakeSocket extends EventEmitter {
    readonly written: string[] = [];
    readonly ended: string[] = [];
    destroyed = false;
    setEncoding(): void {
        // Data is fed as strings already.
    }
    write(chunk: string): boolean {
        this.written.push(chunk);
        return true;
    }
    end(chunk?: string): void {
        if (chunk !== undefined) this.ended.push(chunk);
        this.destroyed = true;
        this.emit("close");
    }
    destroy(): void {
        this.destroyed = true;
        this.emit("close");
    }
    send(line: string): void {
        this.emit("data", line);
    }
}

function connect(): FakeSocket {
    const socket = new FakeSocket();
    if (!hooks.onConnection) throw new Error("the pipe did not start");
    hooks.onConnection(socket as unknown as net.Socket);
    return socket;
}

const PID = 4242;
let pipe: InstanceType<typeof EventsPipe> | undefined;

afterEach(() => {
    pipe?.dispose();
    pipe = undefined;
});

describe("eventsPipe", () => {
    it("names the pipe AiDevHelpers.VsCode.<pid>.events", () => {
        expect(eventsPipeNameFor(PID)).toBe("AiDevHelpers.VsCode.4242.events");
    });

    it("subscribe line returns ok with a subscriptionId, then streams events from seq 1", () => {
        const hub = new EventHub();
        pipe = new EventsPipe(hub, PID);
        pipe.start();
        const socket = connect();

        socket.send(JSON.stringify({ action: "subscribe", args: { events: ["buildDone"] } }) + "\n");

        const reply = JSON.parse(socket.written[0]) as { ok: boolean; pid: number; subscriptionId: string };
        expect(reply.ok).toBe(true);
        expect(reply.pid).toBe(PID);
        expect(reply.subscriptionId.length).toBeGreaterThan(0);
        expect(hub.count).toBe(1);

        hub.publish("documentSaved", { path: "a.ts" });
        hub.publish("buildDone", { name: "build" });

        expect(socket.written).toHaveLength(2);
        expect(JSON.parse(socket.written[1])).toEqual({ event: "buildDone", seq: 1, data: { name: "build" } });
    });

    it("bad subscribe line returns ok false and ends the connection", () => {
        const hub = new EventHub();
        pipe = new EventsPipe(hub, PID);
        pipe.start();
        const socket = connect();

        socket.send("not json\n");

        const reply = JSON.parse(socket.ended[0]) as { ok: boolean; pid: number; error: string };
        expect(reply.ok).toBe(false);
        expect(reply.pid).toBe(PID);
        expect(typeof reply.error).toBe("string");
        expect(hub.count).toBe(0);
    });

    it("a line that is not a subscribe request returns ok false", () => {
        const hub = new EventHub();
        pipe = new EventsPipe(hub, PID);
        pipe.start();
        const socket = connect();

        socket.send(JSON.stringify({ action: "buildSolution" }) + "\n");

        expect(JSON.parse(socket.ended[0])).toMatchObject({ ok: false, pid: PID });
        expect(hub.count).toBe(0);
    });

    it("dispose closes every subscriber with bridgeStopped", () => {
        const hub = new EventHub();
        pipe = new EventsPipe(hub, PID);
        pipe.start();
        const socket = connect();
        socket.send(JSON.stringify({ action: "subscribe" }) + "\n");

        pipe.dispose();
        pipe = undefined;

        expect(JSON.parse(socket.written[socket.written.length - 1])).toEqual({ event: "closed", reason: "bridgeStopped" });
        expect(hub.count).toBe(0);
    });
});
